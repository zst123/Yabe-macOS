using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO.BACnet;
using System.Linq;
using System.Reflection;

namespace YabeMac
{
    /// <summary>
    /// Settings dialog (port of Forms/SettingsDialog: a categorized PropertyGrid over the user settings).
    /// Every setting of the original descriptor list is shown with the original display name,
    /// description and category. Edits are applied immediately and persisted when the dialog closes.
    /// </summary>
    public partial class SettingsDialog : Window
    {
        const string CAT_GENERAL = "General";
        const string CAT_GUI = "Graphical User Interface";
        const string CAT_EDEEXPORT = "EDE Export";
        const string CAT_COV = "Change of Value";
        const string CAT_IP = "BACnet/IP";
        const string CAT_MSTP = "MS/TP";
        const string CAT_SC = "BACnet/SC";

        /// <summary>One entry of the grid. Options (display, stored) turn the editor into a drop-down.</summary>
        class Item
        {
            public string Prop, Display, Desc, Category;
            public List<KeyValuePair<string, string>> Options;
            public Func<string> Custom;     // unused
        }

        static List<KeyValuePair<string, string>> Names(params string[] n) => n.Select(x => new KeyValuePair<string, string>(x, x)).ToList();
        static List<KeyValuePair<string, string>> EnumOpts<T>() where T : Enum =>
            Enum.GetNames(typeof(T)).Select(x => new KeyValuePair<string, string>(Desc(typeof(T), x), x)).ToList();
        static string Desc(Type t, string name)
        {
            var a = (DescriptionAttribute[])t.GetField(name).GetCustomAttributes(typeof(DescriptionAttribute), false);
            return a.Length > 0 ? a[0].Description : name;
        }

        static readonly List<Item> Items = BuildItems();

        static List<Item> BuildItems()
        {
            var l = new List<Item>();
            void Add(string prop, string display, string cat, string desc, List<KeyValuePair<string, string>> opts = null) =>
                l.Add(new Item { Prop = prop, Display = display, Category = cat, Desc = desc, Options = opts });

            Add("DeviceClassStructure", "Device Class Structure", CAT_GUI, "Activates/Describes the Class View in parallel with the Network View. Value is a semicon string with Groups name and device Ids inside like HVAC(7000-7999,83001,83002);Lighting(2000,2100,2200);Building(HVAC,Lighting,27,40,1000-1999,9001)");
            Add("NotAffectedFolderName", "Device Mode View Not Affected name", CAT_GUI, "Name of the Folder for no affected Devices. Hidden if name is left blank");
            Add("DeviceViewMode", "Device Mode View", CAT_GUI, "Specify how the Device view should be organised", Names("Network", "DeviceClass", "NetworkThenDeviceClass", "DeviceClassThenNetwork"));
            Add("Vertical_Object_Splitter_Orientation", "Object splitter orientation", CAT_GUI, "Global windows split organisation in the GUI interface.",
                new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Horizontal", "False"), new KeyValuePair<string, string>("Vertical", "True") });
            Add("Auto_Store_Object_Names_File", "Auto-store objectnames file", CAT_GUI, "Name of the objectnames database file.");
            Add("ShowDescriptionWhenUseful", "Show description when useful", CAT_GUI, "Displays the description of objects in some places (requires more network activities to get it).");
            Add("Address_Space_Structured_View", "Address space view", CAT_GUI, "The Addendum 135d defines a 'Structured View' entry in the address space. This enables a hierarchical address space (selection 'Structured'). Though if you like the flat model better, set this to 'List'. The option both combines both display modes. FieldTechnician view is a flat view with filter described in the file SimplifiedViewFilter.xml", Names("List", "Structured", "Both", "FieldTechnician"));
            Add("DisplayIdWithName", "Display ID with name", CAT_GUI, "Leaves properties Id (such as ANALOG_INPUT:0) along with the properties name or hides this Id.");
            Add("GraphDotStyle", "Dot style", CAT_GUI, "Allows to select the style of the plotter dots.", Names("Square", "Diamond", "Triangle", "Circle", "XCross", "Plus", "Star", "TriangleDown", "HDash", "VDash", "Default", "None"));
            Add("GraphLineStep", "Line interpolation type", CAT_GUI, "Used by the graph to show or not a stair to link two successive values on a line. When set to Automatic, the graph will interpolate for analog values and step for binary/multistate values.", Names("Automatic", "Interpolated", "Stepped"));
            Add("Auto_Store_Object_Names", "Auto-store objectnames", CAT_GUI, "Automatic update of the objectnames database.");
            Add("Auto_Store_Period_Minutes", "Auto-store period minutes", CAT_GUI, "Objectnames database file flush period when new associations are available.");
            Add("ShowHighLightFilter", "Show highlight filter", CAT_GUI, "Allows to highlight items which match the filter. Helpful to select specific items in a larger device or object list.");
            Add("GridArrayExpandMaxSize", "Auto Expand Grid Array Max Size", CAT_GUI, "Maximum number of elements in Array to automatically expanded it in the Properties Grid. Zero to disable the option");
            Add("GridAlwaysExpandProperties", "Always Expanded Properties", CAT_GUI, "Always expand the Properties in Grid even with a large array (comma separated names, case sensible, no space)");
            Add("EDE_SingleFile", "Single file", CAT_EDEEXPORT, "Export multiple devices into a single EDE file.");
            Add("EDE_CommonFiles", "Common files", CAT_EDEEXPORT, "Generate files for objecttypes and units (known as common files).");
            Add("EDE_Separator", "EDE Separator", CAT_EDEEXPORT, "EDE Columns separator");
            Add("Subscriptions_IssueConfirmedNotifies", "Issue confirmed notifies", CAT_COV, "By default notifications will be sent 'unconfirmed'. If you think your notifications are important set this to 'true' instead.");
            Add("Subscriptions_Lifetime", "Lifetime", CAT_COV, "Subscriptions will be created with this lifetime. E.g. after 120 seconds the subscription will be removed by device. Set to 0 to disable.");
            Add("Subscriptions_ReplacementPollingPeriod", "Replacement polling period", CAT_COV, "Default time in milliseconds for the polling interval in case COV fails.");
            Add("COVTimeFormater", "COV time formater", CAT_COV, "Time format to display COV timestamps.");
            Add("COV_Export_Path", "COV export path", CAT_COV, "Default pathname to save COV graph export files.");
            Add("UsePollingByDefault", "Use polling by default", CAT_COV, "Do not try COV subscription but polling with a predefined period (see replacement polling period).");
            Add("Udp_ExclusiveUseOfSocket", "Exclusive use of socket", CAT_IP, "Set this to 'true' to force single socket usage on port 0xBAC0. A value of 'false' will create an extra unicast socket and allow multiple clients on same IP address / machine.");
            Add("DefaultUdpPort", "Default Port", CAT_IP, "Port number of the UDP port used. 47808 (0xBAC0) is reserved for BACnet traffic.");
            Add("Udp_DontFragment", "Do not fragment", CAT_IP, "This will enforce (if set to 'true') no fragmentation on the udp. It ought to be enforced, but it turns out that MTU is a bit tricky.");
            Add("Udp_MaxPayload", "Max. payload", CAT_IP, "The maximum payload for UDP seems to differ from the expectations of BACnet. The most common payload is 1472. Which is 1500 when added with the 28 bytes IP headers. This number is determined by your local switch / router though.");
            Add("DefaultUdpIp", "Default UDP/IP", CAT_IP, "IP address used as default for BACnet/IP communication.");
            Add("IPv6_Support", "IPv6 support", CAT_IP, "Enables or disables IPv6 communication.");
            Add("IPv6_MulticastAddress", "IPv6 Multicast address", CAT_IP, "Select the appropriate multicast address.",
                new[] { "FF02::BAC0 (Link-Local)", "FF04::BAC0 (Admin-Local)", "FF05::BAC0 (Site-Local)", "FF08::BAC0 (Organization-Local)", "FF0E::BAC0 (Global)" }
                    .Select((x, i) => new KeyValuePair<string, string>(x, i.ToString())).ToList());
            Add("DefaultBBMD", "Default BBMD", CAT_IP, "IP address and port of the BBMD to connect to as a foreign device.");
            Add("MSTP_DisplayFreeAddresses", "Display free addresses", CAT_MSTP, "By default a MS/TP connection will display all 'free' addresses in the 'Device' tree. This can help select a source address for the program. If you don't want to see the 'free' entries, set this option to 'false'");
            Add("MSTP_LogStateMachine", "Log state machine", CAT_MSTP, "The MS/TP code is able to display all state changes in log. This is very verbose. It may help you understand the MS/TP better though.");
            Add("DefaultBaudrate", "Default baudrate", CAT_MSTP, "Default Baudrate used for MS/TP communication",
                new[] { 9600, 19200, 38400, 57600, 76800, 115000 }.Select(b => new KeyValuePair<string, string>(b + " Baud", b.ToString())).ToList());
            Add("DefaultSourceAddress", "Default source address", CAT_MSTP, "Master source address of YABE. Must be unique in the MS/TP network and shall not be assigned to another MS/TP device.");
            Add("DefaultMaxMaster", "Default MaxMaster", CAT_MSTP, "'Max_Master' specifies the highest master station address. Must at least be set in the node with the highest station number to avoid unnecessary 'PollForMaster' requests.");
            Add("DefaultMaxInfoFrames", "Default MaxInfoFrames", CAT_MSTP, "Specifies the number of packets YABE is allowed to send after receiving the token. This allows a load balancing on the MS/TP network.");
            Add("BACnetSCConfigFile", "BACnetSC config file", CAT_SC, "Specifies the path and name of the configuration file used for BACnet/Secure Connect communication.");
            Add("DefaultRetries", "Default retries", CAT_GENERAL, "Number of APDU retries when no response is given to a request (such as read, write, ...)");
            Add("DefaultTimeout", "Default timeout", CAT_GENERAL, "APDU timeout in ms allowed to the remote device to give responses to requests (such as read, write, ...)");
            Add("DefaultDownloadSpeed", "Default download speed", CAT_GENERAL, "This value sets the method for 'file download'. (This is part of the original tests). The default value of '0' will result in a standard 'send request, wait for response' sequence. This is rather efficient on UDP. Value '1' will result in a 'stacked asynchronous' sequence. This is suited for MS/TP when combined with increasing the 'max_info_frames'. Value '2' will result in a 'segmented' sequence. This is the most efficient for both UDP and MS/TP.");
            Add("Segments_ProposedWindowSize", "Proposed window size", CAT_GENERAL, "");
            Add("Segments_Max", "Max. segments", CAT_GENERAL, "This value sets 'allowed max_segments' to send to the client. The client might not support segmentation though. If it gives you trouble, set this to '0' to disable.");
            Add("TimeSynchronize_UTC", "Time synchronize UTC", CAT_GENERAL, "BACnet allows time synchronization as local time (the devices must be located in the same timezone) or UTC (Universal Time Coordinated). UTC allows to synchronize the time across different timezones.");
            Add("DefaultWritePriority", "Default write priority", CAT_GENERAL, "Priorty level used for write operation. Can be changed without reboot. <Ctrl><Alt> + '0' to '9' keys are shortcuts to change this value directly from the main form (0: no priority to priority level 9, for others... no shortcut): a sound is played. To write a NULL value simply enter an empty value to the 'PresentValue' property.", EnumOpts<BacnetWritePriority>());
            Add("Plugins", "Plugins", CAT_GENERAL, "List of plugins to be loaded (separated by commas).");
            Add("YabeDeviceId", "Device ID", CAT_GENERAL, "If this value is positive Yabe send response to 'Who-Is' with this BACnet device ID. Can be usefull to set recipients list in notification class objects without using Yabe IP endpoint.");
            Add("UseObjectsCache", "Use Objects cache when useful", CAT_GENERAL, "Queries the dictionary via a cache if available. The Refresh button forces a real reading.");
            Add("Show_Property_Id_Numbers", "Show property ID numbers", CAT_GENERAL, "Displays the properties ID with there names.");
            Add("BackGroundOperations", "Background requests on IAm receptions", CAT_GENERAL, "Background queries of the Object Dictionary with or without the Names (twice bandwidth consuming), also or not on MSTP slow networks. GetAbsolutlyAll do the same even with a lot of single requests if required (highly time and bandwidth consuming, should be avoided) : experimental.",
                Names("None", "GetObjectsList", "GetObjectsName", "GetObjectsListIncludeMstp", "GetObjectsNameIncludeMstp", "GetAbsolutelyAll"));
            Add("BackGroundThreadNumber", "Background requests : Number of Tasks", CAT_GENERAL, "Number of parallel Background Tasks,max 10. Do not use more than 1 thread on direct network other than BACnet/IP.");
            Add("ByteArray_PreferHex", "Display byte arrays in hexidecimal", CAT_GENERAL, "Choose whether byte arrays are displayed in decimal, e.g. [0,32,255], or hexidecimal, e.g. [0x00,0x20,0xFF]. Note that byte arrays can always be entered as decimal or hexidecimal.");
            return l.OrderBy(i => i.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        readonly AppSettings instance;
        Border _selected;

        public SettingsDialog() : this(AppSettings.Default) { }

        public SettingsDialog(AppSettings instance)
        {
            this.instance = instance;
            InitializeComponent();
            BuildRows();
            // Make escape close the settings window (as the original does).
            KeyDown += (_, e) => { if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { Close(); e.Handled = true; } };
            Closed += (_, __) => instance.Save();
        }

        void BuildRows()
        {
            string cat = null;
            foreach (var it in Items)
            {
                var pi = typeof(AppSettings).GetProperty(it.Prop);
                if (pi == null) continue;
                if (it.Category != cat)
                {
                    cat = it.Category;
                    Rows.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                        Padding = new Thickness(6, 3),
                        Child = new TextBlock { Text = cat, FontWeight = FontWeight.Bold }
                    });
                }
                Rows.Children.Add(MakeRow(it, pi));
            }
        }

        Control MakeRow(Item it, PropertyInfo pi)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), MinHeight = 30 };
            var label = new TextBlock { Text = it.Display, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 2, 6, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            grid.Children.Add(label);
            Control editor;
            Type t = pi.PropertyType;
            object cur = pi.GetValue(instance);

            if (it.Options != null)
            {
                var opts = it.Options.ToList();
                string curStored = Convert.ToString(cur, CultureInfo.InvariantCulture);
                if (cur is bool b) curStored = b ? "True" : "False";
                if (!opts.Any(o => o.Value == curStored)) opts.Add(new KeyValuePair<string, string>(curStored, curStored));
                var combo = new ComboBox { ItemsSource = opts.Select(o => o.Key).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 26, Margin = new Thickness(2) };
                combo.SelectedIndex = opts.FindIndex(o => o.Value == curStored);
                combo.SelectionChanged += (_, __) =>
                {
                    if (combo.SelectedIndex < 0) return;
                    SetValue(pi, opts[combo.SelectedIndex].Value);
                };
                editor = combo;
            }
            else if (t == typeof(bool))
            {
                var combo = new ComboBox { ItemsSource = new[] { "False", "True" }, SelectedIndex = (bool)cur ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 26, Margin = new Thickness(2) };
                combo.SelectionChanged += (_, __) => SetValue(pi, combo.SelectedIndex == 1 ? "True" : "False");
                editor = combo;
            }
            else
            {
                var tb = new TextBox { Text = Convert.ToString(cur, CultureInfo.InvariantCulture), MinHeight = 26, Margin = new Thickness(2), VerticalContentAlignment = VerticalAlignment.Center };
                void Commit()
                {
                    if (!SetValue(pi, tb.Text)) tb.Text = Convert.ToString(pi.GetValue(instance), CultureInfo.InvariantCulture);
                }
                tb.LostFocus += (_, __) => Commit();
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
                editor = tb;
            }
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);

            var border = new Border { Child = grid, Background = Brushes.Transparent };
            border.PointerPressed += (_, __) => Select(border, it);
            editor.GotFocus += (_, __) => Select(border, it);
            return border;
        }

        void Select(Border row, Item it)
        {
            if (_selected != null) _selected.Background = Brushes.Transparent;
            _selected = row;
            row.Background = new SolidColorBrush(Color.FromArgb(50, 0, 120, 215));
            HelpTitle.Text = it.Display;
            HelpText.Text = it.Desc;
        }

        bool SetValue(PropertyInfo pi, string text)
        {
            try
            {
                Type t = pi.PropertyType;
                object v;
                if (t == typeof(string)) v = text ?? "";
                else if (t == typeof(bool)) v = bool.Parse(text);
                else v = Convert.ChangeType(text, t, CultureInfo.InvariantCulture);
                pi.SetValue(instance, v);
                return true;
            }
            catch { return false; }
        }
    }
}
