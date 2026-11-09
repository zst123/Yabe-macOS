using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>Port of the ListAnalogValues plugin form.</summary>
    public partial class AnalogValuesWindow : Window
    {
        public class Row
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public int Instance { get; set; }
            public string Description { get; set; }
            public float PresentValue { get; set; }
            public float RelinquishDefault { get; set; }
        }

        readonly List<Row> rows = new List<Row>();
        public AnalogValuesWindow() { InitializeComponent(); }

        public static async Task Run(MainWindow host, BacnetObjectTypes[] filter)
        {
            var win = new AnalogValuesWindow();
            win.Show(host);
            var device = host.SelectedDevice;
            if (device == null) { win.Devicename.Text = "No device selected"; return; }
            win.Devicename.Text = device.BacAdr.ToString();
            await Task.Run(() =>
            {
                device.ReadObjectList(out List<BacnetObjectId> list, out uint count);
                if (list == null) return;
                foreach (var object_id in list.ToList())
                {
                    if (!filter.Contains(object_id.type)) continue;
                    try
                    {
                        string Identifier = device.GetObjectName(object_id);
                        IList<BacnetValue> value;
                        device.channel.ReadPropertyRequest(device.BacAdr, object_id, BacnetPropertyIds.PROP_PRESENT_VALUE, out value);
                        float pv = float.Parse(value[0].Value.ToString());
                        device.channel.ReadPropertyRequest(device.BacAdr, object_id, BacnetPropertyIds.PROP_DESCRIPTION, out value);
                        string descr = value[0].Value.ToString();
                        bool ret = device.channel.ReadPropertyRequest(device.BacAdr, object_id, BacnetPropertyIds.PROP_RELINQUISH_DEFAULT, out value);
                        float rd = float.Parse(value[0].Value.ToString());
                        if (ret)
                        {
                            string name = object_id.ToString();
                            if (name.StartsWith("OBJECT_")) name = name.Substring(7);
                            string[] n1 = name.Split(':');
                            win.rows.Add(new Row { Name = Identifier ?? "", Type = n1[0], Instance = int.Parse(n1[1]), Description = descr, PresentValue = pv, RelinquishDefault = rd });
                        }
                    }
                    catch { }
                }
            });
            win.Grid.ItemsSource = win.rows.ToList();
        }

        async void OnExportCsv(object s, RoutedEventArgs e)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                FileTypeChoices = new[] { new FilePickerFileType("CSV file (*.csv)") { Patterns = new[] { "*.csv" } }, FilePickerFileTypes.All },
                DefaultExtension = "csv",
            });
            string path = f?.TryGetLocalPath();
            if (path == null) return;
            var sb = new StringBuilder();
            foreach (var h in new[] { "Object Name", "Object Type", "Instance Number", "Description", "Present Value", "Relinquish Default" }) sb.Append(h + ';');
            sb.Append("\r\n");
            foreach (var r in rows)
            {
                foreach (var c in new object[] { r.Name, r.Type, r.Instance, r.Description, r.PresentValue, r.RelinquishDefault }) sb.Append(c + ";");
                sb.Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString() + "\r\n");
            await Dialogs.Message("Data are exported!", "Message", this);
        }
    }
}
