using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Yabe;

namespace YabeMac
{
    // Object tree ("address space"): port of MainDialog.cs AddObjectEntry/FetchStructuredObjects/FetchViewObjects/
    // FetchGroupProperties/AddObjectListOneByOne, MainDialog.Menus.cs m_DeviceTree_AfterSelect (object part),
    // searchToolStripMenuItem1_Click, ToggleViewMenuItem_Click, readPropertiesName / ChangeObjectIdByName and the
    // object type icons (as glyph prefixes).
    public partial class MainWindow
    {
        public enum AddressViewKind { List, Structured, Both, FieldTechnician }

        // ---- state ------------------------------------------------------------------------------
        int _asynchRequestId = 0;                       // original AsynchRequestId: invalidates running async updates
        int _addSpaceCount = 0;                         // original AddSpaceLabel.Tag
        string _highlightAddress = "";                  // text of the object-tree highlight filter
        /// <summary>Original SimplifiedViewFilter (Common Files/SimplifiedViewFilter.xml); null when not loaded.</summary>
        public List<BacnetObjectDescription> SimplifiedViewFilter;
        /// <summary>Original ToogleViewSimplified (Ctrl+Alt+S): inverts the configured mode.</summary>
        public bool ToogleViewSimplified = false;
        string AddrSpaceTxt = "Objects";
        /// <summary>Label of the properties pane ("Properties" or "Properties (Simplified)").</summary>
        public string PropSpaceText = "Properties";
        [ThreadStatic] static List<BacnetObjectId> _structuredViewParents;   // recursion detection for structured views

        AddressViewKind AddressView
        {
            get { Enum.TryParse(AppSettings.Default.Address_Space_Structured_View, true, out AddressViewKind k); return k; }
        }

        /// <summary>True when the simplified (filtered) view is currently active.</summary>
        public bool IsSimplifiedViewActive => ((AddressView == AddressViewKind.FieldTechnician) ^ ToogleViewSimplified) && SimplifiedViewFilter != null;

        TextBlock AddSpaceLabelCtl => this.FindControl<TextBlock>("AddSpaceLabel");
        void SetAddSpaceLabel(string text, int? count = null)
        {
            void Do() { var l = AddSpaceLabelCtl; if (l != null) l.Text = text; if (count.HasValue) _addSpaceCount = count.Value; }
            if (Dispatcher.UIThread.CheckAccess()) Do(); else Dispatcher.UIThread.Post(Do);
        }

        void InitObjectTree()
        {
            LoadSimplifiedViewFilter();
            SetSimplifiedLabels();
            // TbxHighlightAddress_TextChanged
            HighlightAddress.IsVisible = AppSettings.Default.ShowHighLightFilter;
            HighlightAddress.PropertyChanged += (_, ev) =>
            {
                if (ev.Property != TextBox.TextProperty) return;
                _highlightAddress = HighlightAddress.Text ?? "";
                HighlightObjectNodes(_objectRoots, _highlightAddress);
            };
            ObjectTree.SelectionChanged += (_, __) => UpdateObjectMenu();
            // Ctrl+Alt+S: toggle simplified view, Ctrl+Alt+N: read all object names (original address space tree key handler)
            ObjectTree.KeyDown += (_, ev) =>
            {
                if (ev.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && ev.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
                {
                    if (ev.Key == Avalonia.Input.Key.S) { OnSimplifiedView(null, null); ev.Handled = true; }
                    else if (ev.Key == Avalonia.Input.Key.N) { OnGetAllObjectNames(null, null); ev.Handled = true; }
                }
            };
            UpdateObjectMenu();
            AttachRightClickSelects(ObjectTree);
        }

        void LoadSimplifiedViewFilter()
        {
            Task.Run(() =>
            {
                try
                {
                    string[] candidates =
                    {
                        Path.Combine(AppContext.BaseDirectory, "SimplifiedViewFilter.xml"),
                        Path.Combine(AppContext.BaseDirectory, "Common Files", "SimplifiedViewFilter.xml"),
                        Path.Combine(AppContext.BaseDirectory, "../../../../../external/yabe-svn/Yabe/Common Files/SimplifiedViewFilter.xml"),
                    };
                    string file = candidates.FirstOrDefault(File.Exists);
                    if (file == null) return;
                    var xs = new XmlSerializer(typeof(List<BacnetObjectDescription>));
                    using (var sr = new StreamReader(file))
                        SimplifiedViewFilter = (List<BacnetObjectDescription>)xs.Deserialize(sr);
                    Dispatcher.UIThread.Post(SetSimplifiedLabels);
                }
                catch (Exception ex) { Log("Error with SimplifiedViewFilter.xml :" + ex.Message); }
            });
        }

        // ---- icons (as glyphs) ---------------------------------------------------------------------
        /// <summary>Original GetIconNum(): index into the image list of the original (kept for reference by other areas).</summary>
        public static int GetIconNum(BacnetObjectTypes object_type)
        {
            switch (object_type)
            {
                case BacnetObjectTypes.OBJECT_DEVICE: return 2;
                case BacnetObjectTypes.OBJECT_FILE: return 5;
                case BacnetObjectTypes.OBJECT_ANALOG_INPUT:
                case BacnetObjectTypes.OBJECT_ANALOG_OUTPUT:
                case BacnetObjectTypes.OBJECT_ANALOG_VALUE: return 6;
                case BacnetObjectTypes.OBJECT_BINARY_INPUT:
                case BacnetObjectTypes.OBJECT_BINARY_OUTPUT:
                case BacnetObjectTypes.OBJECT_BINARY_VALUE: return 7;
                case BacnetObjectTypes.OBJECT_NETWORK_PORT: return 8;
                case BacnetObjectTypes.OBJECT_GROUP: return 10;
                case BacnetObjectTypes.OBJECT_STRUCTURED_VIEW: return 11;
                case BacnetObjectTypes.OBJECT_TRENDLOG:
                case BacnetObjectTypes.OBJECT_TREND_LOG_MULTIPLE: return 12;
                case BacnetObjectTypes.OBJECT_NOTIFICATION_CLASS: return 13;
                case BacnetObjectTypes.OBJECT_SCHEDULE: return 14;
                case BacnetObjectTypes.OBJECT_CALENDAR: return 15;
                default: return 4;
            }
        }

        /// <summary>Glyph replacing the original image-list icon (0..17 of MainDialog m_ImageList).</summary>
        public static string IconGlyph(int imageIndex) =>
            imageIndex >= 0 && imageIndex <= 17 ? "avares://YabeMac.App/Assets/ObjectIcons/" + imageIndex + ".png" : null;

// ---- node title ----------------------------------------------------------------------------
        /// <summary>Title of an object node: cached object name plus short id when DisplayIdWithName is on.</summary>
        public static string ObjectTitle(BACnetDevice device, BacnetObjectId id)
        {
            string name = id.ToString();
            string objName = device.GetObjectName(id);
            return ComposeObjectTitle(objName, name);
        }

        static string ComposeObjectTitle(string objName, string name)
        {
            if (objName == null) return name;
            if (AppSettings.Default.DisplayIdWithName)
            {
                string abbreviatedName = BACnetDevice.ShortenObjectId(name);
                if (!abbreviatedName.Equals(name)) return objName + " (" + abbreviatedName + ")";
                return objName + " (" + name + ")";
            }
            return objName;
        }

        /// <summary>Original ChangeTreeNodePropertyName: a name was learned for an object node.</summary>
        public void ChangeTreeNodePropertyName(ObjectNode tn, string Name)
        {
            void Do()
            {
                if (string.IsNullOrEmpty(tn.ToolTipText)) tn.ToolTipText = tn.Title;
                if (AppSettings.Default.DisplayIdWithName)
                {
                    string abbreviatedName = BACnetDevice.ShortenObjectId(tn.ToolTipText);
                    if (!abbreviatedName.Equals(tn.ToolTipText))
                        tn.Title = Name + " (" + abbreviatedName + ")";
                    else
                        tn.Title = Name + " (" + System.Threading.Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(tn.ToolTipText.ToLower()) + ")";
                }
                else tn.Title = Name;
                tn.Highlighted = HighlightHit(tn.Title, _highlightAddress);
            }
            if (Dispatcher.UIThread.CheckAccess()) Do(); else Dispatcher.UIThread.Post(Do);
        }

        /// <summary>Update the name of the node(s) of an object in the tree after PROP_OBJECT_NAME was read (used by the properties pane).</summary>
        public void UpdateObjectNodeName(BacnetObjectId id, string name)
        {
            void Walk(IEnumerable<ObjectNode> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n.ObjectId.HasValue && n.ObjectId.Value.Equals(id)) ChangeTreeNodePropertyName(n, name);
                    Walk(n.Children);
                }
            }
            Walk(_objectRoots);
        }

        static bool HighlightHit(string title, string text) =>
            !string.IsNullOrEmpty(text) && title != null && title.ToLower().Contains(text.ToLower());

        void HighlightObjectNodes(IEnumerable<ObjectNode> nodes, string text)
        {
            foreach (var n in nodes)
            {
                n.Highlighted = HighlightHit(n.Title, text);
                HighlightObjectNodes(n.Children, text);
            }
        }

        // ---- AddObjectEntry and the hierarchical fetchers --------------------------------------------
        bool SimplifiedFilteredOut(BacnetObjectId object_id) =>
            IsSimplifiedViewActive && !SimplifiedViewFilter.Exists(o => o.typeId == object_id.Type);

        /// <summary>
        /// Port of AddObjectEntry. Adds the node for an object (and for Group / Structured View children) to
        /// <paramref name="nodes"/> and returns the number of nodes added (0 when filtered by the simplified view).
        /// <paramref name="isRoot"/> = the collection is the top level of the tree.
        /// </summary>
        int AddObjectEntry(BACnetDevice device, string name, BacnetObjectId object_id, IList<ObjectNode> nodes, bool ForceRead = false, int insertLocation = -1, bool isRoot = false)
        {
            bool currentNodeIsRootNodeOfSubTree = false;
            bool recursionDetected = false;

            if (SimplifiedFilteredOut(object_id)) return 0;

            var addrView = AddressView;

            if (object_id.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW)
            {
                if (_structuredViewParents == null) { _structuredViewParents = new List<BacnetObjectId>(); currentNodeIsRootNodeOfSubTree = true; }
                if (_structuredViewParents.Contains(object_id)) recursionDetected = true;
                else _structuredViewParents.Add(object_id);
            }

            if (string.IsNullOrEmpty(name)) name = object_id.ToString();

            ObjectNode node;
            string objName = device.GetObjectName(object_id);

            if (object_id.Type == (BacnetObjectTypes)((uint)BacnetObjectTypes.MAX_BACNET_OBJECT_TYPE + 1) && object_id.Instance == 4194303)
            {
                node = new ObjectNode { Title = "UNKNOWN OBJECT", ToolTipText = "" };
            }
            else if (objName != null)
                node = new ObjectNode { Title = ComposeObjectTitle(objName, name), ToolTipText = name };
            else
                node = new ObjectNode { Title = name, ToolTipText = "" };

            node.Device = device;
            node.ObjectId = object_id;
            node.Glyph = IconGlyph(GetIconNum(object_id.type));
            node.Highlighted = HighlightHit(node.Title, _highlightAddress);

            if (insertLocation >= 0 && insertLocation <= nodes.Count) nodes.Insert(insertLocation, node); else nodes.Add(node);
            int Count = 1;

            //fetch sub properties
            if (object_id.type == BacnetObjectTypes.OBJECT_GROUP)
                Count += FetchGroupProperties(device, object_id, node.Children, ForceRead);
            else if (object_id.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW && (addrView == AddressViewKind.Structured || addrView == AddressViewKind.Both))
            {
                if (recursionDetected)
                    node.Children.Add(new ObjectNode { Title = "WARNING: RECURSIVE NODE DETECTED", Glyph = IconGlyph(16), ToolTipText = "" });
                else
                    Count += FetchViewObjects(device, object_id, node.Children, ForceRead);
            }
            else if (object_id.type == BacnetObjectTypes.OBJECT_DEVICE && isRoot && (addrView == AddressViewKind.Structured || addrView == AddressViewKind.Both))
                Count += FetchStructuredObjects(device, object_id.Instance, node.Children, ForceRead);

            if (device.DeviceIdUnconfigured && object_id.type == BacnetObjectTypes.OBJECT_DEVICE) // Mstp & Remote Device with 0x3FFFFF
            {
                device.deviceId = object_id.Instance;
                Dispatcher.UIThread.Post(() => { var n = _selectedDeviceNode; if (n != null) AddToDeviceClassView(n); });
            }

            if (object_id.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW && _structuredViewParents != null)
            {
                if (_structuredViewParents.Contains(object_id)) _structuredViewParents.Remove(object_id);
                if (currentNodeIsRootNodeOfSubTree) _structuredViewParents = null;
            }
            return Count;
        }

        int FetchStructuredObjects(BACnetDevice device, uint device_id, IList<ObjectNode> nodes, bool ForceRead = false)
        {
            int old_reties = device.channel.Retries;
            int Count = 0;
            try
            {
                device.channel.Retries = 1;       //only do 1 retry
                if (!device.ReadPropertyRequest(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device_id), BacnetPropertyIds.PROP_STRUCTURED_OBJECT_LIST, out IList<BacnetValue> ret, ForceRead))
                {
                    Log("Didn't get response from 'Structured Object List'");
                    return 0;
                }
                foreach (var objid in SortBacnetObjects(ret))
                    Count += AddObjectEntry(device, null, objid, nodes, ForceRead);
            }
            catch (Exception) { return 0; }
            finally { device.channel.Retries = old_reties; }
            return Count;
        }

        int FetchViewObjects(BACnetDevice device, BacnetObjectId object_id, IList<ObjectNode> nodes, bool ForceRead = false)
        {
            int Count = 0;
            try
            {
                if (device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_SUBORDINATE_LIST, out IList<BacnetValue> values, ForceRead))
                {
                    foreach (var objid in SortBacnetObjects(values))
                        Count += AddObjectEntry(device, null, objid, nodes, ForceRead);
                }
                else Log("Couldn't fetch view members");
            }
            catch (Exception ex) { Log("Couldn't fetch view members: " + ex.Message); }
            return Count;
        }

        int FetchGroupProperties(BACnetDevice device, BacnetObjectId object_id, IList<ObjectNode> nodes, bool ForceRead = false)
        {
            int Count = 0;
            try
            {
                // Each property referenced in a group appears as the object it comes from (several times if several properties)
                if (device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_LIST_OF_GROUP_MEMBERS, out IList<BacnetValue> values, ForceRead))
                {
                    foreach (BacnetValue value in values)
                        if (value.Value is BacnetReadAccessSpecification spec)
                            foreach (BacnetPropertyReference p in spec.propertyReferences)
                                Count += AddObjectEntry(device, spec.objectIdentifier.ToString() + ":" + ((BacnetPropertyIds)p.propertyIdentifier).ToString(), spec.objectIdentifier, nodes, ForceRead);
                }
                else Log("Couldn't fetch group members");
            }
            catch (Exception ex) { Log("Couldn't fetch group members: " + ex.Message); }
            return Count;
        }

        List<BacnetObjectId> SortBacnetObjects(IList<BacnetValue> RawList, bool Sort = true)
        {
            var SortedList = new List<BacnetObjectId>();
            foreach (BacnetValue value in RawList)
            {
                if (value.Value is BacnetObjectId id) SortedList.Add(id);
                else if (value.Value == null)
                    SortedList.Add(new BacnetObjectId((BacnetObjectTypes)((uint)BacnetObjectTypes.MAX_BACNET_OBJECT_TYPE + 1), 4194303));
                else if (value.Value is BacnetDeviceObjectReference v)
                    SortedList.Add(v.objectIdentifier);          // Subordinate_List: ignore deviceIdentifier
            }
            if (Sort) SortedList.Sort();
            return SortedList;
        }

        // ---- loading the tree ---------------------------------------------------------------------------
        /// <summary>Clears the object tree (original ClearAddressSpace). deselectDevice also clears the device selection.</summary>
        public void ClearAddressSpace(bool deselectDevice = true)
        {
            _objectRoots.Clear();
            _propertyRows.Clear();
            SetAddSpaceLabel(AddrSpaceTxt, 0);
            ObjectTree.SelectedItem = null;
            if (deselectDevice)
            {
                DeviceTree.SelectedItem = null;
                _selectedDeviceNode = null;
            }
        }

        /// <summary>
        /// Port of the object part of m_DeviceTree_AfterSelect: reads the object list of the device and fills the tree.
        /// force = re-read from the network and clear the cache (Refresh button); selection normally uses the cache.
        /// </summary>
        async Task LoadObjects(BACnetDevice device, bool force) => await LoadObjects(device, force, force);

        async Task LoadObjects(BACnetDevice device, bool force, bool clearCache)
        {
            int reqId = ++_asynchRequestId;                  // disables a possible running async update of the tree
            _objectRoots.Clear();
            _propertyRows.Clear();
            SetAddSpaceLabel(AddrSpaceTxt, 0);
            if (clearCache || !AppSettings.Default.UseObjectsCache) device.ClearCache();

            var addrView = AddressView;
            var result = await Task.Run(() =>
            {
                var roots = new List<ObjectNode>();
                int total = 0; bool ok = true; uint pending = 0; var oneByOne = false;
                int old_timeout = device.channel.Timeout;
                try
                {
                    int countStructured = 0;
                    if (addrView == AddressViewKind.Structured)
                        countStructured = FetchStructuredObjects(device, device.deviceId, roots, force);

                    if (countStructured != 0) total = countStructured;
                    else
                    {
                        // Without PROP_STRUCTURED_OBJECT_LIST we fall back to a flat view of the dictionary
                        if (!device.ReadObjectList(out List<BacnetObjectId> objectList, out uint list_count, force))
                            ok = false;
                        else if (objectList == null && list_count != 0) { oneByOne = true; pending = list_count; }
                        else
                        {
                            if (device.SortableDictionary) objectList.Sort();
                            foreach (var id in objectList)
                                total += AddObjectEntry(device, null, id, roots, false, -1, true);
                        }
                    }
                }
                catch { }
                return (roots, total, ok, oneByOne, pending);
            });

            if (reqId != _asynchRequestId) return;           // selection moved on
            if (!result.ok) { Log("Didn't get response from 'Object List'"); return; }

            foreach (var n in result.roots) _objectRoots.Add(n);
            // If the device name is not set yet, try to update it
            if (_selectedDeviceNode != null && string.IsNullOrEmpty(_selectedDeviceNode.ToolTipText) && device.deviceName != null)
                UpdateDeviceNodeName(device, _selectedDeviceNode);

            if (result.oneByOne)
            {
                SetAddSpaceLabel(AddrSpaceTxt + " : 0 Items / " + result.pending + " expected", 0);
                _ = Task.Run(() => AddObjectListOneByOne(device, result.pending, reqId, force));
            }
            else
            {
                SetAddSpaceLabel(AddrSpaceTxt + " : " + result.total + " Items", result.total);
                _ = LoadNamesInBackground(device, reqId);
            }
        }

        /// <summary>Reads every object name of the device and refreshes the tree labels, so the tree shows names (not just ids).</summary>
        async Task LoadNamesInBackground(BACnetDevice device, int reqId)
        {
            try
            {
                var ids = await Task.Run(() => { device.ReadAllObjectsName(); device.ReadObjectList(out var l, out _); return l; });
                if (reqId != _asynchRequestId || ids == null) return;
                foreach (var id in ids)
                {
                    string name = device.GetObjectName(id);
                    if (!string.IsNullOrEmpty(name)) UpdateObjectNodeName(id, name);
                }
            }
            catch { }
        }

        void AddObjectListOneByOne(BACnetDevice device, uint count, int requestId, bool ForceRead = false)
        {
            try
            {
                int ObjNumber = 0;      // With filtering and possible VIEW, the number is not known at startup
                int ShiftCount = 0;
                for (uint i = 1; i <= count; i++)
                {
                    if (!device.ReadObjectListItem(out BacnetObjectId objId, i, ForceRead)) { Log("Couldn't fetch object list index"); return; }
                    if (requestId != _asynchRequestId) return;       // Selected device is no more the good one

                    var tmp = new List<ObjectNode>();
                    int Nb = AddObjectEntry(device, null, objId, tmp, ForceRead, -1, true);
                    uint idx = i;
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        if (requestId != _asynchRequestId) return;
                        foreach (var n in tmp) _objectRoots.Add(n);
                        ObjNumber += Nb;
                        ShiftCount += Nb - 1;                          // Sometimes (Group, View, Filter) more or less than one item is given
                        if (idx != count) SetAddSpaceLabel(AddrSpaceTxt + " : " + (idx + ShiftCount) + " Items / " + (count + ShiftCount) + " expected", (int)(idx + ShiftCount));
                        else SetAddSpaceLabel(AddrSpaceTxt + " : " + ObjNumber + " Items", ObjNumber);
                    });
                }
            }
            catch (Exception ex) { Log("Error during read: " + ex.Message); }
        }

        async void OnRefreshObjects(object s, RoutedEventArgs e)
        {
            var device = _selectedDeviceNode?.Device ?? SelectedDevice;
            if (device == null) return;
            await LoadObjects(device, true, true);          // manual_refresh_objects_Click
        }

        /// <summary>Reload the tree of the selected device after an object was created/deleted/renamed (the original's "ObjNewDelete"/"ObjRename").</summary>
        async Task ReloadObjects(bool clearCache)
        {
            var device = _selectedDeviceNode?.Device ?? SelectedDevice;
            if (device == null) return;
            await LoadObjects(device, false, clearCache);
        }

        // ---- "Get all object names" (readPropertiesNameToolStripMenuItem_Click / ChangeObjectIdByName) --------
        async void OnGetAllObjectNames(object s, RoutedEventArgs e)
        {
            var device = ActionDevice();
            if (device == null) { await Dialogs.Message("Please select a device node", "Wrong node"); return; }
            Log("Reading all object names ...");
            int _retries = device.channel.Retries;
            device.channel.Retries = 1;
            bool ok;
            try { ok = await Task.Run(() => device.ReadAllObjectsName(true)); }     // forced re-read, as Ctrl+Alt+N in the original
            finally { device.channel.Retries = _retries; }
            if (ok) await ReloadObjects(false);              // update the tree all at once
            else
            {
                // Fail: go one by one in the background
                int req = _asynchRequestId;
                _ = Task.Run(() => ChangeObjectIdByNameOneByOne(_objectRoots.ToList(), device, req));
            }
        }

        void ChangeObjectIdByNameOneByOne(IList<ObjectNode> nodes, BACnetDevice device, int requestId)
        {
            int _retries = device.channel.Retries;
            device.channel.Retries = 1;
            try
            {
                foreach (var tn in nodes)
                {
                    if (requestId != _asynchRequestId) return;
                    if (string.IsNullOrEmpty(tn.ToolTipText) && tn.ObjectId.HasValue)
                    {
                        try
                        {
                            string Name = device.ReadObjectName(tn.ObjectId.Value);
                            if (!string.IsNullOrWhiteSpace(Name)) ChangeTreeNodePropertyName(tn, Name);
                        }
                        catch (Exception ex) { Log("Failed to obtain object name for object " + tn.ObjectId + ": " + ex.Message); }
                    }
                    if (tn.Children.Count > 0) ChangeObjectIdByNameOneByOne(tn.Children.ToList(), device, requestId);
                }
            }
            finally { device.channel.Retries = _retries; }
        }

        // ---- context menu visibility (m_AddressSpaceTree_AfterSelect) ----------------------------------------------
        void UpdateObjectMenu()
        {
            var items = ObjectTreeMenu.Items.OfType<MenuItem>().ToList();
            if (items.Count < 10) return;
            foreach (var it in items) it.IsVisible = false;
            items[0].IsVisible = true;      // Subscribe always visible
            items[8].IsVisible = true;      // Search always visible
            items[9].IsVisible = true;      // Toggle view always visible
            var id = SelectedObjectId;
            if (id == null) return;
            switch (id.Value.type)
            {
                case BacnetObjectTypes.OBJECT_FILE: items[1].IsVisible = items[2].IsVisible = true; break;
                case BacnetObjectTypes.OBJECT_TRENDLOG:
                case BacnetObjectTypes.OBJECT_TREND_LOG_MULTIPLE: items[3].IsVisible = true; break;
                case BacnetObjectTypes.OBJECT_SCHEDULE: items[4].IsVisible = true; break;
                case BacnetObjectTypes.OBJECT_NOTIFICATION_CLASS: items[5].IsVisible = true; break;
                case BacnetObjectTypes.OBJECT_CALENDAR: items[6].IsVisible = true; break;
            }
            if (id.Value.type != BacnetObjectTypes.OBJECT_DEVICE) items[7].IsVisible = true;     // delete
        }

        // ---- search (searchToolStripMenuItem1_Click) ------------------------------------------------------------------
        async void OnSearchObjects(object s, RoutedEventArgs e)
        {
            string text = await Dialogs.Input("Name", "Search object", SelectedObjectNode?.Title ?? "");
            if (text == null) return;
            string find = text.ToLower();
            // Original: first match among the top level nodes; extended here to look inside structured views/groups when no top-level node matches
            ObjectNode hit = _objectRoots.FirstOrDefault(n => (n.Title ?? "").ToLower().Contains(find));
            List<ObjectNode> path = null;
            if (hit == null)
            {
                bool Dfs(ObjectNode n, List<ObjectNode> p)
                {
                    foreach (var c in n.Children)
                    {
                        p.Add(c);
                        if ((c.Title ?? "").ToLower().Contains(find)) { hit = c; return true; }
                        if (Dfs(c, p)) return true;
                        p.RemoveAt(p.Count - 1);
                    }
                    return false;
                }
                foreach (var r in _objectRoots)
                {
                    var p = new List<ObjectNode> { r };
                    if (Dfs(r, p)) { path = p; break; }
                }
            }
            if (hit == null) return;
            if (path != null) foreach (var a in path.Take(path.Count - 1)) a.IsExpanded = true;
            ObjectTree.SelectedItem = hit;
            Dispatcher.UIThread.Post(() => { try { ObjectTree.TreeContainerFromItem(hit)?.BringIntoView(); } catch { } }, DispatcherPriority.Background);
        }

        // ---- simplified view toggle (ToggleViewMenuItem_Click, Ctrl+Alt+S) ---------------------------------------------
        void SetSimplifiedLabels()
        {
            string hdr = ToogleViewSimplified ? "Full Normal View" : "Simplified View";
            SimplifiedViewObjects.Header = hdr;
            if (IsSimplifiedViewActive) { PropSpaceText = "Properties (Simplified)"; AddrSpaceTxt = "Objects (Simplified)"; }
            else { PropSpaceText = "Properties"; AddrSpaceTxt = "Objects"; }
        }

        async void OnSimplifiedView(object s, RoutedEventArgs e)
        {
            var stn = SelectedObjectNode;
            ToogleViewSimplified = !ToogleViewSimplified;
            SetSimplifiedLabels();
            await ReloadObjects(false);
            if (stn?.ObjectId != null)
            {
                // try to select the same object (maybe it is no more displayed, not a problem)
                foreach (var tn in _objectRoots)
                {
                    if (tn.ObjectId.HasValue && tn.ObjectId.Value.Equals(stn.ObjectId.Value)) { ObjectTree.SelectedItem = tn; return; }
                    foreach (var tn2 in tn.Children)
                        if (tn2.ObjectId.HasValue && tn2.ObjectId.Value.Equals(stn.ObjectId.Value)) { tn.IsExpanded = true; ObjectTree.SelectedItem = tn2; return; }
                }
            }
        }

        // ---- double click ------------------------------------------------------------------------------------------------
        // The original has no handler on the address space tree: the default TreeView behaviour (double click toggles the
        // expansion of structured views / groups) applies, which Avalonia does natively.
        void OnObjectDoubleTapped(object s, Avalonia.Input.TappedEventArgs e) { }

        // ---- right click selects the node under the mouse (TreeView_MouseDown) ------------------------------------------------
        void AttachRightClickSelects(TreeView tree)
        {
            tree.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (object _, Avalonia.Input.PointerPressedEventArgs e) =>
            {
                if (!e.GetCurrentPoint(tree).Properties.IsRightButtonPressed) return;
                var v = e.Source as Avalonia.Visual;
                while (v != null && !(v is TreeViewItem)) v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
                tree.SelectedItem = (v as TreeViewItem)?.DataContext;
            }, RoutingStrategies.Tunnel);
        }
    }
}
