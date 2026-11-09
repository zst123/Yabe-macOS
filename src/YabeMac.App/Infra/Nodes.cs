using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.BACnet;
using System.Runtime.CompilerServices;
using Yabe;

namespace YabeMac
{
    public class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string n = null)
        {
            if (Equals(field, value)) return false;
            field = value; Raise(n); return true;
        }
    }

    /// <summary>Node of the "Devices" tree: a channel (BacnetClient) or a device (BACnetDevice) or a folder.</summary>
    public class DeviceNode : Observable
    {
        string _title;
        public string Title { get => _title; set => Set(ref _title, value); }
        public ObservableCollection<DeviceNode> Children { get; }
        public DeviceNode(ObservableCollection<DeviceNode> children = null) { Children = children ?? new ObservableCollection<DeviceNode>(); }
        public BacnetClient Channel;      // set on channel nodes
        public BACnetDevice Device;       // set on device nodes
        public bool IsFolder;             // Device Class View folder (see DeviceClassViewFolderIds)
        public DeviceClassViewFolderIds FolderIds;   // set on Device Class View folders
        public string Name = "";          // sort key of folders (original TreeNode.Name)
        public int RootOrder;             // -1 = "Network View" root, -2 = "Device Class View" root, 0 otherwise
        string _glyph, _tip; bool _hl, _exp = true;
        public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
        public string ToolTipText { get => _tip; set => Set(ref _tip, value); }
        public bool Highlighted { get => _hl; set => Set(ref _hl, value); }
        public bool IsExpanded { get => _exp; set => Set(ref _exp, value); }
        public override string ToString() => Title;
    }

    /// <summary>Node of the "Objects" (address space) tree.</summary>
    public class ObjectNode : Observable
    {
        string _title;
        public string Title { get => _title; set => Set(ref _title, value); }
        public ObservableCollection<ObjectNode> Children { get; } = new ObservableCollection<ObjectNode>();
        public BACnetDevice Device;
        public BacnetObjectId? ObjectId;  // null for pure folder nodes
        string _glyph, _tip; bool _hl, _exp;
        public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
        public string ToolTipText { get => _tip; set => Set(ref _tip, value); }   // raw object id text (original TreeNode.ToolTipText)
        public bool Highlighted { get => _hl; set => Set(ref _hl, value); }
        public bool IsExpanded { get => _exp; set => Set(ref _exp, value); }
        public override string ToString() => Title;
    }

    /// <summary>Row of the Properties grid.</summary>
    public class PropertyRow : Observable
    {
        public string Name { get; set; }
        string _value;
        public string Value { get => _value; set => Set(ref _value, value); }
        public BacnetPropertyIds Property;
        public uint PropertyId;
        public System.Collections.Generic.IList<BacnetValue> Raw;
        public bool IsIndexed;
        public uint ArrayIndex = System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL;
    }
}
