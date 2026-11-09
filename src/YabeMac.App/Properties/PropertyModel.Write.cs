using Yabe;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Linq;
using System.Reflection;

namespace YabeMac.PropertyPanel
{
    public static partial class PropertyModel
    {
        /// <summary>AppSettings.DefaultWritePriority ("LOWEST_AND_DEFAULT", "MANUAL_OPERATOR" ...) as number.</summary>
        public static uint DefaultWritePriority()
        {
            var s = AppSettings.Default.DefaultWritePriority;
            if (Enum.TryParse<BacnetWritePriority>(s, true, out var p)) return (uint)p;
            if (uint.TryParse(s, out var u) && u <= 16) return u;
            return 16;
        }

        // ================================================================================================
        // Writing  (m_DataGrid_PropertyValueChanged)
        // ================================================================================================

        /// <summary>
        /// Writes the new value of a node (the node's parsed value; null = NULL / relinquish).
        /// Members of structures are folded back into their structure; array elements are written by array index
        /// (priority array entries become a Present_Value write at that priority), lists are rewritten as a whole.
        /// </summary>
        public static WriteOutcome Write(BACnetDevice device, BacnetObjectId obj, PropertyNode node, object newValue)
        {
            try
            {
                var cur = node; object val = newValue;
                while (cur.Parent != null && !cur.Parent.IsArray && cur.Member != null)
                {
                    var p = cur.Parent;
                    var m = MembersOf(p.Value.GetType()).First(x => x.Info == cur.Member);
                    m.Set(p.Value, val);
                    val = p.Value; cur = p;
                }

                uint prio = DefaultWritePriority();
                if (cur.Parent == null)
                {
                    var bv = MakeValue(cur, val);
                    return DoWrite(device, obj, (BacnetPropertyIds)cur.PropertyId, new[] { bv }, ASN1.BACNET_ARRAY_ALL, prio);
                }

                var root = cur.Parent;          // array / list
                if (root.PropertyId == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY)
                {
                    // like the BACnet standard: Priority_Array is written through Present_Value with the priority
                    var bv = MakeValue(cur, val);
                    return DoWrite(device, obj, BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { bv }, ASN1.BACNET_ARRAY_ALL, (uint)(cur.ElementIndex + 1));
                }
                if (root.IsList)
                    return WriteWholeList(device, obj, root, cur, val, prio);

                var one = MakeValue(cur, val);
                var r = DoWrite(device, obj, (BacnetPropertyIds)root.PropertyId, new[] { one }, (uint)(cur.ElementIndex + 1), prio);
                if (!r.Ok && r.Error != null && r.Error.Contains("PROPERTY_IS_NOT_AN_ARRAY"))
                    return WriteWholeList(device, obj, root, cur, val, prio);    // really a list: rewrite it
                return r;
            }
            catch (Exception ex) { return WriteOutcome.Fail("Couldn't convert property: " + ex.Message); }
        }

        /// <summary>Write NULL (relinquish for Present_Value / a priority array entry).</summary>
        public static WriteOutcome Relinquish(BACnetDevice device, BacnetObjectId obj, PropertyNode node) => Write(device, obj, node, null);

        static WriteOutcome WriteWholeList(BACnetDevice device, BacnetObjectId obj, PropertyNode root, PropertyNode element, object val, uint prio)
        {
            var values = new List<BacnetValue>();
            for (int i = 0; i < root.Raw.Count; i++)
                values.Add(i == element.ElementIndex ? MakeValue(element, val) : root.Raw[i]);
            return DoWrite(device, obj, (BacnetPropertyIds)root.PropertyId, values, ASN1.BACNET_ARRAY_ALL, prio);
        }

        /// <summary>The BacnetValue to send, keeping the application tag the device used when we read it.</summary>
        static BacnetValue MakeValue(PropertyNode n, object val)
        {
            if (val == null) return new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
            if (val is DateTimeParts dtp) return dtp.ToBacnetValue();      // Date / Time / DateTime, possibly with wildcards
            var tag = n.Tag;
            if (tag == null || tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL)
            {
                tag = InferTag(n.ObjectType);
                if (tag == null) return new BacnetValue(val);          // guesses the tag from the CLR type
            }
            // a Real node holding a double etc. is converted by the encoder (Convert.ToXxx)
            return new BacnetValue(tag.Value, val);
        }

        static WriteOutcome DoWrite(BACnetDevice device, BacnetObjectId obj, BacnetPropertyIds prop, IEnumerable<BacnetValue> values, uint arrayIndex, uint priority)
        {
            lock (device.channel)
            {
                uint old = device.channel.WritePriority;
                try
                {
                    device.channel.WritePriority = priority;
                    bool ok = device.channel.WritePropertyRequest(device.BacAdr, obj, prop, values, 0, arrayIndex);
                    return ok ? WriteOutcome.Success() : WriteOutcome.Fail("Couldn't write property");
                }
                catch (Exception ex) { return WriteOutcome.Fail("Error during write: " + Friendly(ex)); }
                finally { device.channel.WritePriority = old; }
            }
        }

        /// <summary>"Error from device: ERROR_CLASS_PROPERTY - ERROR_CODE_WRITE_ACCESS_DENIED" -> readable text.</summary>
        public static string Friendly(Exception ex)
        {
            string m = ex.Message;
            const string pre = "Error from device: ";
            if (m.StartsWith(pre))
            {
                var rest = m.Substring(pre.Length).Split(new[] { " - " }, StringSplitOptions.None);
                if (rest.Length == 2) return TitleCase(rest[0].Replace("ERROR_CLASS_", "").Replace('_', ' ')) + ": " + TitleCase(rest[1].Replace("ERROR_CODE_", "").Replace('_', ' ')) + "  (" + rest[0] + " - " + rest[1] + ")";
            }
            return m;
        }

        // ---- list elements ------------------------------------------------------------------------------------
        /// <summary>Builds the BacnetValue for a new list element from the text typed by the user, typed like the existing elements.</summary>
        public static bool TryMakeElement(PropertyNode list, string text, out BacnetValue element, out string error)
        {
            element = default; error = null;
            var kids = list.Children;
            var tpl = kids.FirstOrDefault(k => k.Value != null) ?? kids.FirstOrDefault();
            if (tpl == null) { error = "The list is empty: cannot infer the type of a new element."; return false; }
            if (tpl.Kind == EditKind.Composite)
            {
                // structure: duplicate the template (edit its members afterwards)
                var copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(tpl.Value, null);
                element = new BacnetValue(tpl.Tag ?? BacnetApplicationTags.BACNET_APPLICATION_TAG_CONTEXT_SPECIFIC_ENCODED, copy);
                return true;
            }
            if (!TryParse(tpl, text, out var v, out error)) return false;
            element = MakeValue(tpl, v);
            return true;
        }

        public static WriteOutcome AddListElement(BACnetDevice device, BacnetObjectId obj, PropertyNode list, BacnetValue element)
        {
            try
            {
                bool ok = device.channel.AddListElementRequest(device.BacAdr, obj, new BacnetPropertyReference(list.PropertyId, ASN1.BACNET_ARRAY_ALL), new[] { element });
                return ok ? WriteOutcome.Success() : WriteOutcome.Fail("Couldn't add the list element");
            }
            catch (Exception ex) { return WriteOutcome.Fail("Error adding list element: " + Friendly(ex)); }
        }

        public static WriteOutcome RemoveListElement(BACnetDevice device, BacnetObjectId obj, PropertyNode element)
        {
            try
            {
                var list = element.Parent;
                var bv = MakeValue(element, element.Value);
                bool ok = device.channel.RemoveListElementRequest(device.BacAdr, obj, new BacnetPropertyReference(list.PropertyId, ASN1.BACNET_ARRAY_ALL), new[] { bv });
                return ok ? WriteOutcome.Success() : WriteOutcome.Fail("Couldn't remove the list element");
            }
            catch (Exception ex) { return WriteOutcome.Fail("Error removing list element: " + Friendly(ex)); }
        }

        // ---- what the UI may offer for a node ---------------------------------------------------------------------
        public static bool CanEdit(PropertyNode n) => !n.ReadOnly && n.Kind != EditKind.ReadOnly && n.Kind != EditKind.Composite && n.Kind != EditKind.Null && !n.IsArray;
        public static bool CanRelinquish(PropertyNode n) =>
            (n.IsRoot && n.PropertyId == (uint)BacnetPropertyIds.PROP_PRESENT_VALUE && !n.ReadOnly) ||
            (n.Parent != null && n.Parent.PropertyId == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY && n.Value != null);
        public static bool CanAddElement(PropertyNode n) { var l = n.IsArray && n.IsRoot ? n : n.Parent; return l != null && l.IsRoot && l.IsList && !l.ReadOnly; }
        public static bool CanRemoveElement(PropertyNode n) => n.Parent != null && n.Parent.IsRoot && n.Parent.IsList && !n.Parent.ReadOnly;
    }
}
