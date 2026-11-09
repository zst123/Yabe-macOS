using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>Port of MainDialog.DeviceView.cs DeviceClassViewFolderIds: the set of device ids (single ids and ranges) of a folder.</summary>
    public class DeviceClassViewFolderIds
    {
        class IdRange { public int Lower, Upper; public IdRange(int l, int u) { Lower = l; Upper = u; } }
        readonly List<IdRange> _ranges = new List<IdRange>();   // sorted by Lower, never overlapping or touching

        public string PrintRanges()
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var item in _ranges)
            {
                if (!first) sb.Append(","); else first = false;
                sb.Append(item.Lower);
                if (item.Upper > item.Lower) { sb.Append("-"); sb.Append(item.Upper); }
            }
            return sb.ToString();
        }
        public void AddIndividualId(int id) => AddIdRange(id, id);
        public bool AddIdRange(int lo, int hi)
        {
            if (lo > hi) return false;
            int mergedLower = lo, mergedUpper = hi;
            for (int i = _ranges.Count - 1; i >= 0; i--)
            {
                var range = _ranges[i];
                int upPlus = range.Upper == int.MaxValue ? int.MaxValue : range.Upper + 1;
                int loMinus = range.Lower == int.MinValue ? int.MinValue : range.Lower - 1;
                if (mergedLower <= upPlus && mergedUpper >= loMinus)
                {
                    mergedLower = Math.Min(mergedLower, range.Lower);
                    mergedUpper = Math.Max(mergedUpper, range.Upper);
                    _ranges.RemoveAt(i);
                }
            }
            InsertSorted(mergedLower, mergedUpper);
            return true;
        }
        public bool RemoveRange(int lo, int hi)
        {
            if (lo > hi) return false;
            bool any = false;
            for (int i = _ranges.Count - 1; i >= 0; i--)
            {
                var range = _ranges[i];
                if (hi < range.Lower || lo > range.Upper) continue;
                any = true;
                _ranges.RemoveAt(i);
                if (range.Lower < lo) InsertSorted(range.Lower, lo - 1);
                if (range.Upper > hi) InsertSorted(hi + 1, range.Upper);
            }
            return any;
        }
        public void RemoveIndividualId(int id) => RemoveRange(id, id);
        public bool Contains(int deviceId) { foreach (var r in _ranges) if (deviceId >= r.Lower && deviceId <= r.Upper) return true; return false; }
        public void ClearAllIds() => _ranges.Clear();
        void InsertSorted(int lower, int upper)
        {
            int index = _ranges.FindIndex(r => r.Lower > lower);
            if (index < 0) index = _ranges.Count;
            _ranges.Insert(index, new IdRange(lower, upper));
        }
    }

    // Device tree: channels / devices (routers, MSTP stations) in the "Network View" and the user defined
    // "Device Class View" folders (MainDialog.DeviceView.cs), device naming (UpdateTreeNodeDeviceName),
    // background operations (BACnetDeviceBackGroundWorker) and the device selection (m_DeviceTree_AfterSelect).
    public partial class MainWindow
    {
        public enum DeviceViewKind { Network, DeviceClass, NetworkThenDeviceClass, DeviceClassThenNetwork }
        const bool RESTRICT_ROUTED_ADDRESS_PRINTING_TO_SUB_TREE_NODES_ONLY = false;

        readonly ObservableCollection<DeviceNode> _treeRoots = new ObservableCollection<DeviceNode>();
        DeviceNode _networkRoot;           // original NetworkViewTreeNode ("Network View"), its children are _deviceRoots (the channels)
        DeviceNode _classRoot;             // original DeviceClassViewTreeNode ("Device Class View")
        DeviceNode DCVNodeNotAffected;     // folder for devices with unknown destination
        DeviceNode _selectedDeviceNode;    // original _selectedDevice
        string _highlightDevice = "";

        DeviceViewKind DeviceView
        {
            get { Enum.TryParse(AppSettings.Default.DeviceViewMode, true, out DeviceViewKind k); return k; }
        }

        /// <summary>Called once from InitNetwork (after InitializeComponent): builds the roots of the device tree and wires the UI.</summary>
        void InitDeviceView()
        {
            _networkRoot = new DeviceNode(_deviceRoots) { Title = "Network View", RootOrder = -1, Glyph = IconGlyph(0) };
            var mode = DeviceView;
            if (mode != DeviceViewKind.Network) _classRoot = CreateDeviceClassView();

            var dcvMenu = DeviceTreeMenu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Device view folders");
            if (_classRoot == null && dcvMenu != null) dcvMenu.IsVisible = false;

            // Network View present if the Device View is not defined (normally it is); NodeSorter orders the two roots
            var roots = new List<DeviceNode>();
            if (mode != DeviceViewKind.DeviceClass || _classRoot == null) roots.Add(_networkRoot);
            if (_classRoot != null) roots.Add(_classRoot);
            roots.Sort((a, b) => mode == DeviceViewKind.NetworkThenDeviceClass ? b.RootOrder.CompareTo(a.RootOrder) : a.RootOrder.CompareTo(b.RootOrder));
            foreach (var r in roots) _treeRoots.Add(r);
            DeviceTree.ItemsSource = _treeRoots;

            HighlightDevice.IsVisible = AppSettings.Default.ShowHighLightFilter;
            HighlightDevice.PropertyChanged += (_, ev) =>
            {
                if (ev.Property != TextBox.TextProperty) return;
                _highlightDevice = HighlightDevice.Text ?? "";
                HighlightDeviceNodes(_treeRoots, _highlightDevice);
            };
            AttachRightClickSelects(DeviceTree);
            AttachClassViewDragDrop();

            // Background operations are not done in the ThreadPool to have control of the task numbers
            // (BACnet InvokeId is 1 byte): max 10 parallel threads
            if (BackGroundOperation != "None")
                for (int i = 0; i < Math.Min(AppSettings.Default.BackGroundThreadNumber, 10); i++)
                    new Thread(BACnetDeviceBackGroundWorker) { IsBackground = true }.Start();
        }

        string BackGroundOperation => AppSettings.Default.BackGroundOperations ?? "None";

        // ---- device titles ------------------------------------------------------------------------------------
        /// <summary>Original node text: "Device 599 - 127.0.0.1:47809" ("Device Id? -" when the id is not known yet).</summary>
        static string DeviceTitle(BACnetDevice d, bool restrictRouted = RESTRICT_ROUTED_ADDRESS_PRINTING_TO_SUB_TREE_NODES_ONLY) =>
            (d.deviceId < 0x3FFFFF ? "Device " + d.deviceId : "Device Id?") + " - " + d.BacAdr.ToString(restrictRouted);

        // ---- adding devices (OnIam GUI update) ----------------------------------------------------------------------------
        /// <summary>Adds a device under its channel (or under its router) unless it is already known. Returns the node.</summary>
        public DeviceNode AddDevice(BacnetClient sender, BacnetAddress adr, uint deviceId, uint maxApdu = 480,
            BacnetSegmentations seg = BacnetSegmentations.SEGMENTATION_UNKNOW, ushort vendorId = 0)
        {
            if (!_channelNodes.TryGetValue(sender, out var parent)) return null;
            var new_device = new BACnetDevice(sender, adr, deviceId, maxApdu, seg, vendorId);
            string deviceName = new_device.deviceName;

            // Check directly connected nodes and their routed children: update existing (can happen in MSTP where slaveId is replaced by the deviceId)
            foreach (var s in parent.Children)
            {
                if (s.Device != null && s.Device.Equals(new_device)) { UpdateKnownDevice(s, new_device, deviceId, maxApdu, seg, vendorId, deviceName, RESTRICT_ROUTED_ADDRESS_PRINTING_TO_SUB_TREE_NODES_ONLY); return s; }
                foreach (var routedChild in s.Children)
                    if (routedChild.Device != null && routedChild.Device.Equals(new_device)) { UpdateKnownDevice(routedChild, new_device, deviceId, maxApdu, seg, vendorId, deviceName, false); return routedChild; }
            }

            DeviceNode insertionPoint = parent;
            // Try to find an insertion point under a router if any
            if (new_device.BacAdr.RoutedSource != null)
                foreach (var s in parent.Children)
                    if (s.Device != null && s.Device.BacAdr.IsMyRouter(adr)) { insertionPoint = s; break; }

            var newNode = new DeviceNode
            {
                Device = new_device,
                Title = DeviceTitle(new_device, RESTRICT_ROUTED_ADDRESS_PRINTING_TO_SUB_TREE_NODES_ONLY && insertionPoint == parent),
                Glyph = IconGlyph(2),
            };
            new_device.Tag = newNode;
            if (deviceName != null) { newNode.ToolTipText = newNode.Title; newNode.Title = deviceName + " [" + deviceId + "] "; }
            else newNode.ToolTipText = "";
            newNode.Highlighted = HighlightHit(newNode.Title, _highlightDevice);

            InsertSorted(insertionPoint.Children, newNode);
            Log($"I-Am: device {deviceId} at {adr}");
            AddToDeviceClassView(newNode);

            if (BackGroundOperation != "None")
            {
                var n = deviceName != null ? null : newNode;          // node not required to be updated when the name is known
                BackGroundQueries.Enqueue(Tuple.Create(new_device, n, 0));
                SemBackground.Release();
            }
            return newNode;
        }

        void UpdateKnownDevice(DeviceNode s, BACnetDevice nd, uint deviceId, uint maxApdu, BacnetSegmentations seg, ushort vendorId, string deviceName, bool restrict)
        {
            var d = s.Device;
            if (d.deviceId >= 0x3FFFFF) d.deviceId = deviceId;
            d.vendor_Id = vendorId;                  // Update vendor id (mstp case)
            d.MaxAPDULenght = maxApdu;
            d.Segmentation = seg;
            s.Title = DeviceTitle(d, restrict);
            if (deviceName != null) UpdateDeviceNodeName(d, s);
        }

        // ---- sorting (NodeSorter) ------------------------------------------------------------------------------------------
        static int CompareNodes(DeviceNode tx, DeviceNode ty)
        {
            if (tx.Device != null && ty.Device != null) return tx.Device.deviceId.CompareTo(ty.Device.deviceId);          // two devices: by deviceId
            if (tx.FolderIds != null && ty.FolderIds != null) return string.CompareOrdinal(tx.Name, ty.Name);            // two folders: by Name
            if (tx.Device != null && ty.FolderIds != null) return -1;                                                     // device first
            if (tx.FolderIds != null && ty.Device != null) return 1;
            return 0;
        }

        static void InsertSorted(IList<DeviceNode> col, DeviceNode n)
        {
            int idx = col.Count;
            for (int i = 0; i < col.Count; i++)
                if (CompareNodes(col[i], n) > 0) { idx = i; break; }
            col.Insert(idx, n);
        }

        // ---- naming ---------------------------------------------------------------------------------------------------------
        /// <summary>Port of UpdateTreeNodeDeviceName: devices can be at several places, so a rename is propagated to each clone.</summary>
        public void UpdateDeviceNodeName(BACnetDevice device, DeviceNode original, IList<DeviceNode> Root = null)
        {
            if (Dispatcher.UIThread.CheckAccess() == false) { Dispatcher.UIThread.Post(() => UpdateDeviceNodeName(device, original, Root)); return; }
            if (Root == null)   // First step, change the ToolTip and the Text
            {
                string Identifier = device.ReadObjectName(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.deviceId));
                if (string.IsNullOrWhiteSpace(Identifier) || string.IsNullOrEmpty(original.ToolTipText))
                {
                    original.ToolTipText = original.Title.Replace("Id?", device.deviceId.ToString());
                    original.Title = Identifier + " [" + device.deviceId + "] ";
                    original.Highlighted = HighlightHit(original.Title, _highlightDevice);
                }
                Root = _treeRoots;
            }
            foreach (var t in Root.ToList())
            {
                if (t == original) { if (t.Children.Count != 0) UpdateDeviceNodeName(device, original, t.Children); continue; }
                if (t.Device == device)
                {
                    t.ToolTipText = original.ToolTipText;
                    t.Title = original.Title;
                    t.Highlighted = original.Highlighted;
                }
                if (t.Children.Count != 0) UpdateDeviceNodeName(device, original, t.Children);
            }
        }

        /// <summary>Port of DeleteTreeNodeDevice: removes every node (all views) of a device.</summary>
        public void DeleteTreeNodeDevice(BACnetDevice device, IList<DeviceNode> Root = null)
        {
            Root = Root ?? _treeRoots;
            for (int i = Root.Count - 1; i >= 0; i--)
            {
                if (Root[i].Children.Count != 0) DeleteTreeNodeDevice(device, Root[i].Children);
                if (Root[i].Device != null && Root[i].Device == device) Root.RemoveAt(i);
            }
        }

        void HighlightDeviceNodes(IEnumerable<DeviceNode> nodes, string text)
        {
            foreach (var n in nodes)
            {
                n.Highlighted = HighlightHit(n.Title, text);
                HighlightDeviceNodes(n.Children, text);
            }
        }

        // ---- MSTP stations (MSTP_FrameRecieved GUI part; the transport itself lives with the channels area) -------------------------
        /// <summary>MSTP station seen on the line: node "deviceNN" with an unconfigured device (0x3FFFFF). Replaces the "freeNN" node.</summary>
        public DeviceNode AddMstpStationNode(BacnetClient client, byte source_address)
        {
            if (!_channelNodes.TryGetValue(client, out var parent)) return null;
            var free = parent.Children.FirstOrDefault(n => n.Title == "free" + source_address);
            var dev = new BACnetDevice(client, new BacnetAddress(BacnetAddressTypes.MSTP, 0, new byte[] { source_address }), 0x3FFFFF);
            var node = new DeviceNode { Title = "device" + source_address, Device = dev, Glyph = IconGlyph(2), ToolTipText = "" };
            dev.Tag = node;
            parent.Children.Add(node);
            if (free != null) parent.Children.Remove(free);
            return node;
        }

        /// <summary>MSTP address polled by a master but not seen as source: node "freeNN" (only if MSTP_DisplayFreeAddresses).</summary>
        public DeviceNode AddMstpFreeNode(BacnetClient client, byte destination_address)
        {
            if (!AppSettings.Default.MSTP_DisplayFreeAddresses) return null;
            if (!_channelNodes.TryGetValue(client, out var parent)) return null;
            var node = new DeviceNode { Title = "free" + destination_address, Glyph = IconGlyph(9), ToolTipText = "" };
            parent.Children.Add(node);
            return node;
        }

        // ---- selection ---------------------------------------------------------------------------------------------------------------
        async void OnDeviceSelected(object s, SelectionChangedEventArgs e)
        {
            var node = DeviceTree.SelectedItem as DeviceNode;
            if (node == null) return;                               // Toggle Simplified View without selected node
            var device = node.Device;
            if (device == null) return;                             // Not a node linked to a device

            if (_selectedDeviceNode != null && _selectedDeviceNode.Device == device && _objectRoots.Count > 0) return;   // same device, on a clone node
            _selectedDeviceNode = node;

            // Unconfigured MSTP: the transport is not available on this platform (see report)
            await LoadObjects(device, false, false);
            if (_selectedDeviceNode != node) return;
            node.Highlighted = HighlightHit(node.Title, _highlightDevice);
        }

        void OnDeviceDoubleTapped(object s, TappedEventArgs e) { }     // the original has no handler (default tree behaviour)

        // ===================================================================================================================================
        // Device Class View (MainDialog.DeviceView.cs)
        // ===================================================================================================================================
        DeviceNode MakeFolder(string text, DeviceClassViewFolderIds ids) =>
            new DeviceNode { Title = text, Name = text, FolderIds = ids, IsFolder = true, Glyph = IconGlyph(11) };

        DeviceNode CreateDeviceClassView()
        {
            string Descr = AppSettings.Default.DeviceClassStructure ?? "";
            var root = new DeviceNode { Title = "Device Class View", RootOrder = -2, Glyph = IconGlyph(0) };
            var AddedTn = new List<DeviceNode>();

            foreach (string Folder in Descr.Split(';'))
            {
                string[] elements = Folder.Split(new[] { '(', ')', ',' });
                if (string.IsNullOrWhiteSpace(elements[0])) continue;
                var ids = new DeviceClassViewFolderIds();
                var tnFolder = MakeFolder(elements[0].Trim(), ids);

                for (int i = 1; i < elements.Length; i++)
                {
                    string cur = elements[i].Trim();
                    if (string.IsNullOrWhiteSpace(cur)) continue;
                    if (cur.Contains("-"))
                    {
                        string[] b = cur.Split('-');
                        if (b.Length >= 2 && int.TryParse(b[0].Trim(), out int lo) && int.TryParse(b[b.Length - 1].Trim(), out int hi))
                            ids.AddIdRange(lo, hi);                           // a DeviceId range
                        else
                            AddGroupReferenceForDeviceClassView(AddedTn, tnFolder, cur);   // maybe a group name containing '-'
                    }
                    else if (int.TryParse(cur, out int id)) ids.AddIndividualId(id);
                    else AddGroupReferenceForDeviceClassView(AddedTn, tnFolder, cur);       // a reference to a previous group
                }
                AddedTn.Add(tnFolder);
            }

            if (AddedTn.Count == 0) AddedTn.Add(MakeFolder("New Folder", new DeviceClassViewFolderIds()));

            // Node for not affected devices is "hidden" if its name is blank
            if (!string.IsNullOrEmpty(AppSettings.Default.NotAffectedFolderName))
            {
                DCVNodeNotAffected = MakeFolder(AppSettings.Default.NotAffectedFolderName, new DeviceClassViewFolderIds());
                DCVNodeNotAffected.Name = new string((char)255, 1);       // for ordering, always last folder
                AddedTn.Add(DCVNodeNotAffected);
            }
            foreach (var tn in AddedTn) InsertSorted(root.Children, tn);
            return root;
        }

        void AddGroupReferenceForDeviceClassView(List<DeviceNode> previouslyAdded, DeviceNode newGroup, string groupName)
        {
            int idx = previouslyAdded.FindIndex(o => o.Title.ToLower() == groupName.ToLower());
            if (idx != -1) { InsertSorted(newGroup.Children, previouslyAdded[idx]); previouslyAdded.RemoveAt(idx); }
        }

        static DeviceNode CloneNode(DeviceNode n)
        {
            var c = new DeviceNode { Title = n.Title, ToolTipText = n.ToolTipText, Glyph = n.Glyph, Highlighted = n.Highlighted, Device = n.Device, Channel = n.Channel, IsExpanded = n.IsExpanded };
            foreach (var ch in n.Children) c.Children.Add(CloneNode(ch));
            return c;
        }

        /// <summary>By default devices are added first in the Network view, then cloned to be added in this view.</summary>
        bool AddToDeviceClassView(DeviceNode deviceNode, DeviceNode CurrentNode = null, bool IsAffected = false)
        {
            if (_classRoot == null || deviceNode == null) return false;
            if (CurrentNode == null) CurrentNode = _classRoot;

            // Find and add at all right places
            foreach (var tn in CurrentNode.Children.ToList())
                if (tn.FolderIds != null)                                   // Folder node ?
                    if (AddToDeviceClassView(deviceNode, tn, IsAffected)) IsAffected = true;     // recursive call

            if (CurrentNode.FolderIds != null && deviceNode.Device != null)
            {
                if (CurrentNode.FolderIds.Contains((int)deviceNode.Device.deviceId))
                {
                    InsertSorted(CurrentNode.Children, CloneNode(deviceNode));
                    CurrentNode.IsExpanded = true;
                    IsAffected = true;
                }
            }

            // Not added, put it in the folder for all not affected devices
            if (!IsAffected && CurrentNode == _classRoot && DCVNodeNotAffected != null)
                InsertSorted(DCVNodeNotAffected.Children, CloneNode(deviceNode));

            return IsAffected;
        }

        // ---- tree helpers ---------------------------------------------------------------------------------------------------
        DeviceNode FindParent(DeviceNode root, DeviceNode child)
        {
            foreach (var c in root.Children)
            {
                if (c == child) return root;
                var p = FindParent(c, child);
                if (p != null) return p;
            }
            return null;
        }
        DeviceNode ParentOf(DeviceNode n)
        {
            foreach (var r in _treeRoots) { if (r == n) return null; var p = FindParent(r, n); if (p != null) return p; }
            return null;
        }
        void RemoveNode(DeviceNode n)
        {
            var p = ParentOf(n);
            if (p != null) p.Children.Remove(n); else _treeRoots.Remove(n);
        }

        void RewriteDeviceClassStructureSettings(DeviceNode tnParent, ref string DeviceClassStructure)
        {
            foreach (var tn in tnParent.Children)
            {
                if (tn.FolderIds != null && tn != DCVNodeNotAffected)
                {
                    RewriteDeviceClassStructureSettings(tn, ref DeviceClassStructure);       // recursive call to children
                    DeviceClassStructure += tn.Title + "(" + tn.FolderIds.PrintRanges();
                    foreach (var tn2 in tn.Children)                                         // the list of Folders inside the current Folder
                        if (tn2.FolderIds != null) DeviceClassStructure += "," + tn2.Title;
                    DeviceClassStructure += ");";
                }
            }
        }

        void RewriteSetting()
        {
            string s = "";
            RewriteDeviceClassStructureSettings(_classRoot, ref s);
            AppSettings.Default.DeviceClassStructure = s;
        }

        void MoveNode(DeviceNode SourceNode, DeviceNode DestinationNode)
        {
            if (SourceNode.Device != null)       // Remove the device identifier from the original folder
            {
                var parent = ParentOf(SourceNode);
                var d = parent?.FolderIds;
                if (d == null) return;           // should never occur
                d.RemoveIndividualId((int)SourceNode.Device.deviceId);
                if (DestinationNode != null)
                {
                    d = DestinationNode.FolderIds;
                    if (!d.Contains((int)SourceNode.Device.deviceId)) d.AddIndividualId((int)SourceNode.Device.deviceId);
                }
            }
            RemoveNode(SourceNode);
            if (DestinationNode != null) InsertSorted(DestinationNode.Children, SourceNode);
            RewriteSetting();
        }

        void RecursivelyUnaffectChildNodes(DeviceNode start)
        {
            if (start.FolderIds == null) return;
            if (start == DCVNodeNotAffected) return;
            var toMove = new List<DeviceNode>();
            foreach (var child in start.Children)
            {
                if (child.FolderIds != null) RecursivelyUnaffectChildNodes(child);
                else if (child.Device != null) toMove.Add(child);
            }
            foreach (var n in toMove) MoveNode(n, DCVNodeNotAffected);
        }

        // ---- menu items (DCVViewToolStripMenuItem_Click, Unaffect, Duplicate) ---------------------------------------------------------------
        async void OnInsertFolder(object s, RoutedEventArgs e)
        {
            var Node = DeviceTree.SelectedItem as DeviceNode;
            if (_classRoot == null || Node == null) return;
            if (Node == _classRoot || ParentOf(Node) == _classRoot)
            {
                string name = await Dialogs.Input("New Folder Name", "Device View Class", "");
                if (name == null) return;
                InsertSorted(Node.Children, MakeFolder(name, new DeviceClassViewFolderIds()));
                Node.IsExpanded = true;
                RewriteSetting();
            }
        }

        async void OnRenameFolder(object s, RoutedEventArgs e)
        {
            var Node = DeviceTree.SelectedItem as DeviceNode;
            if (Node?.FolderIds == null) return;
            string name = await Dialogs.Input("New Folder Name", "Device View Class", Node.Title);
            if (name == null) return;
            var parent = ParentOf(Node);
            Node.Title = Node.Name = name;
            if (parent != null) { parent.Children.Remove(Node); InsertSorted(parent.Children, Node); DeviceTree.SelectedItem = Node; }   // re-sort
            if (Node == DCVNodeNotAffected) AppSettings.Default.NotAffectedFolderName = name;
            RewriteSetting();
        }

        async void OnDeleteFolder(object s, RoutedEventArgs e)
        {
            var Node = DeviceTree.SelectedItem as DeviceNode;
            if (Node?.FolderIds == null || Node == DCVNodeNotAffected) return;
            if (!await Dialogs.OkCancel("Confirm the deletion of \"" + Node.Title + "\"?", "Folder Delete")) return;
            // Recursively move all the devices in the folder to "Not Affected"
            RecursivelyUnaffectChildNodes(Node);
            RemoveNode(Node);
            if (DCVNodeNotAffected != null && DCVNodeNotAffected.Children.Count == 1) DCVNodeNotAffected.IsExpanded = true;
            RewriteSetting();
        }

        void OnUnaffectDevice(object s, RoutedEventArgs e)
        {
            var Node = DeviceTree.SelectedItem as DeviceNode;
            if (Node?.Device == null || DCVNodeNotAffected == null) return;
            if (ParentOf(Node)?.FolderIds == null) return;
            MoveNode(Node, DCVNodeNotAffected);
            if (DCVNodeNotAffected.Children.Count == 1) DCVNodeNotAffected.IsExpanded = true;
        }

        void OnDuplicateDevice(object s, RoutedEventArgs e)
        {
            // It's a problem to duplicate a node in the same folder (when one is moved the deviceId is removed from the accepted list): forbidden.
            if (DCVNodeNotAffected == null) return;
            var Node = DeviceTree.SelectedItem as DeviceNode;
            if (Node?.Device == null || ParentOf(Node)?.FolderIds == null) return;
            InsertSorted(DCVNodeNotAffected.Children, CloneNode(Node));
            if (DCVNodeNotAffected.Children.Count == 1) DCVNodeNotAffected.IsExpanded = true;
        }

        // ---- drag & drop inside the Device Class View (m_DeviceTree_ItemDrag / DragEnter / DragDrop) --------------------------------------------
        DeviceNode _dragNode; Avalonia.Point _dvDragStart;

        void AttachClassViewDragDrop()
        {
            if (_classRoot == null) return;
            DragDrop.SetAllowDrop(DeviceTree, true);
            DeviceTree.AddHandler(InputElement.PointerPressedEvent, (object _, PointerPressedEventArgs e) =>
            {
                _dragNode = null;
                if (!e.GetCurrentPoint(DeviceTree).Properties.IsLeftButtonPressed) return;
                var n = NodeAt(e.Source);
                if (n == null || n == DCVNodeNotAffected) return;
                // only allow a child node of the Device Class View
                var top = n; var p = ParentOf(top);
                while (p != null) { top = p; p = ParentOf(top); }
                if (top == _classRoot && n != _classRoot) { _dragNode = n; _dvDragStart = e.GetPosition(DeviceTree); }
            }, RoutingStrategies.Tunnel);
            DeviceTree.AddHandler(InputElement.PointerMovedEvent, async (object _, PointerEventArgs e) =>
            {
                if (_dragNode == null || !e.GetCurrentPoint(DeviceTree).Properties.IsLeftButtonPressed) return;
                var pos = e.GetPosition(DeviceTree);
                if (Math.Abs(pos.X - _dvDragStart.X) < 6 && Math.Abs(pos.Y - _dvDragStart.Y) < 6) return;
                var node = _dragNode; _dragNode = null;
#pragma warning disable CS0618
                var data = new DataObject();
                data.Set("YabeDeviceNode", node);
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
            }, RoutingStrategies.Tunnel);
            DeviceTree.AddHandler(DragDrop.DragOverEvent, (object _, DragEventArgs e) => { e.DragEffects = DragDropEffects.Move; });
            DeviceTree.AddHandler(DragDrop.DropEvent, (object _, DragEventArgs e) =>
            {
#pragma warning disable CS0618
                var SourceNode = e.Data.Get("YabeDeviceNode") as DeviceNode;
#pragma warning restore CS0618
                var DestinationNode = NodeAt(e.Source);
                DropNode(SourceNode, DestinationNode);
            });
        }

        DeviceNode NodeAt(object source)
        {
            var v = source as Avalonia.Visual;
            while (v != null && !(v is TreeViewItem)) v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
            return (v as TreeViewItem)?.DataContext as DeviceNode;
        }

        void DropNode(DeviceNode SourceNode, DeviceNode DestinationNode)
        {
            if (DestinationNode == null || SourceNode == null) return;
            if (DestinationNode.Device != null) DestinationNode = ParentOf(DestinationNode);       // nothing inside a device: use the parent folder
            if (DestinationNode == null || SourceNode == DestinationNode) return;
            if (SourceNode.Device != null && DestinationNode == _classRoot) return;               // no device at the top level
            if (SourceNode.FolderIds != null && DestinationNode == DCVNodeNotAffected) return;    // no folder inside the Unaffected Devices folder
            if (DestinationNode != _classRoot && DestinationNode.FolderIds == null) return;
            // not possible to put a parent inside its child node
            var tnParent = ParentOf(DestinationNode);
            while (tnParent != null) { if (tnParent == SourceNode) return; tnParent = ParentOf(tnParent); }
            if (DestinationNode == SourceNode) return;
            MoveNode(SourceNode, DestinationNode);
            if (DestinationNode.Children.Count == 1) DestinationNode.IsExpanded = true;
        }

        // ---- background operations (BACnetDeviceBackGroundWorker) ----------------------------------------------------------------------
        readonly SemaphoreSlim SemBackground = new SemaphoreSlim(0, int.MaxValue);
        readonly ConcurrentQueue<Tuple<BACnetDevice, DeviceNode, int>> BackGroundQueries = new ConcurrentQueue<Tuple<BACnetDevice, DeviceNode, int>>();
        readonly ConcurrentQueue<Tuple<BACnetDevice, DeviceNode, int>> BackGroundQueriesLowPriority = new ConcurrentQueue<Tuple<BACnetDevice, DeviceNode, int>>();

        void BACnetDeviceBackGroundWorker()
        {
            for (; ; )
            {
                SemBackground.Wait();
                if (!BackGroundQueries.TryDequeue(out var JobParam))
                    if (!BackGroundQueriesLowPriority.TryDequeue(out JobParam)) continue;       // should never happen

                bool LastJob = BackGroundQueries.IsEmpty && BackGroundQueriesLowPriority.IsEmpty;
                var device = JobParam.Item1;
                var OriginalNode = JobParam.Item2;
                try
                {
                    if (JobParam.Item3 == 0)
                    {
                        // First get the device name, then update all nodes in the tree
                        string deviceName = device.ReadObjectName(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.deviceId));
                        if (OriginalNode != null && !string.IsNullOrWhiteSpace(deviceName))
                            Dispatcher.UIThread.Invoke(() => { if (string.IsNullOrEmpty(OriginalNode.ToolTipText)) UpdateDeviceNodeName(device, OriginalNode); });
                    }
                    switch (BackGroundOperation)
                    {
                        case "GetObjectsList":
                            if (!device.RunOnSlowNetworks) { device.ReadObjectList(out _, out _, false); device.ReadAllHierachicalSubLists(); }
                            break;
                        case "GetObjectsName":
                            if (!device.RunOnSlowNetworks) { device.ReadObjectList(out _, out _, false); device.ReadAllHierachicalSubLists(); device.ReadAllObjectsName(); }
                            break;
                        case "GetObjectsListIncludeMstp":
                            device.ReadObjectList(out _, out _, false); device.ReadAllHierachicalSubLists();
                            break;
                        case "GetObjectsNameIncludeMstp":
                            device.ReadObjectList(out _, out _, false); device.ReadAllHierachicalSubLists(); device.ReadAllObjectsName();
                            break;
                        case "GetAbsolutelyAll":
                            device.ReadObjectList(out List<BacnetObjectId> list, out uint Count, false);
                            if (list == null && Count > 0)
                            {
                                // First loop, push the continuation at the end of the queue: the thread(s) can go on with other devices
                                if (JobParam.Item3 == 0)
                                {
                                    BackGroundQueriesLowPriority.Enqueue(Tuple.Create(JobParam.Item1, JobParam.Item2, 1));
                                    SemBackground.Release();
                                    continue;
                                }
                                for (uint i = 1; i <= Count; i++) device.ReadObjectListItem(out _, i);
                                device.ReadObjectList(out list, out Count, false);
                                if (list == null) break;
                                device.ReadAllHierachicalSubLists();
                                device.ReadAllObjectsName();
                                if (device.GetObjectName(list.Last()) == null)
                                {
                                    BackGroundQueriesLowPriority.Enqueue(Tuple.Create(JobParam.Item1, JobParam.Item2, 2));
                                    SemBackground.Release();
                                    continue;
                                }
                            }
                            else if (list != null)
                            {
                                device.ReadAllHierachicalSubLists();
                                device.ReadAllObjectsName();
                                if (device.GetObjectName(list.Last()) == null)
                                {
                                    if (JobParam.Item3 != 2)
                                    {
                                        BackGroundQueriesLowPriority.Enqueue(Tuple.Create(JobParam.Item1, JobParam.Item2, 2));
                                        SemBackground.Release();
                                        continue;
                                    }
                                    for (uint i = 0; i < Count; i++) device.ReadObjectName(list[(int)i]);
                                }
                            }
                            break;
                    }
                }
                catch (Exception ex) { Log("Background query failed: " + ex.Message); }
                if (LastJob && BackGroundQueries.IsEmpty && BackGroundQueriesLowPriority.IsEmpty) Log("End of a Background queries batch");
            }
        }
    }
}
