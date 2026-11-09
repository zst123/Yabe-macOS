using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Threading.Tasks;
using Yabe;
using YabeMac.PropertyPanel;

namespace YabeMac
{
    // Properties panel: replacement of m_DataGrid (PropertyGrid + DynamicPropertyGridContainer) and of
    // MainDialog.UpdateGrid / m_DataGrid_PropertyValueChanged. The BACnet logic is in Properties/PropertyModel*.cs
    // (UI independent, tested by tools/proptest); the visual part is Controls/PropertyTree.cs.
    public partial class MainWindow
    {
        BACnetDevice _propDevice;
        BacnetObjectId? _propObject;
        int _propSeq;

        // Compatibility view of the last property read (Name/Property/Raw per property) used by other areas
        // (e.g. UpdateAckButtons in MainWindow.Alarms.cs reads Acked_Transitions / Event_Time_Stamps from it).
        // Clearing it from elsewhere (as the object tree does when the selection changes) also empties the panel.
        readonly System.Collections.ObjectModel.ObservableCollection<PropertyRow> _propertyRows = new System.Collections.ObjectModel.ObservableCollection<PropertyRow>();
        bool _rowsInternal;

        /// <summary>Which of the three alarm acknowledge buttons the last property read asks for (offnormal, fault, normal).</summary>
        public bool[] LastShowAlarmAck { get; private set; } = new bool[3];
        /// <summary>The device/object currently shown in the property panel (null when empty).</summary>
        public BACnetDevice PropertyDevice => _propDevice;
        public BacnetObjectId? PropertyObject => _propObject;

        void InitProperties()
        {
            var tree = PropertyTreeView;
            tree.CommitRequested += OnPropertyCommit;
            tree.RelinquishRequested += OnPropertyRelinquish;
            tree.AddElementRequested += OnPropertyAddElement;
            tree.RemoveElementRequested += OnPropertyRemoveElement;
            tree.OpenScheduleRequested += OnPropertyOpenSchedule;
            tree.OpenCalendarRequested += OnPropertyOpenCalendar;
            tree.NodeDoubleClicked += OnPropertyNodeDoubleClicked;
            tree.SimplifiedViewToggled += on => OnSimplifiedPropertyView(null, null);
            _propertyRows.CollectionChanged += (_, e) =>
            {
                if (!_rowsInternal && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && _propertyRows.Count == 0
                    && PropertyTreeView.Roots.Count > 0)
                { _propSeq++; _propDevice = null; _propObject = null; PropertyTreeView.Clear(); }
            };
        }

        void SetPropertyRows(IEnumerable<PropertyNode> nodes)
        {
            _rowsInternal = true;
            try
            {
                _propertyRows.Clear();
                if (nodes == null) return;
                foreach (var n in nodes)
                    _propertyRows.Add(new PropertyRow { Name = n.Name, Property = (BacnetPropertyIds)n.PropertyId, PropertyId = n.PropertyId, Raw = n.Raw, Value = n.Display });
            }
            finally { _rowsInternal = false; }
        }

        public void ClearProperties()
        {
            _propSeq++;
            _propDevice = null; _propObject = null;
            LastShowAlarmAck = new bool[3];
            SetPropertyRows(null);
            PropertyTreeView?.Clear();
        }

        // ---------------------------------------------------------------------------------------------
        // Reading
        // ---------------------------------------------------------------------------------------------

        /// <summary>Reads and shows the properties of the object selected in the object tree.</summary>
        async Task ShowProperties() => await ShowProperties(SelectedDevice, SelectedObjectId);

        /// <summary>Reads and shows the properties of any object (also used for subscription / trend selections).</summary>
        public async Task ShowProperties(BACnetDevice device, BacnetObjectId? objectId)
        {
            if (device == null || objectId == null) { ClearProperties(); return; }
            var id = objectId.Value;
            int seq = ++_propSeq;
            bool same = device.Equals(_propDevice) && id.Equals(_propObject) && PropertyTreeView.Roots.Count > 0;
            var previous = same ? PropertyTreeView.Roots.ToList() : null;
            if (!same) { PropertyTreeView.Clear(); HideAckButtons(); }
            var opt = ReadOptions.FromSettings(ToogleViewSimplified);
            Cursor = new Cursor(StandardCursorType.Wait);
            PropertyReadResult res;
            try { res = await Task.Run(() => PropertyModel.Read(device, id, opt)); }
            finally { Cursor = Cursor.Default; }
            if (seq != _propSeq) return;                                 // a newer request owns the panel

            if (res.Error != null)
            {
                Log($"Properties of {id}: {res.Error}");
                ClearProperties();
                PropertyTreeView.ShowMessage(res.Error);
                return;
            }
            if (same) PropertyModel.MarkChanges(previous, res.Nodes);

            _propDevice = device; _propObject = id;
            PropertyTreeView.CurrentObjectType = id.type;
            PropertyTreeView.SimplifiedView = IsSimplifiedViewActive;
            LblProperties.Text = PropSpaceText;
            PropertyTreeView.SetNodes(res.Nodes, same);
            LastShowAlarmAck = res.ShowAlarmAck;
            SetPropertyRows(res.Nodes);

            // the object name read may differ from the one in the tree (original: ChangeTreeNodePropertyName)
            if (res.ObjectName != null)
            {
                try
                {
                    device.UpdateObjectNameMapping(id, res.ObjectName);
                    var node = SelectedObjectNode;
                    if (node != null && node.ObjectId.HasValue && node.ObjectId.Value.Equals(id) && ReferenceEquals(node.Device, device))
                    {
                        string title = ObjectTitle(device, id);
                        if (node.Title != title) node.Title = title;
                    }
                }
                catch { }
            }
            UpdateAckButtons(id);
        }

        /// <summary>
        /// Menu handler (right click in the property panel): the simplified view is one switch for objects and properties
        /// in the original (ToogleViewSimplified), so this delegates to the object tree's OnSimplifiedView, which reloads
        /// the object list and, through the selection, the properties.
        /// </summary>
        void OnSimplifiedPropertyView(object s, RoutedEventArgs e) => OnSimplifiedView(null, null);

        public void SetPropertySimplified(bool on)
        {
            if (on != IsSimplifiedViewActive) OnSimplifiedView(null, null);
        }

        // ---------------------------------------------------------------------------------------------
        // Editing (m_DataGrid_PropertyValueChanged)
        // ---------------------------------------------------------------------------------------------

        async void OnPropertyCommit(PropertyNode node, object value)
        {
            var device = _propDevice; var id = _propObject;
            if (device == null || id == null) return;
            string shown = value == null ? "NULL" : PropertyModel.FormatObject(value, node.Tag);
            await RunPropertyWrite(device, id.Value, () => PropertyModel.Write(device, id.Value, node, value), $"Wrote {node.Name} = {shown}");
        }

        async void OnPropertyRelinquish(PropertyNode node)
        {
            var device = _propDevice; var id = _propObject;
            if (device == null || id == null) return;
            await RunPropertyWrite(device, id.Value, () => PropertyModel.Relinquish(device, id.Value, node), $"Relinquished {node.Name}");
        }

        async void OnPropertyAddElement(PropertyNode node)
        {
            var device = _propDevice; var id = _propObject;
            var list = node.IsArray && node.IsRoot ? node : node.Parent;
            if (device == null || id == null || list == null) return;
            var tpl = list.Children.FirstOrDefault(k => k.Value != null) ?? list.Children.FirstOrDefault();
            string text = "";
            if (tpl != null && tpl.Kind != EditKind.Composite)
            {
                text = await Dialogs.Input($"Value of the new element of {list.Name}:", "Add list element", tpl.Display ?? "");
                if (text == null) return;
            }
            else if (tpl != null && !await Dialogs.OkCancel($"The new element is a copy of the selected {list.Name} entry; edit its fields afterwards.", "Add list element")) return;
            if (!PropertyModel.TryMakeElement(list, text, out var element, out var err)) { await Dialogs.Message(err, "Add list element"); return; }
            await RunPropertyWrite(device, id.Value, () => PropertyModel.AddListElement(device, id.Value, list, element), $"Added element to {list.Name}");
        }

        async void OnPropertyRemoveElement(PropertyNode node)
        {
            var device = _propDevice; var id = _propObject;
            if (device == null || id == null || node.Parent == null) return;
            if (!await Dialogs.Confirm($"Remove {node.Name} ({node.Display}) from {node.Parent.Name}?", "Remove list element")) return;
            await RunPropertyWrite(device, id.Value, () => PropertyModel.RemoveListElement(device, id.Value, node), $"Removed element of {node.Parent.Name}");
        }

        async Task RunPropertyWrite(BACnetDevice device, BacnetObjectId id, Func<WriteOutcome> write, string okText)
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            WriteOutcome r;
            try { r = await Task.Run(write); }
            finally { Cursor = Cursor.Default; }
            if (r.Ok) Log(okText);
            else
            {
                Log(r.Error);
                await Dialogs.Message(r.Error, r.Error.StartsWith("Couldn't write") ? "Communication Error" : "Error");
            }
            // the original always reloads the grid after a write
            if (device.Equals(_propDevice) && id.Equals(_propObject)) await ShowProperties(device, id);
        }

        // ---------------------------------------------------------------------------------------------
        // Schedule / calendar editors (delivered by the Schedule worker; looked up by reflection)
        // ---------------------------------------------------------------------------------------------

        void OnPropertyOpenSchedule()
        {
            if (_propDevice == null || _propObject == null) return;
            if (!ExternalEditors.OpenSchedule(_propDevice, _propObject.Value)) Todo("Schedule editor");
        }

        void OnPropertyOpenCalendar()
        {
            if (_propDevice == null || _propObject == null) return;
            if (!ExternalEditors.OpenCalendar(_propDevice, _propObject.Value)) Todo("Calendar editor");
        }

        void OnPropertyNodeDoubleClicked(PropertyNode n)
        {
            if (_propObject == null || !n.IsRoot) return;
            var pid = (BacnetPropertyIds)n.PropertyId;
            if (_propObject.Value.type == BacnetObjectTypes.OBJECT_SCHEDULE &&
                (pid == BacnetPropertyIds.PROP_WEEKLY_SCHEDULE || pid == BacnetPropertyIds.PROP_EXCEPTION_SCHEDULE))
                OnPropertyOpenSchedule();
            else if (_propObject.Value.type == BacnetObjectTypes.OBJECT_CALENDAR && pid == BacnetPropertyIds.PROP_DATE_LIST)
                OnPropertyOpenCalendar();
        }
    }
}
