using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>
    /// Port of MainDialog.Export.cs: "Export device database" (DeviceStorage xml) and EDE file export.
    /// </summary>
    public partial class MainWindow
    {
        const string EDE_EXPORT_TITLE = "EDE file export";
        const string EDE_EXPORT_EXT = "csv";

        // ---- helpers (FetchEndPoint / FetchEndPoints of the original) -------------------------------
        static IEnumerable<DeviceNode> Flatten(IEnumerable<DeviceNode> nodes)
        {
            foreach (var n in nodes.ToList())
            {
                yield return n;
                foreach (var c in Flatten(n.Children)) yield return c;
            }
        }
        IEnumerable<DeviceNode> AllDeviceNodes() =>
            DeviceTree.ItemsSource is IEnumerable<DeviceNode> roots ? Flatten(roots) : Enumerable.Empty<DeviceNode>();

        /// <summary>All known BACnet devices (original FetchEndPoints).</summary>
        public List<BACnetDevice> FetchEndPoints() => AllDeviceNodes().Where(n => n.Device != null).Select(n => n.Device).Distinct().ToList();

        /// <summary>The selected device or null (original FetchEndPoint).</summary>
        public BACnetDevice FetchEndPoint() => SelectedDevice;

        public BACnetDevice FetchDeviceFromDeviceId(uint deviceId) => FetchEndPoints().FirstOrDefault(d => d.deviceId == deviceId);

        async Task<string> PickSaveFile(string title, string suggestedName, string typeName, string pattern)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = pattern.TrimStart('*', '.'),
                FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { pattern } } },
            });
            return f?.TryGetLocalPath();
        }

        async Task<string> PickFolder(string title)
        {
            var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return r.Count > 0 ? r[0].TryGetLocalPath() : null;
        }

        void SetBusy(bool busy) => Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;

        // ---- Export device database ---------------------------------------------------------------
        async void OnExportDeviceDb(object s, RoutedEventArgs e)
        {
            BACnetDevice device = FetchEndPoint();
            if (device == null)
            {
                await Dialogs.Message("Please select a device node", "Wrong node");
                return;
            }

            string file = await PickSaveFile("Export device database", $"Device{device.deviceId}.xml", "xml", "*.xml");
            if (file == null) return;

            SetBusy(true);
            bool removeObject = false;
            string error = null;
            try
            {
                await Task.Run(() =>
                {
                    var storage = new System.IO.BACnet.Storage.DeviceStorage();
                    IList<BacnetValue> value_list;
                    device.ReadPropertyRequest(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.deviceId), BacnetPropertyIds.PROP_OBJECT_LIST, out value_list);
                    var object_list = new LinkedList<BacnetObjectId>();
                    foreach (BacnetValue value in value_list)
                    {
                        if (Enum.IsDefined(typeof(BacnetObjectTypes), ((BacnetObjectId)value.Value).Type))
                            object_list.AddLast((BacnetObjectId)value.Value);
                        else
                            removeObject = true;
                    }

                    foreach (BacnetObjectId object_id in object_list)
                    {
                        IList<BacnetReadAccessResult> multi_value_list;
                        var properties = new BacnetPropertyReference[] { new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_ALL, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL) };
                        device.ReadPropertyMultipleRequest(object_id, properties, out multi_value_list);

                        foreach (BacnetPropertyValue value in multi_value_list[0].values)
                        {
                            try { storage.WriteProperty(object_id, (BacnetPropertyIds)value.property.propertyIdentifier, value.property.propertyArrayIndex, value.value, true); }
                            catch { }
                        }
                    }
                    storage.Save(file);
                });
            }
            catch (Exception ex) { error = ex.Message; }
            finally { SetBusy(false); }

            if (removeObject) Log("All proprietary Objects removed from export");
            if (error != null) await Dialogs.Message("Error during export: " + error, "Export Error");
            else await Dialogs.Message("Done", "Export done");
        }

        // ---- EDE export ------------------------------------------------------------------------------
        void OnExportEdeSelected(object s, RoutedEventArgs e) => ExportDeviceEdeFile(true);
        void OnExportEdeAll(object s, RoutedEventArgs e) => ExportDeviceEdeFile(false);

        static char EdeSep { get { var v = AppSettings.Default.EDE_Separator; return string.IsNullOrEmpty(v) ? ';' : v[0]; } }

        async void ExportDeviceEdeFile(bool selDeviceOnly)
        {
            var endPoints = new List<BACnetDevice>();
            if (selDeviceOnly) { var d = FetchEndPoint(); if (d != null) endPoints.Add(d); }
            else endPoints = FetchEndPoints();
            if (endPoints.Count == 0) return;

            string file;
            bool singleFile = endPoints.Count == 1 || AppSettings.Default.EDE_SingleFile;
            var errors = new List<string>();
            if (singleFile)
            {
                var endPoint = endPoints.First();
                string name = endPoints.Count == 1 ? $"Device{endPoint.deviceId}.{EDE_EXPORT_EXT}" : $"Devices.{EDE_EXPORT_EXT}";
                file = await PickSaveFile(EDE_EXPORT_TITLE, name, "csv", "*.csv");
                if (file == null) return;
                if (file.Length >= 4) file = file.Remove(file.Length - 4, 4);

                SetBusy(true);
                try { await Task.Run(() => ExportDeviceEdeFile(endPoints, file, errors)); }
                finally { SetBusy(false); }
            }
            else
            {
                string dir = await PickFolder($"Select output folder to export {endPoints.Count} EDE files to.");
                if (dir == null) return;
                SetBusy(true);
                try
                {
                    await Task.Run(() =>
                    {
                        foreach (var endPoint in endPoints)
                            ExportDeviceEdeFile(new[] { endPoint }, Path.Combine(dir, $"Device{endPoint.deviceId}.{EDE_EXPORT_EXT}"), errors);
                    });
                }
                finally { SetBusy(false); }
            }

            foreach (var err in errors) await Dialogs.Message($"Export failed:\r\n{err}", EDE_EXPORT_TITLE);
            await Dialogs.Message($"Exported {endPoints.Count} device(s).", EDE_EXPORT_TITLE);
        }

        /// <summary>Exports one or more devices to EDE files (EDE, StateTexts, optionally ObjTypes and Units). Base: https://www.big-eu.org/s/big_ede_2_3.zip</summary>
        void ExportDeviceEdeFile(IEnumerable<BACnetDevice> endPoints, string fileName, List<string> errors)
        {
            var stateTextReferences = new List<string>();
            char Sep = EdeSep;

            using (var edeWriter = new StreamWriter($"{fileName}_EDE.csv"))
            {
                edeWriter.WriteLine("#Engineering-Data-Exchange - B.I.G.-EU");
                edeWriter.WriteLine("PROJECT_NAME");
                edeWriter.WriteLine("VERSION_OF_REFERENCEFILE");
                edeWriter.WriteLine("TIMESTAMP_OF_LAST_CHANGE" + Sep + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                edeWriter.WriteLine("AUTHOR_OF_LAST_CHANGE" + Sep + "YABE Yet Another Bacnet Explorer");
                edeWriter.WriteLine("VERSION_OF_LAYOUT" + Sep + "2.3");
                edeWriter.WriteLine("#mandatory;mandator;mandatory;mandatory;mandatory;optional;optional;optional;optional;optional;optional;optional;optional;optional;optional;optional".Replace(';', Sep));
                edeWriter.WriteLine("# keyname;device obj.-instance;object-name;object-type;object-instance;description;present-value-default;min-present-value;max-present-value;settable;supports COV;hi-limit;low-limit;state-text-reference;unit-code;vendor-specific-addres".Replace(';', Sep));

                foreach (var device in endPoints)
                    ExportDeviceEdeFile(device, edeWriter, stateTextReferences, errors);
            }
            using (var stateTextWriter = new StreamWriter($"{fileName}_StateTexts.csv"))
            {
                stateTextWriter.WriteLine("#State Text Reference");
                if (stateTextReferences.Count > 0)
                {
                    var maxStates = stateTextReferences.Select(stateRef => stateRef.Count(c => c.Equals(Sep)) + 1).Max();
                    var columns = Enumerable.Range(0, maxStates + 1).Select(col => $"Text {col}").ToArray();
                    if (maxStates >= 0) columns[0] = "Reference Number";
                    if (maxStates >= 1) columns[1] += " or Inactive-Text";
                    if (maxStates >= 2) columns[2] += " or Active-Text";
                    stateTextWriter.WriteLine("#" + string.Join(Sep.ToString(), columns));

                    int i = 0;
                    foreach (var stateRef in stateTextReferences)
                    {
                        stateTextWriter.Write($"{i++}" + Sep);
                        stateTextWriter.WriteLine(stateRef);
                    }
                }
            }

            if (AppSettings.Default.EDE_CommonFiles)
            {
                using (var objTypesWriter = new StreamWriter($"{fileName}_ObjTypes.csv"))
                {
                    objTypesWriter.WriteLine("#Encoding of BACnet Object Types" + Sep);
                    objTypesWriter.WriteLine("#Code;Object Type;".Replace(';', Sep));
                    foreach (var objType in EdeCommon.ObjectType_EdeTexts)
                        objTypesWriter.WriteLine($"{(int)objType.Key};{objType.Value}" + Sep);   // (sic) the original keeps ';' here
                }
                using (var unitsWriter = new StreamWriter($"{fileName}_Units.csv"))
                {
                    unitsWriter.WriteLine("#Encoding of BACnet Engineering Units" + Sep);
                    unitsWriter.WriteLine("#Code;Unit Text;".Replace(';', Sep));
                    foreach (var unit in EdeCommon.Unit_EdeTexts)
                        unitsWriter.WriteLine($"{(int)unit.Key};{unit.Value};".Replace(';', Sep));
                }
            }
        }

        /// <summary>Gathers a device's EDE data and writes it to the stream.</summary>
        void ExportDeviceEdeFile(BACnetDevice device, StreamWriter edeWriter, List<string> stateTextReferences, List<string> errors)
        {
            char Sep = EdeSep;
            try
            {
                device.ReadObjectList(out _, out uint ObjCount);

                var propertiesWithText = new BacnetPropertyReference[6] {
                    new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_OBJECT_NAME, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL),
                    new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_DESCRIPTION, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL),
                    new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_UNITS, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL),
                    new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_STATE_TEXT, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL),
                    new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_INACTIVE_TEXT, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL),
                    new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_ACTIVE_TEXT, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL),
                };

                for (uint i = 1; i <= ObjCount; i++)
                {
                    BacnetObjectId Bacobj;
                    device.ReadObjectListItem(out Bacobj, i);
                    string Identifier = "", Description = "", UnitCode = "", InactiveText = "", ActiveText = "";
                    IList<BacnetValue> State_Text = null;

                    try
                    {
                        IList<BacnetReadAccessResult> multi_value_list;
                        device.ReadPropertyMultipleRequest(Bacobj, propertiesWithText, out multi_value_list);
                        BacnetReadAccessResult br = multi_value_list[0];

                        foreach (BacnetPropertyValue pv in br.values)
                        {
                            var pid = (BacnetPropertyIds)pv.property.propertyIdentifier;
                            if (pid == BacnetPropertyIds.PROP_OBJECT_NAME) Identifier = pv.value[0].Value.ToString();
                            if (pid == BacnetPropertyIds.PROP_DESCRIPTION && !(pv.value[0].Value is BacnetError)) Description = pv.value[0].Value.ToString();
                            if (pid == BacnetPropertyIds.PROP_UNITS && !(pv.value[0].Value is BacnetError)) UnitCode = pv.value[0].Value.ToString();
                            if (pid == BacnetPropertyIds.PROP_STATE_TEXT && !(pv.value[0].Value is BacnetError)) State_Text = pv.value;
                            if (pid == BacnetPropertyIds.PROP_INACTIVE_TEXT && !(pv.value[0].Value is BacnetError)) InactiveText = pv.value[0].Value.ToString();
                            if (pid == BacnetPropertyIds.PROP_ACTIVE_TEXT && !(pv.value[0].Value is BacnetError)) ActiveText = pv.value[0].Value.ToString();
                        }
                    }
                    catch { }

                    int? stateTextIdx = null;
                    IEnumerable<string> stateTexts = null;
                    if (State_Text != null)
                        stateTexts = State_Text.Select(sta => sta.Value.ToString());
                    else if ((InactiveText != "") && (ActiveText != ""))
                        stateTexts = new string[] { InactiveText, ActiveText };

                    if (stateTexts != null)
                    {
                        var line = string.Join(Sep.ToString(), stateTexts);
                        stateTextIdx = stateTextReferences.IndexOf(line);
                        if (stateTextIdx == -1)
                        {
                            stateTextIdx = stateTextReferences.Count;
                            stateTextReferences.Add(line);
                        }
                    }

                    if (Description.Contains(Sep)) Description = "\"" + Description + "\"";
                    if (Identifier.Contains(Sep)) Identifier = "\"" + Identifier + "\"";

                    edeWriter.WriteLine(Bacobj.ToString() + Sep + device.deviceId.ToString() + Sep + Identifier + Sep + ((int)Bacobj.type).ToString() + Sep + Bacobj.instance.ToString() + Sep + Description + Sep + Sep + Sep + Sep + Sep + Sep + Sep + Sep + stateTextIdx + Sep + UnitCode);

                    device.UpdateObjectNameMapping(Bacobj, Identifier);
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex.Message); }
        }
    }

    // ---- Common (Units / ObjectTypes csv tables) and CsvLoader of the original -------------------------
    static class EdeCommon
    {
        static IReadOnlyDictionary<BacnetUnitsId, string> _unitTexts;
        static IReadOnlyDictionary<BacnetObjectTypes, string> _objTexts;

        public static IReadOnlyDictionary<BacnetUnitsId, string> Unit_EdeTexts => _unitTexts ??= Load<BacnetUnitsId>("Units.csv", 5);
        public static IReadOnlyDictionary<BacnetObjectTypes, string> ObjectType_EdeTexts => _objTexts ??= Load<BacnetObjectTypes>("ObjectTypes.csv", 3);

        static Dictionary<T, string> Load<T>(string file, int column) where T : struct, Enum
        {
            try
            {
                string path = Path.Combine(Startup.AppDir, file);
                if (File.Exists(path))
                    return new CsvLoader<T>(File.ReadAllText(path, Encoding.Latin1), 0).CreateDictionary(column);
            }
            catch (Exception ex) { MainWindow.Instance?.Log($"Cannot load {file}: {ex.Message}"); }
            return new Dictionary<T, string>();
        }
    }

    /// <summary>Loader for *.csv content; each cell column is associated with an enum value (original CsvLoader).</summary>
    class CsvLoader<T> where T : struct, Enum
    {
        public CsvLoader(string content, int primaryKeyColumn, int firstRow = 1)
        {
            Lines = content.Split('\n');
            PrimaryKeyColumn = primaryKeyColumn;
            FirstRow = firstRow;
        }
        readonly string[] Lines; readonly int PrimaryKeyColumn; readonly int FirstRow;

        public Dictionary<T, string> CreateDictionary(int column, bool skipEmptyCells = true)
        {
            var result = new Dictionary<T, string>();
            int i = 0;
            foreach (var line in Lines)
            {
                if (i++ < FirstRow) continue;
                if (line.Length == 0) continue;
                var cells = line.Split(';');
                if (PrimaryKeyColumn >= cells.Length) throw new IndexOutOfRangeException($"Creating dictionary failed at reading primary key at line {i}!");
                if (!int.TryParse(cells[PrimaryKeyColumn], out var key)) throw new InvalidCastException($"Creating dictionary failed at parsing primary key at line {i}!");
                string val = "";
                if (column < cells.Length) val = cells[column].Replace("\r", "");
                if ((val.Length > 0) || (!skipEmptyCells)) result.Add((T)Enum.ToObject(typeof(T), key), val);
            }
            return result;
        }
    }
}
