using Yabe;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO.BACnet;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace YabeMac.PropertyPanel
{
    public static partial class PropertyModel
    {
        /// <summary>Original setting ByteArray_PreferHex (set from AppSettings by the window / tests).</summary>
        public static bool ByteArrayPreferHex => AppSettings.Default.ByteArray_PreferHex;

        // ================================================================================================
        // Formatting
        // ================================================================================================

        public static string FormatValue(PropertyNode n)
        {
            if (n.IsArray)
            {
                int c = n._children?.Count ?? (n.Value is Array a ? a.Length : 0);
                return c == 0 ? "[empty]" : "[" + c + (c == 1 ? " item]" : " items]");
            }
            if (n.IsErrorValue) return Convert.ToString(n.Value, CultureInfo.InvariantCulture);
            if (n.Value == null)
                return "";
            if (n.Parts != null) return n.Parts.Format();
            switch (n.Kind)
            {
                case EditKind.Choice:
                    if (n.Choices != null && TryUInt(n.Value, out uint cv))
                    {
                        var ch = n.Choices.FirstOrDefault(c => c.Value == cv);
                        if (ch != null) return ch.Label;
                        if (n.EnumType != null) return ChoiceLabel(n.EnumType, cv);
                        return cv.ToString();
                    }
                    break;
                case EditKind.BitString:
                    if (n.Value is BacnetBitString bs) return FormatBitString(bs, n.EnumType, n.LinearBits);
                    break;
            }
            return FormatObject(n.Value, n.Tag, n.EnumType, ChoiceFormatting: false);
        }

        static bool TryUInt(object v, out uint u)
        {
            u = 0;
            try
            {
                if (v is Enum) { u = Convert.ToUInt32(v); return true; }
                if (v is ulong ul) { u = (uint)ul; return ul <= uint.MaxValue; }
                if (v is uint x) { u = x; return true; }
                if (v is int i && i >= 0) { u = (uint)i; return true; }
                if (v is long l && l >= 0 && l <= uint.MaxValue) { u = (uint)l; return true; }
                if (v is ushort us) { u = us; return true; }
                if (v is byte b) { u = b; return true; }
            }
            catch { }
            return false;
        }

        public static string FormatBitString(BacnetBitString bs, Type enumType, bool linear)
        {
            string bits = bs.ToString();
            if (enumType == null) return bits;
            var sb = new StringBuilder();
            for (int i = 0; i < bits.Length; i++)
            {
                if (bits[i] != '1') continue;
                string name = null;
                try { name = Enum.GetName(enumType, linear ? (object)i : (object)(1 << i)); } catch { }
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name != null ? BitNiceName(name) : "Bit " + i);
            }
            return sb.ToString();
        }

        /// <summary>Generic value to text (what the PropertyGrid default converters produced).</summary>
        public static string FormatObject(object v, BacnetApplicationTags? tag = null, Type enumType = null, bool ChoiceFormatting = false)
        {
            switch (v)
            {
                case null: return "";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case bool b: return b ? "True" : "False";
                case string s: return s;
                case BacnetObjectId oid: return oid.type + ":" + oid.instance;
                case BacnetDeviceObjectPropertyReference r: return "Reference to " + r.objectIdentifier;
                case BacnetBitString bs: return bs.ToString();
                case byte[] ba: return FormatBytes(ba);
                case IPAddress ip: return ip.ToString();
                case DateTimeParts dtp: return dtp.Format();
                case DateTime dt:
                    if (tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME) return dt.ToLongTimeString();
                    if (tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE) return dt.ToShortDateString();
                    if (tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_DATETIME) return dt.ToString();
                    if (dt.Year == 1 && dt.Month == 1 && dt.Day == 1) return dt.ToLongTimeString();
                    if (dt.TimeOfDay == TimeSpan.Zero) return dt.ToShortDateString();
                    return dt.ToString();
                case BacnetGenericTime gt:
                    switch (gt.Tag)
                    {
                        case BacnetTimestampTags.TIME_STAMP_TIME: return gt.Time.ToLongTimeString();
                        case BacnetTimestampTags.TIME_STAMP_SEQUENCE: return "Sequence " + gt.Sequence;
                        case BacnetTimestampTags.TIME_STAMP_DATETIME: return gt.Time.ToString();
                        default: return "";
                    }
                case Enum e: return e.ToString();
                case IFormattable fm when !(v is Enum): return fm.ToString(null, CultureInfo.InvariantCulture);
            }
            string ts = v.ToString();
            var t = v.GetType();
            if (ts == t.ToString() || ts == t.FullName || ts == t.Name)
                return Summary(v);
            return ts;
        }

        public static string FormatBytes(byte[] values)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                if (ByteArrayPreferHex) sb.Append("0x").Append(values[i].ToString("X2"));
                else sb.Append(values[i]);
            }
            return sb.Append(']').ToString();
        }

        /// <summary>"{Member=value; ...}" for structures that have no meaningful ToString.</summary>
        static string Summary(object v)
        {
            var parts = new List<string>();
            foreach (var m in MembersOf(v.GetType()))
            {
                if (parts.Count >= 4) { parts.Add("..."); break; }
                try { parts.Add(m.Name + "=" + FormatObject(m.Get(v))); } catch { }
            }
            return "{" + string.Join("; ", parts) + "}";
        }

        // ================================================================================================
        // Parsing (text typed in the editor -> value of the node's CLR type)
        // ================================================================================================

        /// <summary>Returns false with an error message when the text cannot be converted. Empty text on a nullable node yields null.</summary>
        public static bool TryParse(PropertyNode n, string text, out object value, out string error)
        {
            value = null; error = null;
            text = text ?? "";
            Type target = Nullable.GetUnderlyingType(n.ClrType ?? typeof(object)) ?? n.ClrType;
            bool allowNull = n.Nullable || (n.ClrType != null && Nullable.GetUnderlyingType(n.ClrType) != null);
            if (text.Trim().Length == 0 && n.Kind != EditKind.Text && n.Kind != EditKind.Octets && n.Kind != EditKind.BitString)
            {
                if (allowNull) return true;
                error = "A value is required.";
                return false;
            }
            try
            {
                switch (n.Kind)
                {
                    case EditKind.Text: value = text; return true;
                    case EditKind.Boolean: return ParseBool(text, out value, out error);
                    case EditKind.Real:
                        {
                            if (!TryDouble(text, out double d)) { error = "Not a valid number."; return false; }
                            value = (float)d; return CoerceTo(target, value, out value, out error);
                        }
                    case EditKind.Double:
                        if (!TryDouble(text, out double dd)) { error = "Not a valid number."; return false; }
                        value = dd; return CoerceTo(target, value, out value, out error);
                    case EditKind.Unsigned:
                        {
                            string t = text.Trim();
                            ulong u;
                            bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                ? ulong.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u)
                                : ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out u);
                            if (!ok) { error = "Not a valid unsigned integer."; return false; }
                            return CoerceTo(target, u, out value, out error);
                        }
                    case EditKind.Signed:
                        {
                            if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) { error = "Not a valid integer."; return false; }
                            return CoerceTo(target, l, out value, out error);
                        }
                    case EditKind.Choice: return ParseChoice(n, text, target, out value, out error);
                    case EditKind.ObjectId:
                        if (!TryParseObjectId(text, out var oid)) { error = "Use TYPE:INSTANCE, e.g. OBJECT_ANALOG_INPUT:1 or AI:1."; return false; }
                        value = oid; return true;
                    case EditKind.Octets: return ParseBytes(text, out value, out error);
                    case EditKind.BitString:
                        {
                            string t = text.Trim();
                            if (t.Any(c => c != '0' && c != '1')) { error = "A bit string is a series of 0 and 1."; return false; }
                            value = BacnetBitString.Parse(t); return true;
                        }
                    case EditKind.Date:
                        if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt) &&
                            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) { error = "Not a valid date."; return false; }
                        value = dt.Date; return true;
                    case EditKind.Time:
                        if (!DateTime.TryParse("1/1/0001 " + text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var tm) &&
                            !DateTime.TryParse("1/1/0001 " + text, CultureInfo.CurrentCulture, DateTimeStyles.None, out tm)) { error = "Not a valid time (HH:mm:ss)."; return false; }
                        value = new DateTime(1, 1, 1, tm.Hour, tm.Minute, tm.Second, tm.Millisecond); return true;
                    case EditKind.DateTime:
                        if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dtt) &&
                            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dtt)) { error = "Not a valid date and time."; return false; }
                        value = dtt; return true;
                    case EditKind.IpAddress:
                        if (!IPAddress.TryParse(text.Trim(), out var ip)) { error = "Not a valid IP address."; return false; }
                        value = ip; return true;
                    default:
                        error = "This value is read only."; return false;
                }
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        static bool TryDouble(string text, out double d)
        {
            text = text.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return true;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out d);
        }

        static bool ParseBool(string text, out object value, out string error)
        {
            value = null; error = null;
            switch (text.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": case "active": value = true; return true;
                case "false": case "0": case "no": case "off": case "inactive": value = false; return true;
            }
            error = "Use True or False."; return false;
        }

        static bool CoerceTo(Type target, object v, out object result, out string error)
        {
            result = v; error = null;
            if (target == null || target == typeof(object) || target.IsInstanceOfType(v)) return true;
            try
            {
                if (target.IsEnum) { result = Enum.ToObject(target, v); return true; }
                result = Convert.ChangeType(v, target, CultureInfo.InvariantCulture);
                return true;
            }
            catch (OverflowException) { error = "Value out of range for " + target.Name + "."; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        static bool ParseChoice(PropertyNode n, string text, Type target, out object value, out string error)
        {
            value = null; error = null;
            string t = text.Trim();
            uint? found = null;
            // "3 : Name" or "3"
            int colon = t.IndexOf(':');
            string head = colon > 0 ? t.Substring(0, colon).Trim() : t;
            if (uint.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u)) found = u;
            else if (n.Choices != null)
            {
                string want = t.ToLowerInvariant();
                foreach (var c in n.Choices)
                {
                    string label = c.Label.Substring(c.Label.IndexOf(':') + 1).Trim().ToLowerInvariant();
                    if (label == want || c.Label.ToLowerInvariant() == want) { found = c.Value; break; }
                }
                if (found == null && n.EnumType != null)
                    foreach (var name in Enum.GetNames(n.EnumType))
                        if (name.Equals(t, StringComparison.OrdinalIgnoreCase)) { found = Convert.ToUInt32(Enum.Parse(n.EnumType, name)); break; }
            }
            if (found == null) { error = "Unknown value."; return false; }
            if (target != null && target.IsEnum) value = Enum.ToObject(target, found.Value);
            else if (target == typeof(ulong)) value = (ulong)found.Value;
            else value = found.Value;
            return true;
        }

        static bool ParseBytes(string text, out object value, out string error)
        {
            value = null; error = null;
            string v = text.Trim();
            try
            {
                if (v.Length == 0) { value = new byte[0]; return true; }
                if (v[0] == '[' && v[v.Length - 1] == ']')
                {
                    v = v.Substring(1, v.Length - 2).Trim();
                    if (v.Length == 0) { value = new byte[0]; return true; }
                    var parts = v.Split(',', '.');
                    var ret = new byte[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string b = parts[i].Trim();
                        ret[i] = b.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToByte(b.Substring(2), 16) : Convert.ToByte(b);
                    }
                    value = ret; return true;
                }
                // plain hex: "0A FF", "0a:ff", "0aff"
                string hex = new string(v.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray());
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
                if (hex.Length % 2 != 0) { error = "Odd number of hex digits."; return false; }
                var r = new byte[hex.Length / 2];
                for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                value = r; return true;
            }
            catch { error = "Use [1,2,3], [0x01,0x02] or hex digits like 0A FF."; return false; }
        }

        // ---- object identifiers ---------------------------------------------------------------------------------
        static Dictionary<string, BacnetObjectTypes> _typeAliases;
        static Dictionary<string, BacnetObjectTypes> TypeAliases()
        {
            if (_typeAliases != null) return _typeAliases;
            var d = new Dictionary<string, BacnetObjectTypes>(StringComparer.OrdinalIgnoreCase);
            foreach (BacnetObjectTypes t in Enum.GetValues(typeof(BacnetObjectTypes)))
            {
                string name = t.ToString();
                if (name == "MAX_BACNET_OBJECT_TYPE") continue;
                d[name] = t;
                string bare = name.StartsWith("OBJECT_") ? name.Substring(7) : name;
                d[bare] = t;
                d[bare.Replace('_', '-')] = t;
                d[bare.Replace("_", "")] = t;
                string sh = BACnetDevice.ShortenObjectId(name);
                if (sh != name) d[sh] = t;
            }
            return _typeAliases = d;
        }

        public static bool TryParseObjectId(string text, out BacnetObjectId id)
        {
            id = default;
            var s = (text ?? "").Split(':');
            if (s.Length != 2) return false;
            if (!uint.TryParse(s[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint inst)) return false;
            string ts = s[0].Trim();
            BacnetObjectTypes type;
            if (ushort.TryParse(ts, out ushort num)) type = (BacnetObjectTypes)num;
            else if (!TypeAliases().TryGetValue(ts, out type)) return false;
            id = new BacnetObjectId(type, inst);
            return true;
        }

        // ================================================================================================
        // Structure reflection (ExpandableObjectConverter replacement)
        // ================================================================================================

        public sealed class MemberAccess
        {
            public string Name;
            public Type Type;
            public bool CanWrite;
            public Func<object, object> Get;
            public Action<object, object> Set;
            public MemberInfo Info;
        }

        static readonly Dictionary<Type, List<MemberAccess>> _members = new Dictionary<Type, List<MemberAccess>>();

        public static List<MemberAccess> MembersOf(Type t)
        {
            lock (_members)
            {
                if (_members.TryGetValue(t, out var l)) return l;
                l = new List<MemberAccess>();
                // like ExpandableObjectConverter: public properties; fall back to public fields when there are none
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.CanRead).ToList();
                var browsable = props.Where(p => !p.GetCustomAttributes(typeof(System.ComponentModel.BrowsableAttribute), true)
                    .OfType<System.ComponentModel.BrowsableAttribute>().Any(b => !b.Browsable)).ToList();
                foreach (var p in browsable)
                {
                    var pi = p;
                    l.Add(new MemberAccess
                    {
                        Name = pi.Name, Type = pi.PropertyType, Info = pi,
                        CanWrite = pi.GetSetMethod(false) != null,
                        Get = o => pi.GetValue(o),
                        Set = (o, v) => pi.SetValue(o, v),
                    });
                }
                if (l.Count == 0)
                {
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var fi = f;
                        l.Add(new MemberAccess
                        {
                            Name = fi.Name, Type = fi.FieldType, Info = fi, CanWrite = !fi.IsInitOnly,
                            Get = o => fi.GetValue(o), Set = (o, v) => fi.SetValue(o, v),
                        });
                    }
                }
                _members[t] = l;
                return l;
            }
        }

        /// <summary>true for structures shown as expandable nodes (not for the primitive-like BACnet value types).</summary>
        public static bool IsComposite(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(TimeSpan)
                || t == typeof(BacnetObjectId) || t == typeof(BacnetBitString) || t == typeof(byte[]) || t == typeof(IPAddress)
                || t == typeof(object) || t.IsArray || typeof(IEnumerable).IsAssignableFrom(t) || t.IsInterface)
                return false;
            if (t == typeof(BacnetDeviceObjectPropertyReference)) return true;
            return t.IsValueType || t.IsClass;
        }
    }
}
