using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>
    /// Avalonia port of YabePlugins/GlobalCommander (GlobalCommander.cs / .Designer.cs). Same three-list workflow
    /// (devices -> points -> properties) and the same operations; BACnet access runs on worker tasks.
    /// </summary>
    public partial class GlobalCommanderWindow : Window
    {
        const int PATIENCE_INTERVAL = 7500;
        const int MAX_BACNET_DEVICE_INSTANCE = 4194303;
        const int MAXIMUM_UNCONFIRMED_GLOBAL_COMMAND_COUNT = 0;   // safer to always have confirmation
        const int MAXIMUM_MESSAGE_BOX_LINES = 40;

        enum FilterAdvancedOperation { Equals, Does_not_equal, Contains, Has_Property, Does_Not_Have_Property }

        readonly MainWindow _yabe;

        List<GcDevice> _selectedDevices;
        List<GcDevice> _commonDevices;
        List<GcPoint> _selectedPoints;
        List<GcPoint> _allSelectedPoints;
        List<GcPoint> _pointListForDisplay;
        List<GcPoint> _pointListTextFiltered;
        List<GcPoint> _pointListAdvancedFiltered;
        GcProperty _selectedProperty;
        List<GcProperty> _allSelectedProperties;
        List<GcProperty> _commonProperties;

        readonly ObservableCollection<GcItem> deviceItems = new ObservableCollection<GcItem>();
        readonly ObservableCollection<GcItem> pointItems = new ObservableCollection<GcItem>();
        readonly ObservableCollection<GcItem> propertyItems = new ObservableCollection<GcItem>();
        readonly ListBox DeviceList, PointList, PropertyList;

        // controls (names as in the original designer)
        readonly Button cmdPopulateDevices = Ui.Btn("Populate Devices", 104, 36);
        readonly Button cmdPopulatePoints = Ui.Btn("Populate Points", 140, 36, "Populate the objects from the selected devices.\r\nThe behaviour of this button is dependant on\r\nthe \"Populate Points Method\" setting to the right.");
        readonly Button cmdPopulateProperties = Ui.Btn("Populate Properties", 140, 36, "Populate the relevant properties from the selected\r\nobjects. The behaviour of this button is dependant\r\non the \"Populate Properties Method\" setting to the\r\nright.\r\n");
        readonly Button cmdCommand = Ui.Btn("Globally Command", 214, 36, "Command/relinquish the selected property\r\non all selected objects/devices to the specified\r\nvalue.");
        readonly Button cmdViewProps = Ui.Btn("View Properties in Scope", 214, 36, "View all the curent value of the selected property\r\non all selected objects/devices that would be globally\r\ncommanded. The values can also be commanded\r\none-by-one from here.");
        readonly Button cmdFetchDescriptionsDevice = Ui.Btn("Fetch Descriptions", 108, 22);
        readonly Button cmdFetchDescriptionsPoint = Ui.Btn("Fetch Descriptions", 108, 22);
        readonly Button cmdAdvancedFilter = Ui.Btn("Advanced Filter...", 108, 22);
        readonly Button btnSyncTime = Ui.Btn("Sync Time", 102, 36);
        readonly Button btnDeviceControl = Ui.Btn("Device Control", 102, 36);
        readonly TextBox txtDeviceFilter = Ui.Text(double.NaN, "Filter");
        readonly TextBox txtPointFilter = Ui.Text(double.NaN, "Filter");
        readonly TextBox txtPointFilter2 = Ui.Text(double.NaN, "Filter 2");
        readonly TextBox txtPropFilter = Ui.Text(double.NaN, "Filter");
        readonly TextBox txtCmdVal = Ui.Text(double.NaN);
        readonly TextBox txtWhoIsLow = Ui.Text(82);
        readonly TextBox txtWhoIsHigh = Ui.Text(82);
        readonly ComboBox cboPriority = new ComboBox();
        readonly CheckBox chkReread = new CheckBox { Content = Ui.Wrapped("Force re-read objects every time") };
        readonly CheckBox chkContinueFailure = new CheckBox { Content = "Continue on errors" };
        readonly RadioButton radComObj = new RadioButton { Content = Ui.Wrapped("Common Objects (Selected Devices)"), GroupName = "pm" };
        readonly RadioButton radJoinObj = new RadioButton { Content = Ui.Wrapped("Join All Objects into List"), GroupName = "pm", IsChecked = true };
        readonly RadioButton oComProp = new RadioButton { Content = Ui.Wrapped("Properties Common to ALL\r\nSelected Points"), GroupName = "pp", IsChecked = true };
        readonly RadioButton oAnyProp = new RadioButton { Content = Ui.Wrapped("Properties that Exist in ANY\r\nSelected Point"), GroupName = "pp" };
        readonly CheckBox chkApostrophe = new CheckBox { Content = "'", IsChecked = true, IsVisible = false };
        readonly CheckBox chkColon = new CheckBox { Content = ":", IsChecked = true, IsVisible = false };
        readonly CheckBox chkDot = new CheckBox { Content = ".", IsChecked = true, IsVisible = false };
        readonly CheckBox chkUnderscore = new CheckBox { Content = "_", IsVisible = false };
        readonly CheckBox chkComma = new CheckBox { Content = ",", IsVisible = false };
        readonly CheckBox chkHyphen = new CheckBox { Content = "-", IsVisible = false };
        readonly TextBlock lblPtDelimLabel = new TextBlock { Text = "Delimeters:", IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock lblGlobal1 = new TextBlock
        {
            Text = "The \"Populate Properties\" method\r\nneeds to be set to \"Common to ALL\"\r\nin order to globally command.",
            IsVisible = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent
        };
        readonly TextBlock PatienceLabel = new TextBlock
        {
            Text = "Please be patient - the global commander has absolutely no prior knowledge of the nework, or the duplicity of bacnet objects in each device, and must poll each device individually.",
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap
        };
        readonly ProgressBar progBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 12 };
        readonly DispatcherTimer PatienceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PATIENCE_INTERVAL) };
        readonly DispatcherTimer ObjectTextFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        int _suppress;            // programmatic list changes do not reset the dependent lists
        bool deviceTreeSuppressReset, pointTreeSuppressReset;
        volatile uint _writePriority = 8;

        public GlobalCommanderWindow(MainWindow yabe)
        {
            _yabe = yabe;
            Title = "Yabe Global Commander";
            Width = 1247; Height = 700; MinWidth = 1000; MinHeight = 694 - 40;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            DeviceList = Ui.List(deviceItems, true);
            PointList = Ui.List(pointItems, true);
            PropertyList = Ui.List(propertyItems, false);
            cboPriority.ItemsSource = new[] { "1 (Manual Life Safety)", "2 (Auto Life Safety)", "3", "4", "5 (Critical Equipment Protection)",
                "6 (Minimum On/Off Time)", "7", "8 (Manual Operator)", "9", "10", "11", "12", "13", "14", "15", "16" };
            cboPriority.SelectedIndex = 7;
            cboPriority.SelectionChanged += (_, __) => _writePriority = (uint)(Math.Max(cboPriority.SelectedIndex, 0) + 1);

            BuildLayout();
            WireEvents();
            InitialState();
        }

        /// <summary>Priority used for non-priority-array writes (original DetermineWritePriority).</summary>
        public uint DetermineWritePriority() => _writePriority;

        // ------------------------------------------------------------------ layout
        void BuildLayout()
        {
            foreach (var b in new[] { cmdCommand, cmdViewProps }) b.HorizontalAlignment = HorizontalAlignment.Stretch;
            cboPriority.HorizontalAlignment = HorizontalAlignment.Stretch; txtCmdVal.HorizontalAlignment = HorizontalAlignment.Stretch;
            // devices column
            var devBottom = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            var devRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            txtDeviceFilter.Width = double.NaN; txtDeviceFilter.Margin = new Thickness(6, 0, 0, 0); txtDeviceFilter.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(txtDeviceFilter, 1);
            devRow.Children.Add(cmdPopulateDevices); devRow.Children.Add(txtDeviceFilter);
            devBottom.Children.Add(devRow);
            cmdFetchDescriptionsDevice.HorizontalAlignment = HorizontalAlignment.Left;
            devBottom.Children.Add(cmdFetchDescriptionsDevice);
            var devCol = ColumnOf(DeviceList, devBottom);

            // points column
            var ptBottom = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            var filters = new StackPanel { Spacing = 2 };
            filters.Children.Add(txtPointFilter); filters.Children.Add(txtPointFilter2);
            var ptRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            filters.Margin = new Thickness(6, 0, 0, 0); filters.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(filters, 1);
            ptRow.Children.Add(cmdPopulatePoints); ptRow.Children.Add(filters);
            ptBottom.Children.Add(ptRow);
            var ptRow2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            chkReread.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(chkReread, "Normally, we will try and use the cached object list/cached map of\nobject names when populating the object list. With this option checked,\nwe will explicitly re-read the object list from the device, and also re-read\nthe object names every time the list is populated. Note that this will also\nupdate the cache to match.");
            cmdFetchDescriptionsPoint.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetColumn(cmdFetchDescriptionsPoint, 1); Grid.SetColumn(cmdAdvancedFilter, 2);
            Grid.SetColumnSpan(chkReread, 3); Grid.SetRow(cmdFetchDescriptionsPoint, 1); Grid.SetRow(cmdAdvancedFilter, 1); cmdFetchDescriptionsPoint.Margin = new Thickness(0, 4, 4, 0); cmdAdvancedFilter.Margin = new Thickness(0, 4, 0, 0);
            ptRow2.Children.Add(chkReread); ptRow2.Children.Add(cmdFetchDescriptionsPoint); ptRow2.Children.Add(cmdAdvancedFilter);
            ptBottom.Children.Add(ptRow2);
            var ptCol = ColumnOf(PointList, ptBottom);

            // properties column
            var prBottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
            txtPropFilter.Margin = new Thickness(6, 0, 0, 0); txtPropFilter.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(txtPropFilter, 1);
            prBottom.Children.Add(cmdPopulateProperties); prBottom.Children.Add(txtPropFilter);
            var prCol = ColumnOf(PropertyList, prBottom);

            var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("230*,4,449*,4,343*") };
            Grid.SetColumn(devCol, 0); Grid.SetColumn(ptCol, 2); Grid.SetColumn(prCol, 4);
            var s1 = new GridSplitter { Width = 4, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(s1, 1);
            var s2 = new GridSplitter { Width = 4, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns }; Grid.SetColumn(s2, 3);
            lists.Children.Add(devCol); lists.Children.Add(s1); lists.Children.Add(ptCol); lists.Children.Add(s2); lists.Children.Add(prCol);

            // right column of group boxes
            const double W = 280;
            var right = new StackPanel { Spacing = 6, Width = W };
            right.Children.Add(Ui.Group("Device Commands", DevCmdRow()));

            var cmdGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            void P(Control c, int col, int row, int span = 1, Thickness? m = null) { Grid.SetColumn(c, col); Grid.SetRow(c, row); Grid.SetColumnSpan(c, span); if (m != null) c.Margin = m.Value; cmdGrid.Children.Add(c); }
            P(new TextBlock { Text = "Command Value:", VerticalAlignment = VerticalAlignment.Center }, 0, 0, 1, new Thickness(0, 0, 6, 4));
            P(txtCmdVal, 1, 0, 1, new Thickness(0, 0, 0, 4));
            P(new TextBlock { Text = "Priority:", VerticalAlignment = VerticalAlignment.Center }, 0, 1, 1, new Thickness(0, 0, 6, 4));
            cboPriority.HorizontalAlignment = HorizontalAlignment.Stretch;
            P(cboPriority, 1, 1, 1, new Thickness(0, 0, 0, 4));
            P(cmdCommand, 0, 2, 2); P(lblGlobal1, 0, 2, 2);
            chkContinueFailure.Margin = new Thickness(0, 6, 0, 0);
            P(chkContinueFailure, 0, 3, 2);
            right.Children.Add(Ui.Group("Global Commanding", cmdGrid));
            right.Children.Add(Ui.Group("Viewing", cmdViewProps));

            var who = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            var l1 = new TextBlock { Text = "\"WhoIs\" Limit Low:", VerticalAlignment = VerticalAlignment.Center };
            var l2 = new TextBlock { Text = "\"WhoIs\" Limit High:", VerticalAlignment = VerticalAlignment.Center };
            txtWhoIsLow.Margin = new Thickness(0, 0, 0, 4);
            Grid.SetRow(l2, 1); Grid.SetColumn(txtWhoIsLow, 1); Grid.SetRow(txtWhoIsHigh, 1); Grid.SetColumn(txtWhoIsHigh, 1);
            l1.Margin = new Thickness(0, 0, 0, 4);
            who.Children.Add(l1); who.Children.Add(txtWhoIsLow); who.Children.Add(l2); who.Children.Add(txtWhoIsHigh);
            right.Children.Add(Ui.Group("\"Populate Devices\" Settings", who));

            var pm = new StackPanel { Spacing = 2 };
            ToolTip.SetTip(radComObj, "In \"Common Objects\" mode, objects are only shown if that object exists in every selected device,\nwith a). the same instance number and b). the part of the object name after the final delimeter\nmatches. E.g. if delimeter is set to apostrophe, \"Rm1'Temp\" matches with \"Rm2'Temp\", but\n\"Rm1'Temp\" and \"Rm1'Hum\" do not match.");
            ToolTip.SetTip(radJoinObj, "In \"Join All Objects\" mode, all the objects of all the selected devices are listed together.");
            pm.Children.Add(radComObj); pm.Children.Add(radJoinObj);
            var delims = new WrapPanel { Orientation = Orientation.Horizontal };
            delims.Children.Add(lblPtDelimLabel);
            foreach (var c in new[] { chkApostrophe, chkDot, chkColon, chkComma, chkUnderscore, chkHyphen })
            {
                c.MinWidth = 0; c.Margin = new Thickness(6, 0, 0, 0);
                ToolTip.SetTip(c, ToolTip.GetTip(radComObj));
                delims.Children.Add(c);
            }
            pm.Children.Add(delims);
            right.Children.Add(Ui.Group("\"Populate Points\" Method", pm));

            var pp = new StackPanel { Spacing = 6 };
            ToolTip.SetTip(oComProp, "In \"Properties Common to ALL\" mode, properties are only listed if ALL of the selected points have the property\nin their property list. Additionally, the Bacnet Application Tag must match between all properties unless\nit is the priority array (which is excluded from this condition), and any other array properties must have\nthe same length.");
            ToolTip.SetTip(oAnyProp, "In \"Properties that Exist in ANY\" mode, any property that is present in ANY of the selected points will make\nit into the property list, ignoring Bacnet Application Tag and array size. Note that \"Globally Command\" must\nbe disabled in this case, to avoid the risk of accidentally commanding two different data typed points to the\nsame value. E.g. we don't want to Globally Command an analog value and a binary value both to 22.5, because\nthe binary value commands would fail in this case.");
            pp.Children.Add(oComProp); pp.Children.Add(oAnyProp);
            right.Children.Add(Ui.Group("\"Populate Properties\" Method", pp));

            var main = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 12, 12, 4) };
            right.Margin = new Thickness(6, 0, 0, 0);
            var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            Grid.SetColumn(rightScroll, 1);
            main.Children.Add(lists); main.Children.Add(rightScroll);

            var root = new DockPanel();
            var bottom = new StackPanel { Margin = new Thickness(12, 0, 12, 8), Spacing = 3 };
            bottom.Children.Add(PatienceLabel);
            bottom.Children.Add(progBar);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(main);
            Content = root;
        }

        Control DevCmdRow()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            btnSyncTime.HorizontalAlignment = HorizontalAlignment.Stretch; btnDeviceControl.HorizontalAlignment = HorizontalAlignment.Stretch;
            btnDeviceControl.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(btnDeviceControl, 1);
            g.Children.Add(btnSyncTime); g.Children.Add(btnDeviceControl);
            return g;
        }

        static Control ColumnOf(Control list, Control bottom)
        {
            var d = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            d.Children.Add(bottom);
            d.Children.Add(list);
            return d;
        }

        // ------------------------------------------------------------------ state helpers
        void InitialState()   // GlobalCommander_Shown
        {
            cmdPopulateDevices.Focus();
            cmdCommand.IsEnabled = false;
            chkContinueFailure.IsEnabled = false;
            cmdViewProps.IsEnabled = false;
            cboPriority.IsEnabled = false;
            txtCmdVal.IsEnabled = false;
            txtPropFilter.IsEnabled = false; txtPropFilter.Text = "";
            ObjectTextFilterTimer.Stop();
            txtPointFilter.IsEnabled = false; txtPointFilter.Text = "";
            txtPointFilter2.IsEnabled = false; txtPointFilter2.Text = "";
            txtDeviceFilter.IsEnabled = false; txtDeviceFilter.Text = "";
            cmdAdvancedFilter.IsEnabled = false;
            cmdFetchDescriptionsPoint.IsEnabled = false;
            cmdFetchDescriptionsDevice.IsEnabled = false;
        }

        void WireEvents()
        {
            cmdPopulateDevices.Click += (_, __) => PopulateDevices();
            cmdPopulatePoints.Click += (_, __) => PopulatePoints();
            cmdPopulateProperties.Click += (_, __) => PopulateProperties();
            cmdCommand.Click += (_, __) => GloballyCommand();
            cmdViewProps.Click += (_, __) => ViewProps();
            btnSyncTime.Click += (_, __) => SyncTime();
            btnDeviceControl.Click += (_, __) => DeviceControl();
            cmdFetchDescriptionsDevice.Click += (_, __) => FetchDescriptionsDevice();
            cmdFetchDescriptionsPoint.Click += (_, __) => FetchDescriptionsPoint();
            cmdAdvancedFilter.Click += (_, __) => AdvancedFilterClick();
            txtDeviceFilter.TextChanged += (_, __) => DeviceFilterChanged();
            txtPointFilter.TextChanged += (_, __) => PointFilterChanged(txtPointFilter);
            txtPointFilter2.TextChanged += (_, __) => PointFilterChanged(txtPointFilter2);
            txtPropFilter.TextChanged += (_, __) => PropFilterChanged();
            ObjectTextFilterTimer.Tick += (_, __) => ObjectTextFilterTick();
            PatienceTimer.Tick += (_, __) => { PatienceTimer.Stop(); PatienceLabel.IsVisible = true; };
            radComObj.IsCheckedChanged += (_, __) => { if (radComObj.IsChecked == true) { CheckDelimVisibility(); ResetFromObjectsList(); } };
            radJoinObj.IsCheckedChanged += (_, __) => { if (radJoinObj.IsChecked == true) { CheckDelimVisibility(); ResetFromObjectsList(); } };
            foreach (var c in new[] { chkApostrophe, chkColon, chkComma, chkDot, chkUnderscore, chkHyphen })
                c.IsCheckedChanged += (_, __) => ResetFromObjectsList();
            oComProp.IsCheckedChanged += (_, __) => { if (oComProp.IsChecked == true) { ResetFromPropertyList(); lblGlobal1.IsVisible = false; } };
            oAnyProp.IsCheckedChanged += (_, __) => { if (oAnyProp.IsChecked == true) { ResetFromPropertyList(); lblGlobal1.IsVisible = true; } };
            DeviceList.SelectionChanged += (_, __) => { if (_suppress == 0 && !deviceTreeSuppressReset) ResetFromObjectsList(); };
            PointList.SelectionChanged += (_, __) => { if (_suppress == 0 && !pointTreeSuppressReset) ResetFromPropertyList(); };
            PropertyList.DoubleTapped += (_, __) => { if (PropertyList.SelectedItem != null) ViewProps(); };
            PropertyList.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter && cmdViewProps.IsEnabled) { e.Handled = true; ViewProps(); }
            };
            Closed += (_, __) => { PatienceTimer.Stop(); ObjectTextFilterTimer.Stop(); };
        }

        void StartPatienceTimer() { PatienceTimer.Stop(); PatienceTimer.Interval = TimeSpan.FromMilliseconds(PATIENCE_INTERVAL); PatienceTimer.Start(); }
        void ResetPatience() { PatienceTimer.Stop(); PatienceLabel.IsVisible = false; }
        void SetProgress(int v) { if (Dispatcher.UIThread.CheckAccess()) progBar.Value = v; else Dispatcher.UIThread.Post(() => progBar.Value = v); }
        Action<int> Progress => SetProgress;
        Task Msg(string text, string title = "Error") => Dialogs.Message(text, title, this);

        void ClearItems(ObservableCollection<GcItem> items, ListBox lb)
        {
            _suppress++;
            try { lb.SelectedItems?.Clear(); items.Clear(); }
            finally { _suppress--; }
        }

        void ResetFromObjectsList()
        {
            _selectedProperty = null; _allSelectedProperties = null;
            ClearItems(propertyItems, PropertyList);
            _selectedPoints = null; _allSelectedPoints = null;
            ClearItems(pointItems, PointList);
            _pointListTextFiltered = null; _pointListAdvancedFiltered = null;
            cmdAdvancedFilter.ClearValue(Button.BackgroundProperty);

            cmdCommand.IsEnabled = false; chkContinueFailure.IsEnabled = false; cmdViewProps.IsEnabled = false;
            cboPriority.IsEnabled = false; txtCmdVal.IsEnabled = false;
            txtPropFilter.IsEnabled = false; txtPropFilter.Text = "";
            ObjectTextFilterTimer.Stop();
            txtPointFilter.IsEnabled = false; txtPointFilter.Text = "";
            txtPointFilter2.IsEnabled = false; txtPointFilter2.Text = "";
            cmdAdvancedFilter.IsEnabled = false;
            cmdFetchDescriptionsPoint.IsEnabled = false;
        }

        void ResetFromPropertyList()
        {
            _selectedProperty = null; _allSelectedProperties = null;
            ClearItems(propertyItems, PropertyList);
            cmdCommand.IsEnabled = false; chkContinueFailure.IsEnabled = false; cmdViewProps.IsEnabled = false;
            cboPriority.IsEnabled = false; txtCmdVal.IsEnabled = false;
            txtPropFilter.IsEnabled = false; txtPropFilter.Text = "";
        }

        void ResetAfterPropertyList()
        {
            _selectedProperty = null;
            ClearItems(propertyItems, PropertyList);
        }

        void CheckDelimVisibility()
        {
            bool v = radComObj.IsChecked == true;
            foreach (var c in new[] { chkApostrophe, chkColon, chkComma, chkDot, chkHyphen, chkUnderscore }) c.IsVisible = v;
            lblPtDelimLabel.IsVisible = v;
        }

        char[] GetDelims()
        {
            var d = new List<char>();
            if (chkApostrophe.IsChecked == true) d.Add('\'');
            if (chkColon.IsChecked == true) d.Add(':');
            if (chkComma.IsChecked == true) d.Add(',');
            if (chkDot.IsChecked == true) d.Add('.');
            if (chkHyphen.IsChecked == true) d.Add('-');
            if (chkUnderscore.IsChecked == true) d.Add('_');
            return d.ToArray();
        }

        // Disable/Enable commanding: remember the previous enabled states of the controls and restore them afterwards.
        readonly Stack<bool[]> previousCmdStates = new Stack<bool[]>();
        Control[] CmdControls => new Control[]
        {
            cmdCommand, cboPriority, btnSyncTime, btnDeviceControl, txtCmdVal, cmdViewProps, txtWhoIsLow, txtWhoIsHigh, chkContinueFailure,
            oAnyProp, oComProp, radComObj, radJoinObj, chkApostrophe, chkDot, chkColon, chkComma, chkHyphen, chkUnderscore, chkReread,
            cmdFetchDescriptionsDevice, cmdFetchDescriptionsPoint, cmdAdvancedFilter, txtDeviceFilter, txtPointFilter, txtPointFilter2,
            txtPropFilter, cmdPopulateDevices, cmdPopulatePoints, cmdPopulateProperties, DeviceList, PointList, PropertyList
        };
        void DisableCommanding()
        {
            var c = CmdControls;
            previousCmdStates.Push(c.Select(x => x.IsEnabled).ToArray());
            foreach (var x in c) x.IsEnabled = false;
        }
        void EnableCommanding()
        {
            if (previousCmdStates.Count == 0) return;
            var s = previousCmdStates.Pop(); var c = CmdControls;
            for (int i = 0; i < c.Length; i++) c[i].IsEnabled = s[i];
        }

        // ------------------------------------------------------------------ selection
        List<GcDevice> SelectedDeviceExports() => Ui.Selected(DeviceList, deviceItems).Select(i => (GcDevice)i.Tag).ToList();

        /// <summary>Original SelectAllPointsAndProperties. Returns null on success, otherwise the message (box shown by caller).</summary>
        async Task<bool> SelectAllPointsAndProperties(bool selectObjInsFromAllDevices, bool suppressValueZeroCount = false)
        {
            _selectedPoints = new List<GcPoint>();
            var sel = Ui.Selected(PointList, pointItems);
            if (sel.Count > 0)
            {
                progBar.Value = 0;
                foreach (var item in sel) _selectedPoints.Add((GcPoint)item.Tag);
            }
            else
            {
                await Msg("No point(s) selected.");
                return false;
            }

            bool propertiesSelected = PropertyList.SelectedItem != null;
            _allSelectedProperties = new List<GcProperty>();
            _allSelectedPoints = new List<GcPoint>();

            if (propertiesSelected) _selectedProperty = (GcProperty)((GcItem)PropertyList.SelectedItem).Tag;

            if (propertiesSelected && _selectedProperty == null) { await Msg("Property selected is not valid."); return false; }
            if (propertiesSelected && _selectedProperty.Values.Count <= 0 && !suppressValueZeroCount)
            {
                await Msg("Property selected does not have a value attached to it. Unable to read/command property value.");
                return false;
            }

            if (selectObjInsFromAllDevices)
            {
                foreach (var device in _selectedDevices)
                    foreach (var point in _selectedPoints)
                    {
                        var actualPoint = device.Points.Find(x => x.ObjectID.Equals(point.ObjectID));
                        if (actualPoint != null)
                        {
                            _allSelectedPoints.Add(actualPoint);
                            if (propertiesSelected)
                            {
                                var match = actualPoint.Properties.Find(x => x.PropertyID == _selectedProperty.PropertyID);
                                if (match != null) _allSelectedProperties.Add(match);
                            }
                        }
                    }
            }
            else
            {
                foreach (var point in _selectedPoints)
                {
                    _allSelectedPoints.Add(point);
                    if (propertiesSelected)
                    {
                        var match = point.Properties.Find(x => x.PropertyID == _selectedProperty.PropertyID);
                        if (match != null) _allSelectedProperties.Add(match);
                    }
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ item builders
        static string Describe(string name, string desc) => string.IsNullOrEmpty(desc) ? name : name + " - " + desc;

        GcItem DeviceItem(GcDevice d) => new GcItem { Name = d.Name, Text = Describe(d.Name, d.Description), Tag = d };
        GcItem PointItem(GcPoint p, string name = null) { var n = name ?? p.Name; return new GcItem { Name = n, Text = Describe(n, p.Description), Tag = p }; }

        void FillPoints(IEnumerable<GcPoint> pts, Func<GcPoint, string> nameOf = null)
        {
            _suppress++;
            try
            {
                PointList.SelectedItems?.Clear();
                pointItems.Clear();
                if (pts != null) foreach (var p in pts) pointItems.Add(PointItem(p, nameOf?.Invoke(p)));
            }
            finally { _suppress--; }
        }

        static string FormatTag(string rawTag) =>
            System.Threading.Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(rawTag.Replace("BACNET_APPLICATION_TAG_", "").Replace('_', ' ').ToLower());

        GcItem PropertyItem(GcProperty property, bool onlyCommon)
        {
            string name;
            if (onlyCommon)
                name = property.Values.Count <= 0 ? string.Format("{0} [{1}]", property.Name, "NO VALUE PRESENT")
                                                  : string.Format("{0} [{1}]", property.Name, FormatTag(property.Values[0].Tag.ToString()));
            else name = string.Format("{0}", property.Name);
            return new GcItem { Name = name, Text = name, Tag = property };
        }

        // ------------------------------------------------------------------ Populate Devices
        List<BACnetDevice> EnumerateYabeDevices()   // original YabeDiscoveredDevices (all channels)
        {
            var list = new List<BACnetDevice>();
            var roots = _yabe.DeviceTree.ItemsSource as IEnumerable<DeviceNode>;
            void Collect(IEnumerable<DeviceNode> l)
            {
                if (l == null) return;
                foreach (var n in l)
                {
                    if (n.Device != null && !list.Contains(n.Device)) list.Add(n.Device);
                    Collect(n.Children);
                }
            }
            Collect(roots);
            return list;
        }

        async void PopulateDevices()
        {
            StartPatienceTimer();
            progBar.Value = 0;
            ResetFromObjectsList();

            if (!int.TryParse((txtWhoIsLow.Text ?? "").Trim(), out int whoIsLow)) whoIsLow = 0;
            if (!int.TryParse((txtWhoIsHigh.Text ?? "").Trim(), out int whoIsHigh)) whoIsHigh = MAX_BACNET_DEVICE_INSTANCE;
            bool reread = chkReread.IsChecked == true;
            var devices = EnumerateYabeDevices();

            cmdPopulateDevices.IsEnabled = false;
            try
            {
                _commonDevices = await Task.Run(() => PopulateDevicesWithNames(devices, whoIsLow, whoIsHigh, reread, Progress));
            }
            finally { cmdPopulateDevices.IsEnabled = true; }
            _commonDevices.Sort();
            _selectedDevices = null;

            _suppress++;
            try
            {
                DeviceList.SelectedItems?.Clear();
                deviceItems.Clear();
                foreach (var d in _commonDevices) deviceItems.Add(DeviceItem(d));
            }
            finally { _suppress--; }

            txtDeviceFilter.IsEnabled = true;
            cmdFetchDescriptionsDevice.IsEnabled = true;
            progBar.Value = 100;
            ResetPatience();
        }

        List<GcDevice> PopulateDevicesWithNames(List<BACnetDevice> devices, int whoIsLow, int whoIsHigh, bool reread, Action<int> prog)
        {
            int progTotal = devices.Count + 1, p = 0;
            var deviceList = new List<GcDevice>();
            foreach (var dev in devices)
            {
                uint deviceID = dev.deviceId;
                if (deviceID < whoIsLow || deviceID > whoIsHigh) continue;
                var device = new GcDevice(dev, this);
                var deviceObjectID = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, deviceID);
                string identifier = dev.GetObjectName(deviceObjectID);
                if (!string.IsNullOrEmpty(identifier)) identifier = identifier + " [" + deviceObjectID.Instance.ToString() + "]";
                else
                {
                    try
                    {
                        string nameRead = dev.ReadObjectName(deviceObjectID, reread);
                        if (nameRead != null) identifier = nameRead + " [" + deviceObjectID.Instance.ToString() + "]";
                    }
                    catch { }
                }
                if (identifier != null) device.Name = identifier;
                if (deviceList.Find(item => item.DeviceID == deviceID) == null) deviceList.Add(device);
                p++; prog((int)(100 * p / progTotal));
            }
            return deviceList;
        }

        // ------------------------------------------------------------------ Populate Points
        static string GetNameWithOnlyLastPartShown(string input, char[] delims)
        {
            if (input == null) return input;
            string output = string.Empty;
            bool containedDelim = false;
            for (int delimIndex = 0; delimIndex < delims.Length; delimIndex++)
            {
                if (input.Contains(delims[delimIndex]))
                {
                    containedDelim = true;
                    string[] parts = input.Split(delims[delimIndex]);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (i == (parts.Length - 1)) { output = output + parts[i]; break; }
                        else output = output + "*" + delims[delimIndex];
                    }
                    break;
                }
            }
            return containedDelim ? output : input;
        }

        static bool CheckLastPartOfNameMatch(string a, string b, char[] delims)
        {
            if (a == null || b == null) return a == b;
            if (a.Equals(b)) return true;
            for (int delimIndex = 0; delimIndex < delims.Length; delimIndex++)
            {
                if (a.Contains(delims[delimIndex]) && b.Contains(delims[delimIndex]))
                {
                    int hyphenA = a.LastIndexOf(delims[delimIndex]);
                    int hyphenB = b.LastIndexOf(delims[delimIndex]);
                    return a.Substring(hyphenA).Equals(b.Substring(hyphenB));
                }
            }
            return false;
        }

        async void PopulatePoints()
        {
            ResetFromObjectsList();
            _selectedDevices = SelectedDeviceExports();
            if (_selectedDevices.Count == 0) { await Msg("No device(s) selected."); return; }

            StartPatienceTimer();
            progBar.Value = 0;
            _pointListForDisplay = new List<GcPoint>();
            bool reread = chkReread.IsChecked == true;
            bool common = radComObj.IsChecked == true, join = radJoinObj.IsChecked == true;
            char[] delims = GetDelims();
            var devs = _selectedDevices;

            cmdPopulatePoints.IsEnabled = false;
            string error;
            try { error = await Task.Run(() => PopulatePointsForDevices(devs, reread, Progress)); }
            finally { cmdPopulatePoints.IsEnabled = true; }

            if (error != null)
            {
                ResetPatience(); progBar.Value = 0;
                await Msg(error, "Communication Error");
                return;
            }

            if (common)
            {
                // Only the points that are common to all controllers (name compared after the LAST delimeter).
                foreach (var point in devs[0].Points) _pointListForDisplay.Add(point);
                for (int i = 0; i < _pointListForDisplay.Count; i++)
                    foreach (var device in devs)
                    {
                        var match = device.Points.Find(x => x.ObjectID.Equals(_pointListForDisplay[i].ObjectID) && CheckLastPartOfNameMatch(x.ObjectName, _pointListForDisplay[i].ObjectName, delims));
                        if (match == null) { _pointListForDisplay.RemoveAt(i--); break; }
                    }
            }
            else if (join)
            {
                foreach (var device in devs) foreach (var point in device.Points) _pointListForDisplay.Add(point);
            }

            _pointListTextFiltered = null; _pointListAdvancedFiltered = null;
            cmdAdvancedFilter.ClearValue(Button.BackgroundProperty);

            FillPoints(_pointListForDisplay, p => (devs.Count > 1 && common)
                ? GetNameWithOnlyLastPartShown(p.ObjectName, delims) + " [" + BACnetDevice.ShortenObjectId(p.ObjectID.ToString()) + "]"
                : p.Name);

            progBar.Value = 100;
            ResetPatience();
            cmdCommand.IsEnabled = false; chkContinueFailure.IsEnabled = false; cboPriority.IsEnabled = false;
            txtCmdVal.IsEnabled = false; cmdViewProps.IsEnabled = false;
            txtPointFilter.IsEnabled = true; txtPointFilter2.IsEnabled = true;
            cmdAdvancedFilter.IsEnabled = true; cmdFetchDescriptionsPoint.IsEnabled = true;
        }

        string PopulatePointsForDevices(List<GcDevice> devices, bool forceReread, Action<int> prog)
        {
            int progTotal = devices.Count + 2, p = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                string err = PopulatePointsForDevice(devices[i], forceReread);
                if (err != null) return err;
                p++; prog((int)(100 * p / progTotal));
            }
            return null;
        }

        string PopulatePointsForDevice(GcDevice device, bool forceReread)
        {
            var comm = device.Comm; var adr = device.DeviceAddress;
            string fail = string.Format("Couldn't fetch objects for device {0}.", device.Name);
            List<BacnetObjectId> objectList; uint objectCount;
            if (!device.Device.ReadObjectList(out objectList, out objectCount, forceReread)) return fail;

            if (objectList == null && objectCount != 0)
            {
                // slow way: item by item
                objectList = new List<BacnetObjectId>();
                try
                {
                    for (uint i = 1; i <= objectCount; i++)
                    {
                        if (!device.Device.ReadObjectListItem(out BacnetObjectId objId, i, forceReread)) return fail;
                        objectList.Add(objId);
                    }
                }
                catch (Exception) { return fail; }
            }
            if (objectList == null || objectList.Count != objectCount) return fail;

            objectList.Sort();
            device.Points.Clear();

            // faster name read (manual splitting to avoid segmentation); on failure the names are read one by one below
            bool _ = device.Device.ReadAllObjectsName(forceReread);

            foreach (BacnetObjectId bobj_id in objectList)
            {
                var point = new GcPoint(device, bobj_id);
                string identifier = null, objectName = null;
                identifier = device.Device.GetObjectName(bobj_id);
                if (identifier != null)
                {
                    objectName = identifier;   // (original left ObjectName null here, which broke "Common Objects" matching)
                    identifier = identifier + " [" + BACnetDevice.ShortenObjectId(bobj_id.ToString()) + "]";
                }
                else
                {
                    try
                    {
                        if (comm.ReadPropertyRequest(adr, bobj_id, BacnetPropertyIds.PROP_OBJECT_NAME, out IList<BacnetValue> values))
                        {
                            device.Device.UpdateObjectNameMapping(bobj_id, values[0].ToString());
                            objectName = values[0].ToString();
                            identifier = objectName + " [" + BACnetDevice.ShortenObjectId(bobj_id.ToString()) + "]";
                        }
                    }
                    catch { }
                }
                if (identifier != null) { point.Name = identifier; point.ObjectName = objectName; }
                if (device.Points.Find(item => item.ObjectID.Equals(bobj_id)) == null) device.Points.Add(point);
            }
            return null;
        }

        // ------------------------------------------------------------------ Populate Properties
        bool PopulatePropertyRefsForPoints(List<GcPoint> points, Action<int> prog)
        {
            int progTotal = points.Count + 2, p = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (!PopulatePropertiesForPoint(points[i])) return false;
                p++; prog((int)(100 * p / progTotal));
            }
            return true;
        }

        bool PopulatePropertiesForPoint(GcPoint point)
        {
            BACnetDevice device = point.ParentDevice.Device;
            var properties = new[] { new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_ALL, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL) };
            IList<BacnetReadAccessResult> multi_value_list;
            try
            {
                if (!device.channel.ReadPropertyMultipleRequest(device.BacAdr, point.ObjectID, properties, out multi_value_list)) return false;
            }
            catch (Exception) { return false; }

            point.Properties.Clear();
            if (multi_value_list.Count > 0)
            {
                foreach (BacnetPropertyValue p_value in multi_value_list[0].values)
                {
                    BacnetPropertyIds propertyID = (BacnetPropertyIds)p_value.property.propertyIdentifier;
                    var property = new GcProperty(point, propertyID);
                    property.Name = device.GetNiceName(propertyID, true);
                    if (p_value.value != null)
                    {
                        var b_values = p_value.value.ToList();
                        property.Values.AddRange(b_values);
                        for (int i = 0; i < b_values.Count; i++)
                            property.ValueTypes.Add(b_values[i].Value != null ? b_values[i].Value.GetType() : null);
                    }
                    if (point.Properties.Find(item => item.PropertyID.Equals(propertyID)) == null) point.Properties.Add(property);
                }
            }
            return true;
        }

        async void PopulateProperties()
        {
            ResetFromPropertyList();
            if (!await SelectAllPointsAndProperties(radComObj.IsChecked == true))
            {
                progBar.Value = 0;
                await Msg("Failed to retrieve a comprehensive list of selected points/properties.");
                return;
            }
            StartPatienceTimer();
            var allPoints = _allSelectedPoints;
            bool onlyCommonProperties = oComProp.IsChecked == true;

            cmdPopulateProperties.IsEnabled = false;
            bool result;
            try { result = await Task.Run(() => PopulatePropertyRefsForPoints(allPoints, Progress)); }
            finally { cmdPopulateProperties.IsEnabled = true; }

            _commonProperties = new List<GcProperty>();
            if (_selectedPoints != null && _selectedPoints.Count > 0)
            {
                if (result)
                {
                    foreach (var property in _selectedPoints[0].Properties) _commonProperties.Add(property);
                    if (_selectedPoints.Count > 1)
                    {
                        if (onlyCommonProperties)
                        {
                            // Every "common" property must exist in every selected point, with the same array size and the same
                            // application tag (the priority array is exempt from the tag condition).
                            _commonProperties.RemoveAll(x => !_selectedPoints.All(y => y.Properties.Exists(z =>
                                z.PropertyID == x.PropertyID && x.Values.Count == z.Values.Count &&
                                (x.Values.Count == 0 ? true : (x.Values[0].Tag == z.Values[0].Tag || z.PropertyID == BacnetPropertyIds.PROP_PRIORITY_ARRAY)))));
                        }
                        else
                        {
                            for (int i = 1; i < _selectedPoints.Count; i++)
                                foreach (var property in _selectedPoints[i].Properties)
                                    if (!_commonProperties.Exists(x => x.PropertyID == property.PropertyID)) _commonProperties.Add(property);
                        }
                    }
                }
                else
                {
                    progBar.Value = 0; ResetPatience();
                    await Msg("Failed to retrieve a comprehensive list of selected points/properties.");
                    return;
                }
            }
            else
            {
                progBar.Value = 0; ResetPatience();
                await Msg("No points selected.");
                return;
            }

            _suppress++;
            try
            {
                PropertyList.SelectedItems?.Clear(); propertyItems.Clear();
                foreach (var property in _commonProperties) propertyItems.Add(PropertyItem(property, onlyCommonProperties));
            }
            finally { _suppress--; }

            progBar.Value = 100;
            ResetPatience();
            cmdCommand.IsEnabled = onlyCommonProperties;
            chkContinueFailure.IsEnabled = onlyCommonProperties;
            cboPriority.IsEnabled = onlyCommonProperties;
            txtCmdVal.IsEnabled = onlyCommonProperties;
            cmdViewProps.IsEnabled = true;
            txtPropFilter.IsEnabled = true;
        }

        // ------------------------------------------------------------------ Globally command
        async void GloballyCommand()
        {
            if (_selectedPoints == null || _selectedPoints.Count <= 0)
                await Msg("No points found to command.");

            if (PropertyList.SelectedItem == null) { await Msg("No property selected."); return; }
            _selectedProperty = (GcProperty)((GcItem)PropertyList.SelectedItem).Tag;
            if (_selectedProperty == null) { await Msg("Property selected is not valid."); return; }
            if (_selectedProperty.Values.Count <= 0)
            {
                await Msg("Property selected does not have a value attached to it. The Global Commander needs a value to already be present in order to command it to something else.");
                return;
            }
            if (_selectedProperty.ExportAsJSON)
            {
                await Msg("Unfortunately, globally commanding the selected property is not supported, but can be viewed read-only from the \"View Properties in Scope\" window.");
                return;
            }

            progBar.Value = 0;
            string enteredValue = (txtCmdVal.Text ?? "").Trim();
            progBar.Value = 100 * 1 / 3;

            if (!await SelectAllPointsAndProperties(radComObj.IsChecked == true))
            {
                progBar.Value = 0;
                await Msg("Failed to retrieve a comprehensive list of selected properties to be commanded.");
                return;
            }
            if (_allSelectedProperties == null || _allSelectedProperties.Count <= 0)
            {
                await Msg("Points to command could not be resolved.");
                progBar.Value = 0;
                return;
            }

            var props = _allSelectedProperties;
            var selProp = _selectedProperty;
            if (props.Count > MAXIMUM_UNCONFIRMED_GLOBAL_COMMAND_COUNT)
            {
                var question = new StringBuilder();
                question.AppendLine("Are you sure you want to globally command all objects in the scope of the selection?");
                question.AppendLine();
                question.AppendLine("The Following objects will be commanded:");
                int count = 0;
                foreach (var propertyToCommand in props)
                {
                    if (count < MAXIMUM_MESSAGE_BOX_LINES)
                    {
                        question.AppendLine(string.Format("Device \"{0}\"; object \"{1}\"; property \"{2}\".", propertyToCommand.ParentPoint.ParentDevice.Name, propertyToCommand.ParentPoint.Name, selProp.Name));
                        count++;
                    }
                    else
                    {
                        question.AppendLine();
                        question.AppendLine(string.Format("and {0} more...", props.Count - count));
                        break;
                    }
                }
                if (!await Dialogs.Confirm(question.ToString(), "Confirm Global Command", this)) return;
            }

            progBar.Value = 100 * 2 / 3;
            bool continueOnErrors = chkContinueFailure.IsChecked == true;
            DisableCommanding();
            int errorCounter = 0;
            var errorString = new StringBuilder();
            string stopMessage = null;
            await Task.Run(() =>
            {
                int prog = 2, progTotal = props.Count + 3;
                foreach (var propertyToCommand in props)
                {
                    try { propertyToCommand.ValueObjectString = enteredValue; }
                    catch (Exception ex)
                    {
                        if (continueOnErrors)
                        {
                            errorCounter++;
                            if (errorCounter < MAXIMUM_MESSAGE_BOX_LINES) errorString.AppendLine(ex.Message);
                        }
                        else { stopMessage = ex.Message; break; }
                    }
                    prog++; SetProgress((int)(100 * prog / progTotal));
                }
            });

            if (stopMessage != null)
            {
                await Msg(stopMessage, "Data Error");
                progBar.Value = 0;
                EnableCommanding();
                return;
            }
            progBar.Value = 100;
            if (errorCounter > 0)
                await Msg("The following errors occurred:" + errorString.ToString() + ((errorCounter > MAXIMUM_MESSAGE_BOX_LINES) ? (Environment.NewLine + "and " + (errorCounter - MAXIMUM_MESSAGE_BOX_LINES) + " more...") : ""), "Communication Errors");
            EnableCommanding();
        }

        // ------------------------------------------------------------------ View properties in scope
        async void ViewProps()
        {
            progBar.Value = 0;
            bool common = radComObj.IsChecked == true;
            if (!await SelectAllPointsAndProperties(common, true))
            {
                progBar.Value = 0;
                await Msg("Failed to retrieve a comprehensive list of selected points/properties.");
                return;
            }
            var pts = _allSelectedPoints;
            DisableCommanding();
            try { await Task.Run(() => PopulatePropertyRefsForPoints(pts, Progress)); }
            finally { EnableCommanding(); }

            if (!await SelectAllPointsAndProperties(common, true))
            {
                progBar.Value = 0;
                await Msg("Failed to retrieve a comprehensive list of selected points/properties.");
                return;
            }
            if (_allSelectedProperties == null || _allSelectedProperties.Count <= 0)
            {
                progBar.Value = 0;
                await Msg("Points to view could not be resolved.");
                return;
            }
            try
            {
                var w = new PropertyValueListWindow(_allSelectedProperties, _selectedProperty?.Name);
                w.Show(this);
            }
            catch (Exception ex)
            {
                progBar.Value = 0;
                await Msg(string.Format("Failed to load property value list. {0} - {1}.", ex.GetType().ToString(), ex.Message));
                return;
            }
            progBar.Value = 100;
        }

        // ------------------------------------------------------------------ Device commands
        async void SyncTime()
        {
            _selectedDevices = SelectedDeviceExports();
            if (_selectedDevices.Count == 0) { await Msg("No device(s) selected."); return; }
            StartPatienceTimer();
            progBar.Value = 0;
            bool utc = AppSettings.Default.TimeSynchronize_UTC;
            for (int i = 0; i < _selectedDevices.Count; i++)
            {
                var d = _selectedDevices[i];
                try
                {
                    await Task.Run(() =>
                    {
                        if (utc) d.Comm.SynchronizeTime(d.DeviceAddress, DateTime.Now.ToUniversalTime(), true);
                        else d.Comm.SynchronizeTime(d.DeviceAddress, DateTime.Now, false);
                    });
                }
                catch (Exception ex) { await Msg(string.Format("Failed to sync time for device {0}: {1}", d.Name, ex.Message)); }
                progBar.Value = (int)(100 * i / _selectedDevices.Count);
            }
            progBar.Value = 100;
            ResetPatience();
        }

        async void DeviceControl()
        {
            _selectedDevices = SelectedDeviceExports();
            if (_selectedDevices.Count == 0) { await Msg("No device(s) selected."); return; }

            var dlg = new DeviceCommunicationControlDialog();
            await dlg.ShowDialog(this);
            if (!dlg.Accepted) return;

            BacnetReinitializedStates action = dlg.ReinitializeState;
            bool reinitialize = dlg.IsReinitialize;
            string actionString = string.Empty;
            switch (action)
            {
                case BacnetReinitializedStates.BACNET_REINIT_COLDSTART: actionString = "Cold Start"; break;
                case BacnetReinitializedStates.BACNET_REINIT_WARMSTART: actionString = "Warm Start"; break;
                case BacnetReinitializedStates.BACNET_REINIT_STARTBACKUP: actionString = "Start Backup"; break;
                case BacnetReinitializedStates.BACNET_REINIT_ENDBACKUP: actionString = "End Backup"; break;
                case BacnetReinitializedStates.BACNET_REINIT_STARTRESTORE: actionString = "Start Restore"; break;
                case BacnetReinitializedStates.BACNET_REINIT_ENDRESTORE: actionString = "End Restore"; break;
                case BacnetReinitializedStates.BACNET_REINIT_ABORTRESTORE: actionString = "Abort Restore"; break;
                case BacnetReinitializedStates.BACNET_REINIT_ACTIVATE_CHANGES: actionString = "Activate Changes"; break;
                case BacnetReinitializedStates.BACNET_REINIT_IDLE: actionString = "Idle"; break;
            }
            string password = dlg.Password;
            uint duration = dlg.Duration;
            uint disableCommunication = dlg.DisableCommunication ? 1u : 0u;

            if (!await Dialogs.Confirm("Are you sure you want to send the a \"" + actionString + "\" device control command to " + _selectedDevices.Count.ToString() + " selected device(s)?", "Confirm command", this))
                return;

            StartPatienceTimer();
            progBar.Value = 0;
            var devs = _selectedDevices;
            string kind = reinitialize ? "reinitialize" : "device communication control";
            for (int i = 0; i < devs.Count; i++)
            {
                var d = devs[i];
                bool last = i >= devs.Count - 1;
                try
                {
                    bool ok = await Task.Run(() => reinitialize
                        ? d.Comm.ReinitializeRequest(d.DeviceAddress, action, password)
                        : d.Comm.DeviceCommunicationControlRequest(d.DeviceAddress, duration, disableCommunication, password));
                    if (!ok)
                    {
                        string text = string.Format("Device " + (reinitialize ? "reinitialize" : "communication control") + " command \"" + actionString + "\" was not accepted by device {0}", d.Name);
                        if (!last)
                        {
                            if (!await Dialogs.Confirm(text + Environment.NewLine + Environment.NewLine + "Continue sending command to remaining devices?", "Error", this))
                            { progBar.Value = 0; ResetPatience(); return; }
                        }
                        else await Msg(text);
                    }
                }
                catch (Exception ex)
                {
                    string text = string.Format("Failed to send \"" + actionString + "\" " + kind + " command to device {0}: {1}", d.Name, ex.Message);
                    if (!last)
                    {
                        if (!await Dialogs.Confirm(text + Environment.NewLine + Environment.NewLine + "Continue sending command to remaining devices?", "Error", this))
                        { progBar.Value = 0; ResetPatience(); return; }
                    }
                    else await Msg(text);
                }
                progBar.Value = (int)(100 * i / devs.Count);
            }
            progBar.Value = 100;
            ResetPatience();
        }
    }
}
