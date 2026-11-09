using Yabe;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Linq;
using System.Net;

namespace YabeMac.PropertyPanel
{
    public sealed class ReadOptions
    {
        public bool Simplified;                       // apply SimplifiedViewFilter.xml
        public bool ShowIds;                          // Show_Property_Id_Numbers
        public int ArrayExpandMaxSize = 10;           // GridArrayExpandMaxSize
        public string[] AlwaysExpand = new string[0]; // GridAlwaysExpandProperties

        public static ReadOptions FromSettings(bool simplifiedToggle)
        {
            var s = AppSettings.Default;
            // the original: (view mode == FieldTechnician) XOR toggle
            bool simpl = (s.Address_Space_Structured_View == "FieldTechnician") ^ simplifiedToggle;
            return new ReadOptions
            {
                Simplified = simpl,
                ShowIds = s.Show_Property_Id_Numbers,
                ArrayExpandMaxSize = s.GridArrayExpandMaxSize,
                AlwaysExpand = string.IsNullOrEmpty(s.GridAlwaysExpandProperties) ? new string[0]
                    : s.GridAlwaysExpandProperties.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray(),
            };
        }
    }

    public static partial class PropertyModel
    {
        sealed class Ctx
        {
            public BACnetDevice Device;
            public BacnetObjectId Obj;
            public ReadOptions Opt;
            public string[] StateText;
        }

        // ================================================================================================
        // Reading  (MainDialog.UpdateGrid)
        // ================================================================================================

        public static PropertyReadResult Read(BACnetDevice device, BacnetObjectId id, ReadOptions opt)
        {
            opt = opt ?? ReadOptions.FromSettings(false);
            var res = new PropertyReadResult();
            IList<BacnetReadAccessResult> list = null;
            try
            {
                var props = new[] { new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_ALL, ASN1.BACNET_ARRAY_ALL) };
                device.ReadPropertyMultipleRequest(id, props, out list);
            }
            catch (Exception ex) { res.Error = "ReadPropertyMultiple failed: " + ex.Message; return res; }
            if (list == null || list.Count == 0) { res.Error = "Couldn't perform ReadProperties"; return res; }

            List<BacnetPropertyIds> filter = null;
            if (opt.Simplified) filter = SimplifiedFilter.PropertiesFor(id.type);

            var ctx = new Ctx { Device = device, Obj = id, Opt = opt };
            res.Simplified = filter != null;

            // state text first: multistate present values get named choices
            foreach (var pv in list[0].values)
                if (pv.property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_STATE_TEXT && pv.value != null)
                    ctx.StateText = pv.value.Select(v => v.Value as string ?? "").ToArray();

            foreach (var pv in list[0].values)
            {
                var pid = (BacnetPropertyIds)pv.property.propertyIdentifier;
                if (filter != null && !filter.Exists(o => o == pid) && !filter.Exists(o => o == BacnetPropertyIds.MAX_BACNET_PROPERTY_ID))
                    continue;

                var node = BuildRoot(ctx, pv);
                res.Nodes.Add(node);

                object first = pv.value != null && pv.value.Count > 0 ? pv.value[0].Value : null;
                switch (pid)
                {
                    case BacnetPropertyIds.PROP_SEGMENTATION_SUPPORTED:
                        try { if (first != null) device.Segmentation = (BacnetSegmentations)Convert.ToUInt32(first); } catch { }
                        break;
                    case BacnetPropertyIds.PROP_MAX_APDU_LENGTH_ACCEPTED:
                        try { if (first != null) device.MaxAPDULenght = Convert.ToUInt32(first); } catch { }
                        break;
                    case BacnetPropertyIds.PROP_ACKED_TRANSITIONS:
                        if (first is BacnetBitString acked)
                            for (byte b = 0; b < 3; b++) if (!acked.GetBit(b)) res.ShowAlarmAck[b] = true;
                        break;
                    case BacnetPropertyIds.PROP_OBJECT_NAME:
                        if (first != null) res.ObjectName = first.ToString();
                        break;
                }
            }

            ApplyDefaultExpansion(res.Nodes, opt);
            return res;
        }

        static void ApplyDefaultExpansion(List<PropertyNode> roots, ReadOptions opt)
        {
            if (opt.ArrayExpandMaxSize <= 1 && opt.AlwaysExpand.Length == 0) return;
            foreach (var n in roots)
            {
                if (!n.IsArray || n.Value is not Array arr || arr.Length < 2) continue;   // 1 element arrays are not expandable in the original
                if (arr.Length <= opt.ArrayExpandMaxSize) { n.Expanded = true; continue; }
                string label = n.Name;
                if (opt.ShowIds && label.Contains(":")) label = label.Split(':')[1].Trim();
                if (opt.AlwaysExpand.Any(p => label.Equals(p, StringComparison.OrdinalIgnoreCase))) n.Expanded = true;
            }
        }

        /// <summary>Marks values that differ from the previous read (used to highlight changes on refresh).</summary>
        public static void MarkChanges(IList<PropertyNode> previous, IList<PropertyNode> current)
        {
            if (previous == null) return;
            var old = previous.ToDictionary(p => p.PathKey, p => p, StringComparer.Ordinal);
            foreach (var n in current)
            {
                if (!old.TryGetValue(n.PathKey, out var o)) continue;
                if (Signature(o) == Signature(n)) continue;
                n.Changed = true;
                if (n.IsArray && n.Raw != null && o.Raw != null && n.Raw.Count == o.Raw.Count && n.Raw.Count <= 64)
                {
                    var kids = n.Children;
                    for (int i = 0; i < kids.Count; i++)
                        if (FormatObject(n.Raw[i].Value) != FormatObject(o.Raw[i].Value)) kids[i].Changed = true;
                }
            }
        }

        static string Signature(PropertyNode n)
        {
            if (n.Raw == null) return FormatObject(n.Value);
            var sb = new System.Text.StringBuilder();
            foreach (var v in n.Raw) sb.Append(FormatObject(v.Value, v.Tag)).Append('\u0001');
            return sb.ToString();
        }

        // ================================================================================================
        // Node construction
        // ================================================================================================

        static PropertyNode BuildRoot(Ctx c, BacnetPropertyValue pv)
        {
            uint pid = pv.property.propertyIdentifier;
            var raw = Unwrap(pv.value ?? new List<BacnetValue>());
            var n = new PropertyNode
            {
                Name = c.Device.GetNiceName((BacnetPropertyIds)pid, c.Opt.ShowIds),
                PropertyId = pid, ObjectType = c.Obj.type, Raw = raw, PathKey = pid.ToString(),
            };
            bool listProp = IsListProperty(pid), arrayProp = IsArrayProperty(pid);
            n.IsList = listProp;
            n.ReadOnly = IsReadOnlyProperty(pid)
                         || pid == (uint)BacnetPropertyIds.PROP_WEEKLY_SCHEDULE || pid == (uint)BacnetPropertyIds.PROP_EXCEPTION_SCHEDULE;

            if (raw.Count == 1 && raw[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR)
            {
                n.IsErrorValue = true; n.ReadOnly = true; n.Value = raw[0].Value; n.Tag = raw[0].Tag; n.Kind = EditKind.ReadOnly;
                n.ClrType = typeof(BacnetError);
                n.Display = FormatObject(n.Value);
                return n;
            }

            // a Date followed by a Time of a single-valued property is one BACnetDateTime
            if (!listProp && !arrayProp && raw.Count == 2 && raw[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE && raw[1].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME
                && raw[0].Value is DateTime cd && raw[1].Value is DateTime ct)
                n.Raw = raw = new List<BacnetValue> { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_DATETIME, new DateTime(cd.Year, cd.Month, cd.Day, ct.Hour, ct.Minute, ct.Second, ct.Millisecond)) };

            bool isArray = raw.Count > 1 || ((listProp || arrayProp) && raw.Count != 1) || ((listProp || arrayProp) && raw.Count == 1);
            if (isArray)
            {
                n.IsArray = true;
                n.Kind = EditKind.ReadOnly;
                n.Value = raw.Select(v => v.Value).ToArray();
                n.ClrType = typeof(object[]);
                n._childBuilder = node => BuildArrayChildren(c, node);
            }
            else
            {
                n.Value = raw.Count == 1 ? raw[0].Value : null;
                n.Tag = raw.Count == 1 ? raw[0].Tag : (BacnetApplicationTags?)null;
                Configure(c, n, null, true, null);
                if (n.PropertyId == (uint)BacnetPropertyIds.PROP_PRESENT_VALUE) n.Nullable = true;
            }
            if (!isArray && n.Value is DateTime && (n.Kind == EditKind.Date || n.Kind == EditKind.Time || n.Kind == EditKind.DateTime))
                n.Parts = DateTimeParts.ReadRaw(c.Device, c.Obj, pid) ?? DateTimeParts.FromDateTime((DateTime)n.Value, n.Tag);
            n.Display = FormatValue(n);
            return n;
        }

        /// <summary>Some decoders return a BacnetValue inside a BacnetValue (context specific data): show the inner value.</summary>
        static IList<BacnetValue> Unwrap(IList<BacnetValue> raw)
        {
            if (!raw.Any(v => v.Value is BacnetValue)) return raw;
            return raw.Select(v => v.Value is BacnetValue inner ? inner : v).ToList();
        }

        static List<PropertyNode> BuildArrayChildren(Ctx c, PropertyNode arr)
        {
            var kids = new List<PropertyNode>();
            var raw = arr.Raw;
            // first non null element is the template for the editor of empty (null) entries
            int ti = -1;
            for (int i = 0; i < raw.Count; i++) if (raw[i].Value != null) { ti = i; break; }
            PropertyNode template = null;
            for (int i = 0; i < raw.Count; i++)
            {
                var k = new PropertyNode
                {
                    Parent = arr, Name = ElementName(arr.PropertyId, i), ElementIndex = i, PropertyId = arr.PropertyId,
                    ObjectType = arr.ObjectType, Depth = arr.Depth + 1, PathKey = arr.PathKey + "/" + i,
                    Value = raw[i].Value, Tag = raw[i].Tag, ReadOnly = arr.ReadOnly,
                };
                Configure(c, k, null, true, template);
                if (i == ti) template = k;
                if (arr.PropertyId == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY) k.Nullable = true;
                k.Display = FormatValue(k);
                kids.Add(k);
                // elements before the first non-null one: configure again once we know the template
                if (i == ti)
                    for (int j = 0; j < ti; j++)
                    {
                        Configure(c, kids[j], null, true, template);
                        if (arr.PropertyId == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY) kids[j].Nullable = true;
                        kids[j].Display = FormatValue(kids[j]);
                    }
            }
            return kids;
        }

        static List<PropertyNode> BuildCompositeChildren(Ctx c, PropertyNode parent)
        {
            var kids = new List<PropertyNode>();
            if (parent.Value == null) return kids;
            if (parent.Depth > 6) return kids;
            var v = parent.Value;
            if (v is IList list && !(v is byte[]))
            {
                int i = 0;
                foreach (var e in list)
                {
                    var k = NewChild(parent, "[" + i + "]", e, e?.GetType(), true);
                    k.ElementIndex = i;
                    Configure(c, k, e?.GetType(), false, null);
                    k.ReadOnly = true;
                    k.Display = FormatValue(k);
                    kids.Add(k); i++;
                }
                return kids;
            }
            foreach (var m in MembersOf(v.GetType()))
            {
                object mv;
                try { mv = m.Get(v); } catch { continue; }
                var k = NewChild(parent, m.Name, mv, m.Type, m.CanWrite && !parent.ReadOnly);
                k.Member = m.Info;
                Configure(c, k, m.Type, false, null);
                if (!m.CanWrite) k.ReadOnly = true;
                if (k.ReadOnly && k.Kind != EditKind.Composite) k.Kind = k.Kind;   // keep kind (for formatting) but flagged read only
                k.Display = FormatValue(k);
                kids.Add(k);
            }
            return kids;
        }

        static PropertyNode NewChild(PropertyNode parent, string name, object value, Type t, bool writable)
        {
            return new PropertyNode
            {
                Parent = parent, Name = name, Value = value, ClrType = t, PropertyId = parent.PropertyId, ObjectType = parent.ObjectType,
                Depth = parent.Depth + 1, PathKey = parent.PathKey + "/" + name, ReadOnly = !writable || parent.ReadOnly,
            };
        }

        // ---- decide the editor of a value -----------------------------------------------------------------------
        static BacnetApplicationTags? InferTag(BacnetObjectTypes type)
        {
            switch (type.ToString())
            {
                case "OBJECT_ANALOG_INPUT": case "OBJECT_ANALOG_OUTPUT": case "OBJECT_ANALOG_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL;
                case "OBJECT_LARGE_ANALOG_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE;
                case "OBJECT_BINARY_INPUT": case "OBJECT_BINARY_OUTPUT": case "OBJECT_BINARY_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED;
                case "OBJECT_MULTI_STATE_INPUT": case "OBJECT_MULTI_STATE_OUTPUT": case "OBJECT_MULTI_STATE_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT;
                case "OBJECT_POSITIVE_INTEGER_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT;
                case "OBJECT_INTEGER_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT;
                case "OBJECT_CHARACTERSTRING_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING;
                case "OBJECT_OCTETSTRING_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_OCTET_STRING;
                case "OBJECT_BITSTRING_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING;
                case "OBJECT_DATE_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE;
                case "OBJECT_TIME_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME;
                case "OBJECT_DATETIME_VALUE": return BacnetApplicationTags.BACNET_APPLICATION_TAG_DATETIME;
            }
            return null;
        }

        static bool IsBinary(BacnetObjectTypes t) => t == BacnetObjectTypes.OBJECT_BINARY_INPUT || t == BacnetObjectTypes.OBJECT_BINARY_OUTPUT || t == BacnetObjectTypes.OBJECT_BINARY_VALUE;
        static bool IsMultistate(BacnetObjectTypes t) => t == BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT || t == BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT || t == BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE;

        static bool IsPresentValueLike(uint pid) =>
            pid == (uint)BacnetPropertyIds.PROP_PRESENT_VALUE || pid == (uint)BacnetPropertyIds.PROP_RELINQUISH_DEFAULT ||
            pid == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY || pid == (uint)BacnetPropertyIds.PROP_FEEDBACK_VALUE;

        /// <summary>
        /// Chooses kind / choices / CLR type for a node. propLevel: the node is a root property or an element of one
        /// (so that property based enumerations and bit names apply).
        /// </summary>
        static void Configure(Ctx c, PropertyNode n, Type clr, bool propLevel, PropertyNode template)
        {
            var prop = (BacnetPropertyIds)n.PropertyId;
            object v = n.Value;
            var tag = n.Tag;
            Type t = clr ?? v?.GetType();

            // value missing: borrow what we know from a sibling or the object type
            if (v == null && propLevel)
            {
                if (template != null)
                {
                    n.Tag = template.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL ? template.Tag : n.Tag;
                    n.ClrType = template.ClrType; n.Kind = template.ReadOnly && template.Kind != EditKind.Composite ? template.Kind : template.Kind;
                    n.Choices = template.Choices; n.EnumType = template.EnumType; n.LinearBits = template.LinearBits;
                    if (template.Kind == EditKind.Composite) { n.Kind = EditKind.ReadOnly; n.ReadOnly = true; }
                    return;
                }
                var inf = IsPresentValueLike(n.PropertyId) ? InferTag(n.ObjectType) : null;
                if (inf != null) { tag = inf; n.Tag = (n.Tag == null || n.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL) ? inf : n.Tag; }
                else if (t == null) { n.Kind = n.ReadOnly ? EditKind.ReadOnly : EditKind.Null; return; }
                t = TypeOfTag(tag);
                if (t == null) { n.Kind = n.ReadOnly ? EditKind.ReadOnly : EditKind.Null; return; }
            }
            n.ClrType = n.ClrType ?? t;
            if (t == null) { n.Kind = EditKind.ReadOnly; return; }

            // -- containers inside structures
            if (v is IList && !(v is byte[]) && !propLevel)
            {
                n.IsArray = true; n.Kind = EditKind.ReadOnly; n.ReadOnly = true;
                n.Raw = null;
                n._childBuilder = node => BuildCompositeChildren(c, node);
                return;
            }

            // -- structures
            if (v != null && IsComposite(t) && !(v is IList))
            {
                n.Kind = EditKind.Composite;
                n._childBuilder = node => BuildCompositeChildren(c, node);
                return;
            }

            // -- property based enumerations / bit strings
            var ut = Nullable.GetUnderlyingType(t) ?? t;
            if (propLevel)
            {
                if (v == null || v is uint || v is ulong)
                {
                    if (n.Parent == null && EnumByProperty.TryGetValue(prop, out var et)) { SetChoice(n, et); return; }
                    if (n.Parent == null && prop == BacnetPropertyIds.PROP_ACTION && v != null && tag != BacnetApplicationTags.BACNET_APPLICATION_CONTEXT_SPECIFIC)
                    { SetChoice(n, typeof(BACnetAction)); return; }
                    if (IsPresentValueLike(n.PropertyId) && IsBinary(n.ObjectType) && (tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED))
                    { SetChoice(n, typeof(BACnetBinaryPV)); return; }
                    if (IsPresentValueLike(n.PropertyId) && IsMultistate(n.ObjectType) && c.StateText != null && c.StateText.Length > 0
                        && (tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT || v == null))
                    {
                        n.Kind = EditKind.Choice;
                        n.Choices = c.StateText.Select((s, i) => new Choice { Value = (uint)(i + 1), Label = (i + 1) + " : " + s }).ToList();
                        n.ClrType = typeof(ulong);
                        return;
                    }
                }
                if (v is BacnetBitString && BitsByProperty.TryGetValue(prop, out var bi))
                {
                    n.Kind = EditKind.BitString; n.EnumType = bi.EnumType; n.LinearBits = bi.Linear;
                    return;
                }
            }

            // -- generic, by CLR type (and BACnet tag for date/time)
            n.Kind = KindOfType(ut, tag);
            if (n.Kind == EditKind.Choice && ut.IsEnum) { n.EnumType = ut; n.Choices = ChoicesOf(ut); }
        }

        static void SetChoice(PropertyNode n, Type enumType)
        {
            n.Kind = EditKind.Choice; n.EnumType = enumType; n.Choices = ChoicesOf(enumType);
            if (n.ClrType == null || n.Value == null) n.ClrType = n.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT ? typeof(ulong) : typeof(uint);
        }

        public static EditKind KindOfType(Type t, BacnetApplicationTags? tag)
        {
            if (t == typeof(float)) return EditKind.Real;
            if (t == typeof(double)) return EditKind.Double;
            if (t == typeof(bool)) return EditKind.Boolean;
            if (t == typeof(ulong) || t == typeof(uint) || t == typeof(ushort) || t == typeof(byte)) return EditKind.Unsigned;
            if (t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(sbyte)) return EditKind.Signed;
            if (t == typeof(string)) return EditKind.Text;
            if (t.IsEnum) return EditKind.Choice;
            if (t == typeof(DateTime))
            {
                if (tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME) return EditKind.Time;
                if (tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE) return EditKind.Date;
                return EditKind.DateTime;
            }
            if (t == typeof(BacnetObjectId)) return EditKind.ObjectId;
            if (t == typeof(BacnetBitString)) return EditKind.BitString;
            if (t == typeof(byte[])) return EditKind.Octets;
            if (t == typeof(IPAddress)) return EditKind.IpAddress;
            return EditKind.ReadOnly;
        }

        static Type TypeOfTag(BacnetApplicationTags? tag)
        {
            switch (tag)
            {
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL: return typeof(float);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE: return typeof(double);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT: return typeof(ulong);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT: return typeof(long);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED: return typeof(uint);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN: return typeof(bool);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING: return typeof(string);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_OCTET_STRING: return typeof(byte[]);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING: return typeof(BacnetBitString);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID: return typeof(BacnetObjectId);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE:
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME:
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_DATETIME: return typeof(DateTime);
            }
            return null;
        }
    }
}
