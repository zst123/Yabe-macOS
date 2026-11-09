#pragma warning disable SYSLIB0011   // BinaryFormatter: the .YabeMap object-names database uses it in the original
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    // Device actions (MainDialog.Menus.cs): time synchronize, device control, create / hidden / delete object,
    // file download / upload, object names database (File menu) and the auto-store of the names.
    public partial class MainWindow
    {
        // ---- selection helpers (FetchEndPoint / GetObjectLink of MainDialog.cs) ----------------------------------------
        /// <summary>Original ActionDevice(): the device whose objects are shown (last selected device node).</summary>
        BACnetDevice ActionDevice() => _selectedDeviceNode?.Device ?? SelectedDevice;

        /// <summary>Original GetObjectLink(): device + object id of the selected object node (and optionally its expected type).</summary>
        public bool GetObjectLink(out BACnetDevice device, out BacnetObjectId object_id, BacnetObjectTypes ExpectedType = BacnetObjectTypes.MAX_BACNET_OBJECT_TYPE)
        {
            object_id = new BacnetObjectId();
            device = ActionDevice();
            if (device == null) return false;
            var n = SelectedObjectNode;
            if (n?.ObjectId != null)
            {
                object_id = n.ObjectId.Value;
                return object_id.type == ExpectedType || ExpectedType == BacnetObjectTypes.MAX_BACNET_OBJECT_TYPE;
            }
            return false;
        }

        async Task<BACnetDevice> RequireDevice()
        {
            var device = ActionDevice();
            if (device == null) await Dialogs.Message("Please select a device node", "Wrong node");
            return device;
        }

        void InitDeviceActions()
        {
            // original constructor: LoadStoredObjectNames(), SaveObjectNamesTimer, MainDialog_FormClosing (DoSaveObjectNamesIfNecessary)
            LoadStoredObjectNames();
            int intervalMinutes = Math.Max(Math.Min(AppSettings.Default.Auto_Store_Period_Minutes, 480), 1);
            if (intervalMinutes != AppSettings.Default.Auto_Store_Period_Minutes) AppSettings.Default.Auto_Store_Period_Minutes = intervalMinutes;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(intervalMinutes) };
            timer.Tick += (_, __) =>
            {
                int m = Math.Max(Math.Min(AppSettings.Default.Auto_Store_Period_Minutes, 480), 1);
                if (m != AppSettings.Default.Auto_Store_Period_Minutes) AppSettings.Default.Auto_Store_Period_Minutes = m;
                timer.Interval = TimeSpan.FromMinutes(m);
                DoSaveObjectNamesIfNecessary();
            };
            timer.Start();
            Closing += (_, __) => { try { DoSaveObjectNamesIfNecessary(); } catch { } };

            InitObjectTree();
            InitDeviceView();
            Opened += (_, __) => OnDeviceActionsOpened();
        }

        partial void OnDeviceActionsOpened();

        // ==============================================================================================================
        // Time synchronize / Device communication control & reinitialize
        // ==============================================================================================================
        async void OnTimeSynchronize(object s, RoutedEventArgs e)
        {
            var device = await RequireDevice();
            if (device == null) return;
            try
            {
                bool utc = AppSettings.Default.TimeSynchronize_UTC;
                await Task.Run(() =>
                {
                    if (utc) device.channel.SynchronizeTime(device.BacAdr, DateTime.Now.ToUniversalTime(), true);
                    else device.channel.SynchronizeTime(device.BacAdr, DateTime.Now, false);
                });
                await Dialogs.Message("OK", "Time Synchronize");
            }
            catch (Exception ex) { await Dialogs.Message("Time synchronize failed - " + ex.Message, "Time Synchronize"); }
        }

        async void OnDeviceControl(object s, RoutedEventArgs e)
        {
            var device = await RequireDevice();
            if (device == null) return;

            var dlg = new DeviceCommunicationControlDialog();
            await dlg.ShowDialog(this);
            if (!dlg.Accepted) return;

            const string title = "Device Communication Control";
            bool isReinit = dlg.IsReinitialize;
            var state = isReinit ? dlg.ReinitializeState : BacnetReinitializedStates.BACNET_REINIT_IDLE;
            uint duration = dlg.Duration; bool disable = dlg.DisableCommunication; string pwd = dlg.Password;
            try
            {
                bool ok = await Task.Run(() => isReinit
                    ? device.channel.ReinitializeRequest(device.BacAdr, state, pwd)                                         // Reinitialize Device
                    : device.channel.DeviceCommunicationControlRequest(device.BacAdr, duration, disable ? (uint)1 : (uint)0, pwd));   // Device Communication Control
                if (!ok) await Dialogs.Message("Couldn't perform device communication control", title);
                else await Dialogs.Message("OK", title);
            }
            catch (Exception ex) { await Dialogs.Message("Couldn't perform device communication control - " + ex.Message, title); }
        }

        // ==============================================================================================================
        // Create / add hidden / delete object
        // ==============================================================================================================
        async void OnCreateObject(object s, RoutedEventArgs e)
        {
            var device = await RequireDevice();
            if (device == null) return;

            var F = new CreateObject();
            await F.ShowDialog(this);
            if (!F.Accepted) return;
            try
            {
                BacnetObjectTypes type;
                if (F.SelectedTypeIndex >= 0) type = (BacnetObjectTypes)F.SelectedTypeIndex;
                else if (int.TryParse(F.TypeText, out int tn)) type = (BacnetObjectTypes)tn;
                else throw new Exception("Invalid object type");
                uint instance = F.InstanceNumber;
                var initialvalues = new BacnetPropertyValue[1];      // Add the initial property name
                initialvalues[0] = new BacnetPropertyValue();
                initialvalues[0].property.propertyIdentifier = (uint)BacnetPropertyIds.PROP_OBJECT_NAME;
                initialvalues[0].property.propertyArrayIndex = System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL;
                initialvalues[0].value = new BacnetValue[1];
                initialvalues[0].value[0] = new BacnetValue(F.ObjectName);
                // BACnetDevice.CreateObjectRequest() drops its value list, so the client is called directly to keep the initial name.
                await Task.Run(() => device.channel.CreateObjectRequest(device.BacAdr, new BacnetObjectId(type, instance), initialvalues));
                await ReloadObjects(true);                           // m_DeviceTree_AfterSelect("ObjNewDelete")
            }
            catch (Exception ex)
            {
                Log("Error : " + ex.Message);
                await Dialogs.Message("Fail to Create Object", "CreateObject");
            }
        }

        int BinarySearchNodes(IList<ObjectNode> nodes, BacnetObjectId id)
        {
            if (nodes == null || nodes.Count == 0) return -1;
            int begin = 0, end = nodes.Count;
            while (begin < end)
            {
                int mid = (end + begin) / 2;
                if (nodes[mid].ObjectId is BacnetObjectId midId)
                {
                    if (id.CompareTo(midId) < 0) end = mid; else begin = mid + 1;
                }
                else end = mid;
            }
            return begin;
        }

        async void OnAddHiddenObject(object s, RoutedEventArgs e)
        {
            var device = await RequireDevice();
            if (device == null) return;

            var win = new CreateObject(showObjectNameInput: false, title: "Add hidden object", createButtonName: "Add");
            await win.ShowDialog(this);
            if (!win.Accepted) return;

            BacnetObjectTypes objectTypeToAdd;
            if (win.SelectedTypeIndex >= 0) objectTypeToAdd = (BacnetObjectTypes)win.SelectedTypeIndex;       // user selected from the dropdown
            else if (int.TryParse(win.TypeText, out int objectTypeNumber)) objectTypeToAdd = (BacnetObjectTypes)objectTypeNumber;   // proprietary type number typed in
            else
            {
                await Dialogs.Message("Invalid object type specified. Object will not be added.", "Error adding hidden object");
                return;
            }

            uint objectInstanceToAdd = win.InstanceNumber;
            if (objectInstanceToAdd >= 0x3F_FFFF)
            {
                await Dialogs.Message("Invalid object instance number specified. Object will not be added.", "Error adding hidden object");
                return;
            }

            var objectIdToAdd = new BacnetObjectId(objectTypeToAdd, objectInstanceToAdd);

            // cancel any running one-by-one add / background update to prevent unpredictable behaviour
            _asynchRequestId++;

            int positionInListIfSorted = -1;
            if (device.SortableDictionary) positionInListIfSorted = BinarySearchNodes(_objectRoots, objectIdToAdd);

            int addedNodes = AddObjectEntry(device, null, objectIdToAdd, _objectRoots, false, positionInListIfSorted, true);

            if (addedNodes > 0)
            {
                int objectCount = _addSpaceCount + addedNodes;
                SetAddSpaceLabel(AddrSpaceTxt + " : " + objectCount + " Items", objectCount);
                var target = positionInListIfSorted >= 0 ? _objectRoots[positionInListIfSorted] : _objectRoots[_objectRoots.Count - 1];
                ObjectTree.Focus();
                ObjectTree.SelectedItem = target;
                Dispatcher.UIThread.Post(() => { try { ObjectTree.TreeContainerFromItem(target)?.BringIntoView(); } catch { } }, DispatcherPriority.Background);
            }
            else Log("The specified hidden object was not added to the address space list correctly.");
        }

        async void OnDeleteObject(object s, RoutedEventArgs e)
        {
            try
            {
                GetObjectLink(out var device, out var object_id);
                if (device == null || SelectedObjectNode?.ObjectId == null) return;
                if (await Dialogs.OkCancel("Are you sure you want to delete this object ?", object_id.ToString()))
                {
                    await Task.Run(() => device.DeleteObjectRequest(object_id));
                    await ReloadObjects(true);                       // "ObjNewDelete"
                }
            }
            catch (Exception ex)
            {
                Log("Error : " + ex.Message);
                await Dialogs.Message("Fail to Delete Object", "DeleteObject");
            }
        }

        // ==============================================================================================================
        // File download / upload (AtomicReadFile / AtomicWriteFile through Utils.cs/FileTransfers.cs)
        // ==============================================================================================================
        static readonly FilePickerFileType[] AllFiles = { FilePickerFileTypes.All };

        async Task<IStorageFolder> FolderOf(string path)
        {
            try { var dir = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return await StorageProvider.TryGetFolderFromPathAsync(dir); } catch { }
            return null;
        }

        async void OnDownloadFile(object s, RoutedEventArgs e)
        {
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
            bool transferred = false;
            try
            {
                if (!GetObjectLink(out var device, out var object_id, BacnetObjectTypes.OBJECT_FILE)) return;

                //where to store file?
                string last = AppSettings.Default.GUI_LastFilename ?? "";
                var picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    SuggestedFileName = Path.GetFileName(last),
                    SuggestedStartLocation = await FolderOf(last),
                    FileTypeChoices = AllFiles,
                    ShowOverwritePrompt = true,
                });
                string filename = picked?.TryGetLocalPath();
                if (string.IsNullOrEmpty(filename)) return;
                AppSettings.Default.GUI_LastFilename = filename;

                //get file size
                int filesize = await Task.Run(() => FileTransfers.ReadFileSize(device, object_id));
                if (filesize < 0) { await Dialogs.Message("Couldn't read file size", "Error"); return; }

                //display progress
                var progress = new ProgressDialog { Title = "Downloading file ..." };
                progress.Label = "0 of " + (filesize / 1024) + " kb ... (0.0 kb/s)";
                progress.Maximum = filesize;
                progress.Show(this);

                DateTime start = DateTime.Now;
                var transfer = new FileTransfers();
                progress.Cancel += (_, __) => transfer.Cancel = true;
                Action<int> update_progress = position =>
                {
                    double kb_per_sec = (position / 1024) / Math.Max((DateTime.Now - start).TotalSeconds, 0.001);
                    progress.Value = position;
                    progress.Label = string.Format((position / 1024) + " of " + (filesize / 1024) + " kb ... ({0:F1} kb/s)", kb_per_sec);
                };
                FileTransferSettings.SegmentsProposedWindowSize = AppSettings.Default.Segments_ProposedWindowSize;
                try
                {
                    int speed = AppSettings.Default.DefaultDownloadSpeed;
                    await Task.Run(() =>
                    {
                        if (speed == 2) transfer.DownloadFileBySegmentation(device, object_id, filename, update_progress);
                        else if (speed == 1) transfer.DownloadFileByAsync(device, object_id, filename, update_progress);
                        else transfer.DownloadFileByBlocking(device, object_id, filename, update_progress);
                    });
                    transferred = true;
                }
                catch (Exception ex)
                {
                    progress.Finish();
                    await Dialogs.Message("Error during download file: " + ex.Message, "Error");
                    return;
                }
                finally { progress.Finish(); }
            }
            catch { }
            finally { Cursor = Avalonia.Input.Cursor.Default; }

            if (transferred) await Dialogs.Message("Done", "Done");
        }

        async void OnUploadFile(object s, RoutedEventArgs e)
        {
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
            bool transferred = false;
            try
            {
                if (!GetObjectLink(out var device, out var object_id, BacnetObjectTypes.OBJECT_FILE)) return;

                //which file to upload?
                string last = AppSettings.Default.GUI_LastFilename ?? "";
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    SuggestedStartLocation = await FolderOf(last),
                    AllowMultiple = false,
                    FileTypeFilter = AllFiles,
                });
                string filename = files.FirstOrDefault()?.TryGetLocalPath();
                if (string.IsNullOrEmpty(filename)) return;
                AppSettings.Default.GUI_LastFilename = filename;

                //display progress
                int filesize = (int)new FileInfo(filename).Length;
                var progress = new ProgressDialog { Title = "Uploading file ..." };
                progress.Label = "0 of " + (filesize / 1024) + " kb ... (0.0 kb/s)";
                progress.Maximum = filesize;
                progress.Show(this);

                var transfer = new FileTransfers();
                DateTime start = DateTime.Now;
                progress.Cancel += (_, __) => transfer.Cancel = true;
                Action<int> update_progress = position =>
                {
                    double kb_per_sec = (position / 1024) / Math.Max((DateTime.Now - start).TotalSeconds, 0.001);
                    progress.Value = position;
                    progress.Label = string.Format((position / 1024) + " of " + (filesize / 1024) + " kb ... ({0:F1} kb/s)", kb_per_sec);
                };
                try
                {
                    await Task.Run(() => transfer.UploadFileByBlocking(device, object_id, filename, update_progress));
                    transferred = true;
                }
                catch (Exception ex)
                {
                    progress.Finish();
                    await Dialogs.Message("Error during upload file: " + ex.Message, "Error");
                    return;
                }
                finally { progress.Finish(); }
            }
            catch { }
            finally { Cursor = Avalonia.Input.Cursor.Default; }

            if (transferred) await Dialogs.Message("Done", "Done");
        }

        // ==============================================================================================================
        // Object names database (.YabeMap = BinaryFormatter of Dictionary<Tuple<string, BacnetObjectId>, string>)
        // ==============================================================================================================
        static readonly FilePickerFileType[] YabeMapTypes =
        {
            new FilePickerFileType("Yabe Map files") { Patterns = new[] { "*.YabeMap" } },
            FilePickerFileTypes.All,
        };

        /// <summary>Default location of the auto-stored names (original: C:\Yabe Stored Object Names\...).</summary>
        static string DefaultObjectNamesFile => Path.Combine(AppSettings.Dir, "Auto_Stored_Object_Names.YabeMap");

        /// <summary>Maps the assembly names of a file written by the Windows YABE (Yabe.exe / mscorlib) to the ones of this build.</summary>
        class YabeMapBinder : System.Runtime.Serialization.SerializationBinder
        {
            static string Fix(string s) =>
                Regex.Replace(Regex.Replace(s,
                    @"mscorlib(, Version=[^,\]]+, Culture=[^,\]]+, PublicKeyToken=[0-9a-fA-F]+)?", "System.Private.CoreLib"),
                    @"(?<=,\s?)Yabe(?![\w.])(, Version=[^,\]]+, Culture=[^,\]]+, PublicKeyToken=\w+)?", typeof(BacnetObjectId).Assembly.GetName().Name);
            public override Type BindToType(string assemblyName, string typeName)
            {
                string t = Fix(typeName);
                string a = Fix(assemblyName);
                var type = Type.GetType(t + ", " + a, false) ?? Type.GetType(t, false);
                if (type == null && typeName == "System.IO.BACnet.BacnetObjectId") type = typeof(BacnetObjectId);
                return type;
            }
        }

        static Dictionary<Tuple<string, BacnetObjectId>, string> ReadObjectNamesFile(string filename)
        {
            using (var stream = File.Open(filename, FileMode.Open, FileAccess.Read))
                return (Dictionary<Tuple<string, BacnetObjectId>, string>)new BinaryFormatter { Binder = new YabeMapBinder() }.Deserialize(stream);
        }

        static void WriteObjectNamesFile(string filename)
        {
            Dictionary<Tuple<string, BacnetObjectId>, string> copy;
            lock (BACnetDevice.DevicesObjectsName) copy = new Dictionary<Tuple<string, BacnetObjectId>, string>(BACnetDevice.DevicesObjectsName);
            using (var stream = File.Open(filename, FileMode.Create))
                new BinaryFormatter().Serialize(stream, copy);
        }

        async void OnObjectNamesOpen(object s, RoutedEventArgs e)
        {
            string last = AppSettings.Default.Auto_Store_Object_Names_File;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                SuggestedStartLocation = await FolderOf(string.IsNullOrWhiteSpace(last) ? DefaultObjectNamesFile : last),
                FileTypeFilter = YabeMapTypes,
            });
            string filename = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(filename)) return;
            try
            {
                var d = await Task.Run(() => ReadObjectNamesFile(filename));
                if (d != null)
                {
                    BACnetDevice.DevicesObjectsName = d;
                    BACnetDevice.objectNamesChangedFlag = true;
                    Log("Loaded object names from \"" + filename + "\".");
                }
                AppSettings.Default.Auto_Store_Object_Names_File = filename;
                AppSettings.Default.Auto_Store_Object_Names = true;
                AppSettings.Default.Save();
            }
            catch { await Dialogs.Message("File error", "Wrong file"); }
        }

        async void OnObjectNamesSaveAs(object s, RoutedEventArgs e)
        {
            string last = AppSettings.Default.Auto_Store_Object_Names_File;
            var picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "Auto_Stored_Object_Names.YabeMap",
                DefaultExtension = "YabeMap",
                SuggestedStartLocation = await FolderOf(string.IsNullOrWhiteSpace(last) ? DefaultObjectNamesFile : last),
                FileTypeChoices = YabeMapTypes,
            });
            string filename = picked?.TryGetLocalPath();
            if (string.IsNullOrEmpty(filename)) return;
            try
            {
                await Task.Run(() => WriteObjectNamesFile(filename));
                Log("Saved object names to \"" + filename + "\".");
                AppSettings.Default.Auto_Store_Object_Names_File = filename;
                AppSettings.Default.Auto_Store_Object_Names = true;
                AppSettings.Default.Save();
            }
            catch { await Dialogs.Message("File error", "Wrong file"); }
        }

        async void OnObjectNamesClean(object s, RoutedEventArgs e)
        {
            if (!await Dialogs.OkCancel("Clean all " + BACnetDevice.DevicesObjectsName.Count + " entries from \"" + AppSettings.Default.Auto_Store_Object_Names_File + "\", really?", "Name database suppression")) return;
            BACnetDevice.DevicesObjectsName = new Dictionary<Tuple<string, BacnetObjectId>, string>();
            Log("Created new object names dictionary.");
            BACnetDevice.objectNamesChangedFlag = true;
            DoSaveObjectNames();
            // Enumerate each transport layer and each parent / routed device: back to the "Device id - address" texts
            foreach (var transport in _networkRoot.Children)
                foreach (var node in transport.Children)
                {
                    if (node.Device != null) { node.Title = DeviceTitle(node.Device, RESTRICT_ROUTED_ADDRESS_PRINTING_TO_SUB_TREE_NODES_ONLY); node.ToolTipText = ""; }
                    foreach (var sub in node.Children)
                        if (sub.Device != null) { sub.Title = DeviceTitle(sub.Device, false); sub.ToolTipText = ""; }
                }
            ClearAddressSpace();
        }

        // ---- auto store (LoadStoredObjectNames / DoSaveObjectNames) ----------------------------------------------------------------
        int _saveFaultCount = 0;

        void LoadStoredObjectNames()
        {
            var st = AppSettings.Default;
            if (!st.Auto_Store_Object_Names) return;
            string fileTotal = st.Auto_Store_Object_Names_File;
            if (string.IsNullOrWhiteSpace(fileTotal)) { fileTotal = DefaultObjectNamesFile; st.Auto_Store_Object_Names_File = fileTotal; }
            try
            {
                string file = Path.GetFileName(fileTotal);
                string directory = Path.GetDirectoryName(fileTotal);
                if (string.IsNullOrWhiteSpace(file))
                {
                    file = "Auto_Stored_Object_Names.YabeMap";
                    fileTotal = Path.Combine(directory, file);
                    st.Auto_Store_Object_Names_File = fileTotal;
                }
                if (File.Exists(fileTotal))
                {
                    var d = ReadObjectNamesFile(fileTotal);
                    if (d != null) { BACnetDevice.DevicesObjectsName = d; Log("Loaded object names from \"" + fileTotal + "\"."); }
                }
                else if (!Directory.Exists(directory))
                {
                    try { Directory.CreateDirectory(directory); Log("Created directory \"" + directory + "\"."); }
                    catch
                    {
                        Log("Error trying to setup the auto-save object names function: The directory \"" + directory + "\" does not exist, and Yabe cannot create this directory. Try changing the Auto_StoreObject_Names_File setting to a different path.");
                        st.Auto_Store_Object_Names = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Exception trying to setup the auto-save object names function: " + ex.Message + ". Try resetting the Auto_StoreObject_Names_File setting to a valid file path.");
                st.Auto_Store_Object_Names = false;
            }
        }

        void DoSaveObjectNamesIfNecessary(string path = null)
        {
            if (AppSettings.Default.Auto_Store_Object_Names && BACnetDevice.objectNamesChangedFlag) DoSaveObjectNames();
        }

        void DoSaveObjectNames(string path = null)
        {
            var st = AppSettings.Default;
            string fileTotal = string.IsNullOrWhiteSpace(path) ? st.Auto_Store_Object_Names_File : path;
            if (string.IsNullOrWhiteSpace(fileTotal))
            {
                Log("Error trying to auto-save object names to file: There is no file specified. Try resetting the Auto_StoreObject_Names_File setting to a valid file path.");
                st.Auto_Store_Object_Names = false;
                return;
            }
            try
            {
                string file = Path.GetFileName(fileTotal);
                string directory = Path.GetDirectoryName(fileTotal);
                if (string.IsNullOrWhiteSpace(file))
                {
                    if (path == null) file = "Auto_Stored_Object_Names.YabeMap";
                    else { var d = DateTime.Now; file = "New_Object_Names_File_" + d.Year + "-" + d.Month + "-" + d.Day + "_" + d.Hour + "_" + d.Minute + ".YabeMap"; }
                    fileTotal = Path.Combine(directory, file);
                    st.Auto_Store_Object_Names_File = fileTotal;
                }
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    try { Directory.CreateDirectory(directory); }
                    catch (UnauthorizedAccessException)
                    {
                        Log("Error trying to auto-save object names to file: The directory \"" + directory + "\" does not exist, and Yabe does not have permissions to automatically create this directory. Try changing the Auto_StoreObject_Names_File setting to a different path.");
                        st.Auto_Store_Object_Names = false;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Exception trying to auto-save object names to file: " + ex.Message + ". Try resetting the Auto_StoreObject_Names_File setting to a valid file path.");
                st.Auto_Store_Object_Names = false;
                return;
            }

            try
            {
                WriteObjectNamesFile(fileTotal);
                BACnetDevice.objectNamesChangedFlag = false;
                _saveFaultCount = 0;
                Log("Saved object names to \"" + fileTotal + "\".");
            }
            catch (Exception ex)
            {
                _saveFaultCount++;
                const int maxFault = 3;
                if (_saveFaultCount < maxFault)
                    Log("Exception trying to auto-save object names to file: " + ex.Message + ". We will retry " + (maxFault - _saveFaultCount) + " more time(s).");
                else
                {
                    Log("Exception trying to auto-save object names to file: " + ex.Message + ". This error happened " + _saveFaultCount + " times, so auto-save is being disabled. Try resetting the Auto_StoreObject_Names_File setting to a valid file path.");
                    st.Auto_Store_Object_Names = false;
                }
            }
        }
    }
}
