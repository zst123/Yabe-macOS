using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>Port of the YabeDeviceReport plugin form ("Report of All Devices").</summary>
    public partial class DeviceReportWindow : Window
    {
        const int TAB_SIZE = 8;
        class Dev
        {
            public string Name; public BacnetObjectId Id; public BacnetAddress Address;
            public string DevIdString => $"[DEVICE:{Id.instance}]";
            public string Format(int maxName, int maxId) =>
                string.Format("{0}{3}{1}{4}{2}", Name ?? "", DevIdString, Address.ToString(false),
                    new string('\t', (maxName / TAB_SIZE) - ((Name == null) ? 0 : (Name.Length / TAB_SIZE)) + 1),
                    new string('\t', (maxId / TAB_SIZE) - (DevIdString.Length / TAB_SIZE) + 1));
        }
        List<Dev> devs = new List<Dev>();
        int maxName, maxId;

        public DeviceReportWindow() { InitializeComponent(); }

        public static async Task Run(MainWindow host)
        {
            var devices = host.FetchEndPoints();
            if (devices.Count == 0) { await Dialogs.Message("No devices for export.", "Export Devices"); return; }
            var win = new DeviceReportWindow();
            win.Show(host);
            win.txtReport.Text = "Collecting...";
            await Task.Run(() =>
            {
                foreach (var d in devices)
                {
                    string identifier = null;
                    try { identifier = d.ReadObjectName(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, d.deviceId)); } catch { }
                    if (identifier == null) identifier = "Device " + d.deviceId + " - " + d.BacAdr.ToString(false);
                    var dev = new Dev { Name = identifier, Id = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, d.deviceId), Address = d.BacAdr };
                    win.maxName = Math.Max(win.maxName, identifier.Length);
                    win.maxId = Math.Max(win.maxId, dev.DevIdString.Length);
                    win.devs.Add(dev);
                }
            });
            win.Refresh();
        }

        void Refresh()
        {
            if (devs.Count == 0) return;
            IEnumerable<Dev> sorted;
            if (radSortAddBytesType.IsChecked == true) sorted = devs.OrderBy(d => d.Address.type).ThenBy(d => d.Address.ToString());
            else if (radSortAlphabetical.IsChecked == true) sorted = devs.OrderBy(d => d.Name, StringComparer.CurrentCulture);
            else if (radSortInstance.IsChecked == true) sorted = devs.OrderBy(d => d.Id.instance);
            else sorted = devs.OrderBy(d => d.Address.ToString());
            var sb = new StringBuilder();
            foreach (var d in sorted) sb.AppendLine(d.Format(maxName, maxId));
            txtReport.Text = sb.ToString();
        }

        void OnSortChanged(object s, RoutedEventArgs e) { if (s is RadioButton rb && rb.IsChecked == true && devs != null) Refresh(); }

        async void OnCopy(object s, RoutedEventArgs e)
        {
            string forCopy = txtReport.Text ?? "";
            while (forCopy.Contains("\t\t")) forCopy = forCopy.Replace("\t\t", "\t");
            var cb = GetTopLevel(this)?.Clipboard;
            if (cb != null) await cb.SetTextAsync(forCopy);
        }
    }
}
