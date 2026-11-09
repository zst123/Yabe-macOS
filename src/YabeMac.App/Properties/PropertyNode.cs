using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Reflection;

namespace YabeMac.PropertyPanel
{
    /// <summary>Which inline editor a value gets (replaces the PropertyGrid TypeConverter / UITypeEditor choice).</summary>
    public enum EditKind
    {
        ReadOnly,       // cannot be edited
        Text,           // character string
        Real,           // float
        Double,
        Unsigned,
        Signed,
        Boolean,
        Choice,         // enumeration (drop-down)
        BitString,      // check list (status flags, event enable ...) or "0101" text
        Date,
        Time,
        DateTime,
        ObjectId,
        Octets,         // byte[]
        IpAddress,
        Composite,      // expandable structure: edited through its children
        Null,           // only NULL (no inference possible)
    }

    public sealed class Choice
    {
        public uint Value;
        public string Label;
        public override string ToString() => Label;
    }

    /// <summary>One cell of the property tree: a BACnet property, an array/list element or a member of a structure.</summary>
    public sealed class PropertyNode
    {
        // -------- identity
        public string Name;
        public PropertyNode Parent;
        public uint PropertyId;                       // property of the root this node belongs to
        public BacnetObjectTypes ObjectType;
        public int Depth;
        public bool IsRoot => Parent == null;
        public PropertyNode Root { get { var n = this; while (n.Parent != null) n = n.Parent; return n; } }

        // -------- value
        public object Value;                          // decoded value (scalar, structure, or object[] for arrays/lists)
        public BacnetApplicationTags? Tag;            // application tag of the decoded value (null for arrays / members)
        public IList<BacnetValue> Raw;                // roots only: the values as read
        public bool IsArray;                          // children are elements (BACnet array or list)
        public bool IsList;                           // list (sequence-of) rather than array: written as a whole / add/remove element
        public int ElementIndex = -1;                 // 0-based index in the parent array
        public MemberInfo Member;                     // structure member this node represents
        public Type ClrType;                          // static type of the value (for parse/format)
        public DateTimeParts Parts;                   // Date/Time/DateTime with its wildcards (roots read from the device)
        public bool IsErrorValue;                     // the device answered with a BACnet error for this property

        // -------- editing
        public EditKind Kind = EditKind.ReadOnly;
        public bool ReadOnly;
        public List<Choice> Choices;                  // Kind == Choice
        public Type EnumType;                         // enum used for Choice / BitString names
        public bool LinearBits;                       // bit n named Enum(n) (true) or Enum(1<<n) (false)
        public bool Nullable;                         // an empty entry means NULL (relinquish)
        public string Hint;                           // text for tooltips / description

        // -------- display / state
        public bool Expanded;
        public bool Changed;                          // value changed since the last refresh
        public string Display;                        // formatted value
        public string PathKey;                        // stable key used to restore expansion/selection on refresh

        internal List<PropertyNode> _children;
        internal Func<PropertyNode, List<PropertyNode>> _childBuilder;
        public bool HasChildren => _childBuilder != null || (_children != null && _children.Count > 0);
        public List<PropertyNode> Children
        {
            get
            {
                if (_children == null)
                {
                    _children = _childBuilder != null ? _childBuilder(this) : new List<PropertyNode>();
                    _childBuilder = null;
                }
                return _children;
            }
        }

        /// <summary>Description shown in the description pane (the original showed the application tag name).</summary>
        public string Description
        {
            get
            {
                string d = Tag?.ToString() ?? ClrType?.Name ?? "";
                var r = Root;
                if (r != this && r.Tag != null && Tag == null) d = ClrType?.Name ?? "";
                string extra = IsRoot ? $"Property id {PropertyId}" : "";
                if (ReadOnly) extra += (extra.Length > 0 ? " - " : "") + "read only";
                return extra.Length > 0 ? d + "\n" + extra : d;
            }
        }

        public bool IsNullValue => Value == null && !IsArray && !HasChildren;
        public override string ToString() => Name + " = " + Display;
    }

    public sealed class PropertyReadResult
    {
        public List<PropertyNode> Nodes = new List<PropertyNode>();
        public string ObjectName;
        public bool[] ShowAlarmAck = new bool[3];      // offnormal, fault, normal (like the original ack buttons)
        public string Error;                           // null when the read worked
        public bool Simplified;                        // the simplified filter was applied
    }

    public sealed class WriteOutcome
    {
        public bool Ok;
        public string Error;                           // user-facing message
        public static WriteOutcome Success() => new WriteOutcome { Ok = true };
        public static WriteOutcome Fail(string e) => new WriteOutcome { Ok = false, Error = e };
    }
}
