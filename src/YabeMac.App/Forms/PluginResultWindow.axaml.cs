using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.BACnet;
using System.Linq;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    public class ResultNode
    {
        public string Title { get; set; }
        public ObservableCollection<ResultNode> Children { get; } = new ObservableCollection<ResultNode>();
        public ResultNode(string title) { Title = title; }
        public ResultNode Add(string title) { var n = new ResultNode(title); Children.Add(n); return n; }
    }

    /// <summary>
    /// Avalonia equivalent of the small plugin result forms (Reliability, StatusFlags, OutOfService, Priorities,
    /// COV_Increment): a device label, a tree of findings and an "empty" message.
    /// </summary>
    public partial class PluginResultWindow : Window
    {
        public PluginResultWindow() { InitializeComponent(); }

        /// <summary>Checks every object of the selected device. check returns a node (or null) per object.</summary>
        public static async Task Run(MainWindow host, string title, string emptyText, double width, double height,
            Func<BACnetDevice, BacnetObjectId, ResultNode> check, BacnetObjectTypes[] filter = null)
        {
            var win = new PluginResultWindow { Title = title, Width = width, Height = height };
            win.Show(host);
            var device = host.SelectedDevice;
            if (device == null) { win.Devicename.Text = "No device selected"; win.Busy.IsVisible = false; return; }
            win.Devicename.Text = device.BacAdr.ToString();
            var found = new List<ResultNode>();
            try
            {
                await Task.Run(() =>
                {
                    device.ReadObjectList(out List<BacnetObjectId> list, out uint count);
                    if (list == null) return;
                    foreach (var id in list.ToList())
                    {
                        if (filter != null && !filter.Contains(id.type)) continue;
                        try { var n = check(device, id); if (n != null) found.Add(n); } catch { }
                    }
                });
            }
            catch { }
            var root = new ObservableCollection<ResultNode>(found);
            win.Tree.ItemsSource = root;
            win.Busy.IsVisible = false;
            win.EmptyList.Text = emptyText;
            win.EmptyList.IsVisible = found.Count == 0;
        }

        /// <summary>"Name (Analog Input:1)" label used by the original plugins.</summary>
        public static string ObjectLabel(BACnetDevice device, BacnetObjectId id)
        {
            string Identifier = device.GetObjectName(id);
            string name = id.ToString();
            if (name.StartsWith("OBJECT_")) name = name.Substring(7);
            if (Identifier != null) return Identifier + " (" + TitleCase(name) + ")";
            return name;
        }

        public static string TitleCase(string s) => System.Threading.Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(s.ToLower());
    }
}
