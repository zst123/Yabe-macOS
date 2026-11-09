using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using Yabe;

namespace YabeMac
{
    /// <summary>Tree node of the schedule editor (replacement of WinForms TreeNode).</summary>
    public class SchedNode : Observable
    {
        string _text; bool _expanded, _editing; string _editText;
        public string Text { get => _text; set => Set(ref _text, value); }
        public string Icon { get; set; }       // glyph standing for the original imageList icon
        public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
        public bool IsEditing { get => _editing; set => Set(ref _editing, value); }
        public string EditText { get => _editText; set => Set(ref _editText, value); }
        public object Tag;
        public SchedNode ParentNode;
        public ObservableCollection<SchedNode> Children { get; } = new ObservableCollection<SchedNode>();
        public void BeginEdit() { EditText = Text; IsEditing = true; }
        public override string ToString() => Text;
    }

    /// <summary>Value text round trip used by the schedule editor (port of Storage.Property.Serialize/DeserializeValue).</summary>
    static class ScheduleValueText
    {
        public static BacnetValue Deserialize(string value, BacnetApplicationTags type)
        {
            switch (type)
            {
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL:
                    if (value == "") return new BacnetValue(type, null);
                    return new BacnetValue(value);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN:
                    if (int.TryParse(value, out int vint)) return new BacnetValue(type, vint != 0);
                    return new BacnetValue(type, bool.Parse(value));
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT: return new BacnetValue(type, uint.Parse(value));
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT: return new BacnetValue(type, int.Parse(value));
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL: return new BacnetValue(type, float.Parse(value, CultureInfo.InvariantCulture));
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE: return new BacnetValue(type, double.Parse(value, CultureInfo.InvariantCulture));
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_OCTET_STRING:
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_CONTEXT_SPECIFIC_DECODED:
                    try { return new BacnetValue(type, Convert.FromBase64String(value)); } catch { return new BacnetValue(type, value); }
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING: return new BacnetValue(type, value);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING: return new BacnetValue(type, BacnetBitString.Parse(value));
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED: return new BacnetValue(type, uint.Parse(value));
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE:
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME: return new BacnetValue(type, DateTime.Parse(value));
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID: return new BacnetValue(type, BacnetObjectId.Parse(value));
                default: return new BacnetValue(type, null);
            }
        }

        public static string Serialize(BacnetValue value, BacnetApplicationTags type)
        {
            switch (type)
            {
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL: return value.ToString();
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL: return ((float)value.Value).ToString(CultureInfo.InvariantCulture);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE: return ((double)value.Value).ToString(CultureInfo.InvariantCulture);
                case BacnetApplicationTags.BACNET_APPLICATION_TAG_OCTET_STRING: return Convert.ToBase64String((byte[])value.Value);
                default: return value.Value.ToString();
            }
        }

        /// <summary>Time of day text, current culture "T" like the original.</summary>
        public static string Time(DateTime t) => t.ToString("T");

        public static bool TryParseTime(string s, out DateTime t)
        {
            s = s.Trim();
            if (DateTime.TryParse("01/01/2001 " + s, CultureInfo.CurrentCulture, DateTimeStyles.None, out t)) return true;
            if (DateTime.TryParse("2001-01-01 " + s, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return true;
            return false;
        }

        public static string GetNiceName(BacnetApplicationTags tag)
        {
            string name = tag.ToString();
            name = name.Length > 23 ? name.Substring(23) : name;
            name = name.Replace('_', ' ');
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
        }
    }

    public interface IEditableNode
    {
        SchedNode Node { get; }
        IEditableNodeParent Parent { get; }
        bool TryUpdateFromLabel(string label, out string error);
    }

    public interface IEditableNodeParent
    {
        string Name { get; }
        SchedNode Node { get; }
        List<IEditableNode> EditableEntries { get; }
        string GetDefaultLabelForNewNode();
        IEditableNode AddEntryFromLabel(string label, out string error);
        bool RemoveEntry(IEditableNode editableNode);
        IEditableNode CloneEntry(IEditableNode editableNode);
    }

    /// <summary>One time/value pair of a day's weekly schedule.</summary>
    public sealed class ScheduleEntry : IEditableNode
    {
        public DailySchedule Day { get; private set; }
        public SchedNode Node { get; private set; }
        public DateTime Time { get; private set; }
        public BacnetValue Value { get; private set; }
        public IEditableNodeParent Parent => Day;

        public ScheduleEntry(DailySchedule day, DateTime time, BacnetValue value)
        {
            Day = day; Time = time; Value = value;
            Node = new SchedNode { Text = ToDisplayString(), Icon = "→", Tag = this };
        }

        public string ToDisplayString()
        {
            if (Value.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL) return ScheduleValueText.Time(Time) + " = null";
            return ScheduleValueText.Time(Time) + " = " + ScheduleValueText.Serialize(Value, Value.Tag);
        }

        // Parses a "hh:mm:ss = value" (or "= null") label. Shared with ScheduleException.
        public static bool Validate(string label, BacnetApplicationTags scheduleType, out DateTime time, out BacnetValue value, out string error)
        {
            error = "Unknown error.";
            string[] s = label.Split('=');
            if (s.Length != 2)
            {
                time = new DateTime(); value = new BacnetValue();
                error = "Entry is incorrectly formatted. Entries must be entered similar to \"hh:mm:ss = <value>\" or \"hh:mm:ss = null\".";
                return false;
            }
            if (!ScheduleValueText.TryParseTime(s[0], out time))
            {
                error = "The entry time is incorrectly formatted. Entries must be entered similar to \"hh:mm:ss = <value>\" or \"hh:mm:ss = null\".";
                time = new DateTime(); value = new BacnetValue();
                return false;
            }
            try
            {
                if (s[1].ToLower().Contains("null")) value = new BacnetValue(null);
                else value = ScheduleValueText.Deserialize(s[1].Trim(), scheduleType);
                error = null;
                return true;
            }
            catch
            {
                error = "The entry value is incorrectly formatted. Entries must be entered similar to \"hh:mm:ss = <value>\" or \"hh:mm:ss = null\", where <value> is a valid value for the schedule data type.";
                time = new DateTime(); value = new BacnetValue();
                return false;
            }
        }

        public bool TryUpdateFromLabel(string label, out string error)
        {
            if (Validate(label, Day.ScheduleType, out DateTime time, out BacnetValue value, out error))
            {
                Time = time; Value = value; Node.Text = ToDisplayString();
                return true;
            }
            return false;
        }

        public void Encode(EncodeBuffer b)
        {
            BacnetValue bdt = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME, Time);
            ASN1.bacapp_encode_application_data(b, bdt);
            ASN1.bacapp_encode_application_data(b, Value);
        }
    }

    /// <summary>Monday..Sunday node and its ScheduleEntry list.</summary>
    public sealed class DailySchedule : IEditableNodeParent
    {
        readonly Func<BacnetApplicationTags> _type;
        public BacnetApplicationTags ScheduleType => _type();
        public SchedNode Node { get; private set; }
        public int DayIndex { get; }
        public string DayName => CultureInfo.InvariantCulture.DateTimeFormat.DayNames[(DayIndex + 1) % 7];
        public List<ScheduleEntry> Entries { get; } = new List<ScheduleEntry>();
        public List<IEditableNode> EditableEntries { get { var l = new List<IEditableNode>(); foreach (var e in Entries) l.Add(e); return l; } }
        public string Name => DayName;

        public DailySchedule(Func<BacnetApplicationTags> scheduleType, int dayIndex)
        {
            _type = scheduleType; DayIndex = dayIndex;
            Node = new SchedNode { Text = "[" + DayIndex + "] : " + DayName, Icon = "⏰", Tag = this };
        }

        void Attach(ScheduleEntry e) { Entries.Add(e); e.Node.ParentNode = Node; Node.Children.Add(e.Node); }

        public ScheduleEntry AddEntry(DateTime time, BacnetValue value) { var e = new ScheduleEntry(this, time, value); Attach(e); return e; }

        public IEditableNode CloneEntry(IEditableNode editableEntry)
        {
            if (editableEntry is ScheduleEntry se) { var e = new ScheduleEntry(this, se.Time, se.Value); Attach(e); return e; }
            return null;
        }

        public void SortEntries()
        {
            Entries.Sort((a, b) => a.Time.CompareTo(b.Time));
            Node.Children.Clear();
            foreach (var e in Entries) { e.Node.ParentNode = Node; Node.Children.Add(e.Node); }
        }

        public IEditableNode AddEntryFromLabel(string label, out string error)
        {
            if (ScheduleEntry.Validate(label, ScheduleType, out DateTime time, out BacnetValue value, out error))
            {
                var e = new ScheduleEntry(this, time, value); Attach(e); return e;
            }
            return null;
        }

        public bool RemoveEntry(IEditableNode entry)
        {
            if (entry is ScheduleEntry se)
            {
                if (se.Node != null) Node.Children.Remove(se.Node);
                if (Entries.Contains(se)) { Entries.Remove(se); return true; }
            }
            return false;
        }

        public string GetDefaultLabelForNewNode() => "00:00:00 = 0";
    }

    /// <summary>One BACnetSpecialEvent of the Exception_Schedule.</summary>
    public sealed class ScheduleException : IEditableNode
    {
        public ScheduleExceptionCollection ExceptionRoot { get; private set; }
        public SchedNode Node { get; private set; }
        public object DateSpecifier { get; private set; }   // BacnetDate, BacnetDateRange, BacnetweekNDay or BacnetObjectId (calendar ref)
        public uint Priority { get; private set; }
        public List<(DateTime Time, BacnetValue Value)> TimeValues { get; private set; }
        public IEditableNodeParent Parent => ExceptionRoot;

        public ScheduleException(ScheduleExceptionCollection root, object dateSpecifier, uint priority, IEnumerable<(DateTime, BacnetValue)> timeValues)
        {
            ExceptionRoot = root; DateSpecifier = dateSpecifier; Priority = priority;
            TimeValues = new List<(DateTime, BacnetValue)>(timeValues);
            Node = new SchedNode { Text = ToDisplayString(), Icon = "→", Tag = this };
        }

        public string ToDisplayString()
        {
            string times;
            if (TimeValues.Count == 0) times = "(no times = hold previous value)";
            else
            {
                var l = new List<string>(TimeValues.Count);
                foreach (var (time, value) in TimeValues)
                    l.Add(value.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL
                        ? ScheduleValueText.Time(time) + " = " + ScheduleValueText.Serialize(value, value.Tag)
                        : ScheduleValueText.Time(time) + " = null");
                times = string.Join(", ", l);
            }
            return FormatDateSpecifier(DateSpecifier) + ", priority=" + Priority + ", " + times;
        }

        const string Fmt = "Entries must be entered similar to \"25/12/2026, priority=16, 12:00:00 am=<value>\" or \"Monday (Days 1-7) in September, priority=16, 12:00:00 am=<value>\", etc.";

        public static bool Validate(string label, BacnetApplicationTags scheduleType, out object spec, out byte priority, out List<(DateTime, BacnetValue)> timeValues, out string error)
        {
            error = "Unknown error.";
            spec = null; priority = 0; timeValues = null;
            var nonBlank = new List<string>();
            foreach (var t in label.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (!string.IsNullOrWhiteSpace(t.Trim())) nonBlank.Add(t.Trim());
            string[] tokens = nonBlank.ToArray();
            int pos = 0;
            if (tokens.Length < 3) { error = "The entry is incorrectly formatted. " + Fmt; return false; }

            try
            {
                string cur = tokens[pos++];
                if (BacnetObjectId.TryParse(cur, out BacnetObjectId calendarRef)) spec = calendarRef;
                else if (BacnetDateRange.TryParse(cur, out BacnetDateRange dr)) spec = dr;
                else if (BacnetDate.TryParse(cur, out BacnetDate date)) spec = date;
                else if (BacnetweekNDay.TryParse(cur, out BacnetweekNDay wnd)) spec = wnd;
                else { error = "The entry date/date range/week&day specifier is incorrectly formatted. " + Fmt; spec = null; return false; }
            }
            catch { error = "The entry date/date range/week&day specifier is incorrectly formatted. " + Fmt; spec = null; return false; }

            try
            {
                if (!TryParsePriorityToken(tokens[pos++], out priority))
                {
                    error = "The entry priority is incorrectly formatted. Entries must be entered similar to \"25/12/2026, priority=16, 12:00:00 am=<value>\" or \"Monday (Days 1-7) in September, priority=16, 12:00:00 am=<value>\", where priority is a whole number from 1 to 16.";
                    spec = null; priority = 0; return false;
                }
            }
            catch { error = "The entry priority is incorrectly formatted. " + Fmt; spec = null; priority = 0; return false; }

            try
            {
                var list = new List<(DateTime, BacnetValue)>();
                for (int i = pos; i < tokens.Length; i++)
                {
                    if (ScheduleEntry.Validate(tokens[i], scheduleType, out DateTime t, out BacnetValue v, out _)) list.Add((t, v));
                    else
                    {
                        error = "An entry value is incorrectly formatted. Entries must be entered similar to \"25/12/2026, priority=16, 12:00:00 am=<value>\" or \"Monday (Days 1-7) in September, priority=16, 7:00:00 am=<value>, 4:00:00 pm=<value>\", etc.";
                        spec = null; priority = 0; return false;
                    }
                }
                if (list.Count == 0)
                {
                    error = "No entry times/values present. Entries must be entered similar to \"25/12/2026, priority=16, 12:00:00 am=<value>\" or \"Monday (Days 1-7) in September, priority=16, 7:00:00 am=<value>, 4:00:00 pm=<value>\", etc.";
                    spec = null; priority = 0; return false;
                }
                timeValues = list; error = null; return true;
            }
            catch { error = "The entry value is incorrectly formatted. " + Fmt; spec = null; priority = 0; return false; }
        }

        public bool TryUpdateFromLabel(string label, out string error)
        {
            if (Validate(label, ExceptionRoot.ScheduleType, out object spec, out byte priority, out var tvs, out error))
            {
                DateSpecifier = spec; Priority = priority; TimeValues = tvs; Node.Text = ToDisplayString();
                return true;
            }
            return false;
        }

        static bool TryParsePriorityToken(string token, out byte priority)
        {
            priority = 0;
            string trimmed = token.Trim();
            int split = trimmed.IndexOfAny(new[] { '=', ':' });
            string kw, val;
            if (split >= 0) { kw = trimmed.Substring(0, split).Trim(); val = trimmed.Substring(split + 1).Trim(); }
            else
            {
                int sp = trimmed.LastIndexOf(' ');
                if (sp < 0) return false;
                kw = trimmed.Substring(0, sp).Trim(); val = trimmed.Substring(sp + 1).Trim();
            }
            if (!kw.Equals("priority", StringComparison.OrdinalIgnoreCase)) return false;
            if (!byte.TryParse(val, out byte v) || v < 1 || v > 16) return false;
            priority = v; return true;
        }

        static string FormatDateSpecifier(object spec)
        {
            if (spec is BacnetObjectId id) return id.Type.ToString().Replace("OBJECT_", "") + ":" + id.Instance;
            return spec.ToString();
        }

        public void Encode(EncodeBuffer b)
        {
            if (DateSpecifier is BacnetObjectId objectId) ASN1.encode_context_object_id(b, 1, objectId.Type, objectId.Instance);
            else
            {
                ASN1.encode_opening_tag(b, 0);
                if (DateSpecifier is BacnetDate d) { ASN1.encode_tag(b, 0, true, 4); d.ASN1encode(b); }
                else if (DateSpecifier is BacnetDateRange r) { ASN1.encode_opening_tag(b, 1); r.ASN1encode(b); ASN1.encode_closing_tag(b, 1); }
                else if (DateSpecifier is BacnetweekNDay w) { ASN1.encode_tag(b, 2, true, 3); w.ASN1encode(b); }
                else throw new InvalidOperationException("Schedule exception date specifier is not encodable (unrecognized/unsupported format): " + DateSpecifier);
                ASN1.encode_closing_tag(b, 0);
            }
            ASN1.encode_opening_tag(b, 2);
            foreach (var (time, value) in TimeValues)
            {
                ASN1.bacapp_encode_application_data(b, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME, time));
                ASN1.bacapp_encode_application_data(b, value);
            }
            ASN1.encode_closing_tag(b, 2);
            ASN1.encode_context_unsigned(b, 3, Priority);
        }
    }

    /// <summary>"[7] Schedule Exceptions" root node.</summary>
    public sealed class ScheduleExceptionCollection : IEditableNodeParent
    {
        const string NODE_NAME = "Schedule Exceptions";
        readonly Func<BacnetApplicationTags> _type;
        public BacnetApplicationTags ScheduleType => _type();
        public SchedNode Node { get; private set; }
        public List<ScheduleException> Exceptions { get; } = new List<ScheduleException>();
        public List<IEditableNode> EditableEntries { get { var l = new List<IEditableNode>(); foreach (var e in Exceptions) l.Add(e); return l; } }
        public string Name => NODE_NAME;

        public ScheduleExceptionCollection(Func<BacnetApplicationTags> scheduleType)
        {
            _type = scheduleType;
            Node = new SchedNode { Text = "[7] " + NODE_NAME, Icon = "⚠", Tag = this };
        }

        public IEditableNode CloneEntry(IEditableNode e)
        {
            if (e is ScheduleException se) return AddException(se.DateSpecifier, se.Priority, se.TimeValues);
            return null;
        }

        public ScheduleException AddException(object spec, uint priority, IEnumerable<(DateTime, BacnetValue)> tvs)
        {
            var n = new ScheduleException(this, spec, priority, tvs);
            Exceptions.Add(n); n.Node.ParentNode = Node; Node.Children.Add(n.Node);
            return n;
        }

        public IEditableNode AddEntryFromLabel(string label, out string error)
        {
            if (ScheduleException.Validate(label, ScheduleType, out object spec, out byte priority, out var tvs, out error))
                return AddException(spec, priority, tvs);
            return null;
        }

        public bool RemoveEntry(IEditableNode entry)
        {
            if (entry is ScheduleException se)
            {
                if (se.Node != null) Node.Children.Remove(se.Node);
                if (Exceptions.Contains(se)) { Exceptions.Remove(se); return true; }
            }
            return false;
        }

        public string GetDefaultLabelForNewNode()
        {
            DateTime now = DateTime.Now;
            return new BacnetDate((byte)(now.Year - 1900), (byte)now.Month, (byte)now.Day).ToString() + ", priority=16, 00:00:00 = 0";
        }
    }
}
