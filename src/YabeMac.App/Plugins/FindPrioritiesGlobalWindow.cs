using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>Tree node with an IsExpanded flag (WinForms TreeView.ExpandAll / CollapseAll).</summary>
    public class FpgNode : INotifyPropertyChanged
    {
        bool _exp;
        public string Title { get; set; }
        public ObservableCollection<FpgNode> Children { get; } = new ObservableCollection<FpgNode>();
        public bool IsExpanded { get => _exp; set { _exp = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Avalonia port of YabePlugins/FindPrioritiesGlobal: reads the priority array of every commandable object of the
    /// selected devices and lists the priority levels (filter) that contain a value, with CSV export.
    /// </summary>
    public partial class FindPrioritiesGlobalWindow : Window
    {
        const int PATIENCE_INTERVAL = 7500;

        readonly MainWindow _yabe;
        public bool[] aPrioFilter = new bool[16];
        public static readonly string[] sPrioNames = {
            "Manual-Life Safety", "Automatic-Life Safety", "Available", "Available", "Critical Equipment Control", "Minimum On/Off",
            "Available", "Manual Operator", "Available", "Available", "Available", "Available", "Available", "Available", "Available", "Available" };
        public string GUI_LastFilename = "";

        class DevExport { public BACnetDevice Device; public string Name; public List<PointExport> Points = new List<PointExport>(); public override string ToString() => Name; }
        class PointExport
        {
            public bool[] aPriosSet = new bool[16];
            public string[] aValues = new string[16];
            public DevExport ParentDevice; public BacnetObjectId ObjectID; public string Name, ObjectName;
        }

        List<DevExport> _commonDevices, _selectedDevices;
        List<PointExport> _pointListForDisplay = new List<PointExport>();

        readonly ObservableCollection<GcItem> deviceItems = new ObservableCollection<GcItem>();
        readonly ListBox DeviceList;
        readonly ObservableCollection<FpgNode> nodes = new ObservableCollection<FpgNode>();
        readonly TreeView treeView1;
        readonly CheckBox[] Prio = new CheckBox[16];
        readonly CheckBox PrintValues = new CheckBox { Content = "Print Values", IsChecked = true };
        readonly CheckBox IncludePriorityLevelNames = new CheckBox { Content = "Include Priority Level Names" };
        readonly Button cmdPopulateDevices = Ui.Btn("Search Devices", 140, 36);
        readonly Button cmdPopulatePoints = Ui.Btn("Search Priorities", 140, 36);
        readonly Button buttonExport = Ui.Btn("Export", 140, 36);
        readonly ProgressBar progBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16 };
        readonly TextBlock PatienceLabel = new TextBlock
        {
            Text = "Please be patient - the plugin has absolutely no prior knowledge of the nework and must poll each device individually.",
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap
        };
        readonly DispatcherTimer PatienceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PATIENCE_INTERVAL) };

        const string QuickHelp = "Find Priorities Global quick help:\n\nThis plugin iterates through all \ndevices seeking for commandable \nobjects which contain values other\nthan NULL at  selected priority levels.\n\nThe priority levels may be \nselected as a filter. \nThis means, only selected \npriorities will be compared \nagainst NULL values.\n\nBy default, the priority \nlevel 8 (Manual Operator) is \nintentionally not selected, but\nmay be activated at any time in\naddition to other priority levels.\n\nThe export button allows to export\nthe result as a csv-file which may \nbe processed using Excel or other \ntools.\n\nThe checkox \"Include Priority Level \nNames\" combines the priority level \nwith the predefined meaning taken \nfrom the BACnet standard, e.g. \n\"Automatic LifeSafety\" or \n\"Manual Operator\".\n\nThe option Print Values reads and \ndisplays the actual value at the \npriority level.";

        public FindPrioritiesGlobalWindow(MainWindow yabe)
        {
            _yabe = yabe;
            Title = "Yabe FindPrioritiesGlobal";
            Width = 1234; Height = 721; MinWidth = 1000; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            DeviceList = Ui.List(deviceItems, true);
            treeView1 = new TreeView
            {
                ItemsSource = nodes, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<FpgNode>((n, _) => new TextBlock { Text = n.Title }, n => n.Children)
            };
            treeView1.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<TreeViewItem>())
            { Setters = { new Avalonia.Styling.Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded", BindingMode.TwoWay)) } });

            for (int i = 0; i < 16; i++)
            {
                Prio[i] = new CheckBox { Content = (i + 1) + " - " + sPrioNames[i], IsChecked = i != 7 };
                Prio[i].IsCheckedChanged += (_, __) => UpdatePrioFilter();
            }
            UpdatePrioFilter();

            // device column
            var devBottom = new StackPanel { Margin = new Thickness(0, 4, 0, 0) }; devBottom.Children.Add(cmdPopulateDevices);
            var devCol = new DockPanel(); DockPanel.SetDock(devBottom, Dock.Bottom); devCol.Children.Add(devBottom); devCol.Children.Add(DeviceList);

            // tree column
            var collapse = Ui.Btn("x", 25, 19); var expand = Ui.Btn("o", 25, 19);
            collapse.Padding = new Thickness(0); expand.Padding = new Thickness(0);
            var treeBottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            treeBottom.Children.Add(cmdPopulatePoints); treeBottom.Children.Add(collapse); treeBottom.Children.Add(expand);
            var treeCol = new DockPanel(); DockPanel.SetDock(treeBottom, Dock.Bottom); treeCol.Children.Add(treeBottom); treeCol.Children.Add(treeView1);
            collapse.VerticalAlignment = VerticalAlignment.Center; expand.VerticalAlignment = VerticalAlignment.Center;

            // options column: quick help, priority filter, options, export
            var all = Ui.Btn("Select All", 74, 23); var none = Ui.Btn("Select None", 74, 23);
            all.FontSize = 11; none.FontSize = 11; all.Padding = new Thickness(0); none.Padding = new Thickness(0);
            var filter = new StackPanel { Spacing = 0 };
            foreach (var c in Prio) filter.Children.Add(c);
            filter.Children.Add(Ui.Row(all, none));
            var opt = new StackPanel { Spacing = 4 };
            opt.Children.Add(IncludePriorityLevelNames); opt.Children.Add(PrintValues);
            var optionsCol = new StackPanel { Spacing = 8, Margin = new Thickness(8, 0, 0, 0) };
            optionsCol.Children.Add(Ui.Group("Priority levels to check", filter, 230));
            optionsCol.Children.Add(Ui.Group("Options", opt, 230));
            optionsCol.Children.Add(buttonExport);
            buttonExport.HorizontalAlignment = HorizontalAlignment.Left;
            var help = new TextBlock { Text = QuickHelp, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.NoWrap };
            var optHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            optHost.Children.Add(optionsCol); optHost.Children.Add(help);
            var optScroll = new ScrollViewer { Content = optHost, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };

            var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("296*,4,464*,4,432*"), Margin = new Thickness(12, 12, 12, 4) };
            Grid.SetColumn(devCol, 0); Grid.SetColumn(treeCol, 2); Grid.SetColumn(optScroll, 4);
            var s1 = new GridSplitter { Width = 4, Background = Brushes.Transparent }; Grid.SetColumn(s1, 1);
            var s2 = new GridSplitter { Width = 4, Background = Brushes.Transparent }; Grid.SetColumn(s2, 3);
            cols.Children.Add(devCol); cols.Children.Add(s1); cols.Children.Add(treeCol); cols.Children.Add(s2); cols.Children.Add(optScroll);

            var bottom = new StackPanel { Margin = new Thickness(12, 0, 12, 8), Spacing = 3 };
            bottom.Children.Add(PatienceLabel); bottom.Children.Add(progBar);
            var root = new DockPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(cols);
            Content = root;

            cmdPopulateDevices.Click += (_, __) => SearchDevices();
            cmdPopulatePoints.Click += (_, __) => SearchPriorities();
            collapse.Click += (_, __) => SetExpanded(nodes, false);
            expand.Click += (_, __) => SetExpanded(nodes, true);
            all.Click += (_, __) => { foreach (var c in Prio) c.IsChecked = true; UpdatePrioFilter(); };
            none.Click += (_, __) => { foreach (var c in Prio) c.IsChecked = false; UpdatePrioFilter(); };
            buttonExport.Click += (_, __) => ExportCsv();
            PatienceTimer.Tick += (_, __) => { PatienceTimer.Stop(); PatienceLabel.IsVisible = true; };
            Closed += (_, __) => PatienceTimer.Stop();
        }

        static void SetExpanded(IEnumerable<FpgNode> l, bool v) { foreach (var n in l) { n.IsExpanded = v; SetExpanded(n.Children, v); } }

        void UpdatePrioFilter() { for (int i = 0; i < 16; i++) aPrioFilter[i] = Prio[i].IsChecked == true; }
        void StartPatienceTimer() { PatienceTimer.Stop(); PatienceTimer.Interval = TimeSpan.FromMilliseconds(PATIENCE_INTERVAL); PatienceTimer.Start(); }
        void ResetPatience() { PatienceTimer.Stop(); PatienceLabel.IsVisible = false; }
        void SetProgress(int v) { if (Dispatcher.UIThread.CheckAccess()) progBar.Value = v; else Dispatcher.UIThread.Post(() => progBar.Value = v); }

        List<BACnetDevice> EnumerateYabeDevices()
        {
            var list = new List<BACnetDevice>();
            void Collect(IEnumerable<DeviceNode> l)
            {
                if (l == null) return;
                foreach (var n in l) { if (n.Device != null && !list.Contains(n.Device)) list.Add(n.Device); Collect(n.Children); }
            }
            Collect(_yabe.DeviceTree.ItemsSource as IEnumerable<DeviceNode>);
            return list;
        }

        async void SearchDevices()
        {
            StartPatienceTimer();
            progBar.Value = 0;
            nodes.Clear();
            var devices = EnumerateYabeDevices();
            cmdPopulateDevices.IsEnabled = false;
            try
            {
                _commonDevices = await Task.Run(() =>
                {
                    int progTotal = devices.Count + 1, prog = 0;
                    var deviceList = new List<DevExport>();
                    foreach (var device in devices)
                    {
                        var devExport = new DevExport { Device = device, Name = device.deviceId.ToString() };
                        string identifier = null;
                        try { identifier = device.ReadObjectName(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.deviceId)); } catch { }
                        devExport.Name = identifier + " [" + device.deviceId.ToString() + "] ";
                        if (deviceList.Find(item => item.Device == device) == null) deviceList.Add(devExport);
                        prog++; SetProgress((int)(100 * prog / progTotal));
                    }
                    return deviceList;
                });
            }
            finally { cmdPopulateDevices.IsEnabled = true; }
            _commonDevices.Sort((a, b) => a.Name.CompareTo(b.Name));
            deviceItems.Clear();
            foreach (var d in _commonDevices) deviceItems.Add(new GcItem { Name = d.Name, Text = d.Name, Tag = d });
            progBar.Value = 100;
            ResetPatience();
            DeviceList.SelectAll();   // select all items in the ListBox
        }

        async void SearchPriorities()
        {
            nodes.Clear();
            _selectedDevices = Ui.Selected(DeviceList, deviceItems).Select(i => (DevExport)i.Tag).ToList();
            if (_selectedDevices.Count == 0) { await Dialogs.Message("No device(s) selected.", "Error", this); return; }

            StartPatienceTimer();
            progBar.Value = 0;
            var devs = _selectedDevices;
            var filter = (bool[])aPrioFilter.Clone();
            cmdPopulatePoints.IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    int progTotal = devs.Count + 2, prog = 0;
                    foreach (var d in devs) { PopulatePointsForDevice(d, filter); prog++; SetProgress((int)(100 * prog / progTotal)); }
                });
            }
            finally { cmdPopulatePoints.IsEnabled = true; }

            _pointListForDisplay = new List<PointExport>();
            foreach (var device in devs) foreach (var point in device.Points) _pointListForDisplay.Add(point);

            string lastParentName = ""; FpgNode cur = null;
            bool names = IncludePriorityLevelNames.IsChecked == true, values = PrintValues.IsChecked == true;
            var roots = new List<FpgNode>();
            foreach (var point in _pointListForDisplay)
            {
                if (lastParentName != point.ParentDevice.Name)
                {
                    lastParentName = point.ParentDevice.Name;
                    cur = new FpgNode { Title = point.ParentDevice.Name };
                    roots.Add(cur);
                }
                cur.Children.Add(new FpgNode { Title = point.Name });
                for (int i = 0; i < 16; i++)
                    if (point.aPriosSet[i])
                    {
                        string sItem = "Prio: " + (i + 1);
                        if (names) sItem = sItem + " " + sPrioNames[i];
                        if (values) sItem = sItem + " Value: " + point.aValues[i];
                        cur.Children.Add(new FpgNode { Title = sItem });
                    }
            }
            foreach (var r in roots) nodes.Add(r);
            progBar.Value = 100;
            ResetPatience();
        }

        static bool SupportsPriorityArray(BacnetObjectTypes t)
        {
            // Only these objects contain or may contain a PriorityArray property (135-2020)
            switch (t)
            {
                case BacnetObjectTypes.OBJECT_ACCESS_DOOR:
                case BacnetObjectTypes.OBJECT_ANALOG_OUTPUT:
                case BacnetObjectTypes.OBJECT_ANALOG_VALUE:
                case BacnetObjectTypes.OBJECT_BINARY_LIGHTING_OUTPUT:
                case BacnetObjectTypes.OBJECT_BINARY_OUTPUT:
                case BacnetObjectTypes.OBJECT_BINARY_VALUE:
                case BacnetObjectTypes.OBJECT_BITSTRING_VALUE:
                case BacnetObjectTypes.OBJECT_CHANNEL:
                case BacnetObjectTypes.OBJECT_CHARACTERSTRING_VALUE:
                case BacnetObjectTypes.OBJECT_DATE_VALUE:
                case BacnetObjectTypes.OBJECT_DATE_PATTERN_VALUE:
                case BacnetObjectTypes.OBJECT_DATETIME_VALUE:
                case BacnetObjectTypes.OBJECT_DATETIME_PATTERN_VALUE:
                case BacnetObjectTypes.OBJECT_INTEGER_VALUE:
                case BacnetObjectTypes.OBJECT_LARGE_ANALOG_VALUE:
                case BacnetObjectTypes.OBJECT_LIGHTING_OUTPUT:
                case BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT:
                case BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE:
                case BacnetObjectTypes.OBJECT_OCTETSTRING_VALUE:
                case BacnetObjectTypes.OBJECT_POSITIVE_INTEGER_VALUE:
                case BacnetObjectTypes.OBJECT_TIME_VALUE:
                case BacnetObjectTypes.OBJECT_TIME_PATTERN_VALUE:
                    return true;
            }
            return false;
        }

        void PopulatePointsForDevice(DevExport devExport, bool[] prioFilter)
        {
            var device = devExport.Device;
            device.ReadObjectList(out _, out uint Count);
            devExport.Points.Clear();
            for (uint j = 1; j <= Count; j++)
            {
                device.ReadObjectListItem(out BacnetObjectId bobj_id, j);
                if (!SupportsPriorityArray(bobj_id.type)) continue;

                var point = new PointExport { ParentDevice = devExport, ObjectID = bobj_id, Name = bobj_id.ToString() };
                string objectName = device.ReadObjectName(bobj_id);
                string identifier = objectName + " [" + bobj_id.ToString() + "] ";

                bool bFound = false;
                try
                {
                    device.channel.ReadPropertyRequest(device.BacAdr, bobj_id, BacnetPropertyIds.PROP_PRIORITY_ARRAY, out IList<BacnetValue> values);
                    if (values.Count == 16)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            point.aPriosSet[i] = false;
                            if (values[i].Value != null && prioFilter[i])
                            {
                                bFound = true;   // any prio set and not filtered: the item is displayed
                                point.aPriosSet[i] = true;
                                point.aValues[i] = values[i].Value.ToString();
                            }
                        }
                    }
                }
                catch { }
                point.Name = identifier; point.ObjectName = objectName;
                if (devExport.Points.Find(item => item.ObjectID.Equals(bobj_id)) == null && bFound) devExport.Points.Add(point);
            }
        }

        async void ExportCsv()
        {
            if (nodes.Count == 0) { await Dialogs.Message(" List is empty, nothing to export."); return; }
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = string.IsNullOrEmpty(GUI_LastFilename) ? null : Path.GetFileName(GUI_LastFilename),
                FileTypeChoices = new[] { new FilePickerFileType("Yabe Export files (*.csv)") { Patterns = new[] { "*.csv" } }, FilePickerFileTypes.All },
                DefaultExtension = "csv",
            });
            string filename = f?.TryGetLocalPath();
            if (filename == null) return;
            GUI_LastFilename = filename;

            try
            {
                var csv = new StringBuilder();
                string sep = ";";
                bool names = IncludePriorityLevelNames.IsChecked == true, values = PrintValues.IsChecked == true;
                string head = "Device" + sep + "Object";
                for (int i = 0; i < 16; i++)
                {
                    head += sep + (i + 1);
                    if (names) head += " - " + sPrioNames[i];
                }
                csv.AppendLine(head);
                foreach (var point in _pointListForDisplay)
                {
                    string item = point.ParentDevice.Name + sep + point.Name;
                    for (int i = 0; i < 16; i++)
                    {
                        item += sep;
                        if (point.aPriosSet[i]) item += values ? point.aValues[i] : "x";
                    }
                    csv.AppendLine(item);
                }
                File.WriteAllText(filename, csv.ToString());
            }
            catch
            {
                await Dialogs.Message("Error saving file, sorry!", "File error", this);
                return;
            }
            await Dialogs.Message("File successfully exported.", "File saved", this);
        }
    }
}
