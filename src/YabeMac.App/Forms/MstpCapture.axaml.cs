using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using Yabe.MstpCapture;

namespace YabeMac
{
    /// <summary>
    /// Port of Mstp.BacnetCapture (BacnetCapture.cs/.Designer.cs): sniffs MS/TP frames from a serial port (or from the
    /// transport of the selected Yabe channel), shows per-station statistics and forwards the frames to Wireshark
    /// through a pipe (FIFO /tmp/bacnet on macOS, instead of \\.\pipe\bacnet).
    /// </summary>
    public partial class MstpCapture : Window
    {
        // Original: private static singleton flag
        public static bool SingletonIsRunning { get; private set; }
        public static MstpCapture Instance { get; private set; }

        readonly BacnetMstpProtocolTransport sharedSerial;
        MstpCaptureSession session;
        string master = "Node ";
        readonly Bitmap iconPlc = Load(MstpCaptureIcons.Plc), iconStat = Load(MstpCaptureIcons.Stat2), iconLoupe = Load(MstpCaptureIcons.Loupe);
        readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        readonly object dirtyLock = new object();
        bool dirty;

        class NodeUi { public TreeViewItem Item; public TextBlock Total, Mttr; public TextBlock[] Detail = new TextBlock[10]; }
        readonly Dictionary<byte, NodeUi> nodeUi = new Dictionary<byte, NodeUi>();

        static Bitmap Load(string b64) => new Bitmap(new MemoryStream(Convert.FromBase64String(b64)));

        /// <summary>serial == null: standalone (port selection); otherwise launched from the main window on its MS/TP transport.</summary>
        public MstpCapture(BacnetMstpProtocolTransport serial = null)
        {
            sharedSerial = serial;
            InitializeComponent();
            Opened += OnLoad;
            Closing += OnFormClosing;
            refreshTimer.Tick += (_, __) => RefreshTree();
        }

        string[] portItems = new string[0];
        static readonly string[] speedItems = { "9600", "19200", "38400", "57600", "76800", "115200" };

        void DropDown(Control anchor, TextBox target, string[] items)
        {
            var menu = new ContextMenu { PlacementTarget = anchor };
            var list = new List<MenuItem>();
            foreach (string it in items)
            {
                var mi = new MenuItem { Header = it };
                mi.Click += (_, __) => target.Text = it;
                list.Add(mi);
            }
            menu.ItemsSource = list;
            menu.Open(anchor);
        }
        void OnDropPort(object sender, RoutedEventArgs e) => DropDown(comboPortBtn, comboPort, portItems);
        void OnDropSpeed(object sender, RoutedEventArgs e) => DropDown(comboSpeedBtn, comboSpeed, speedItems);

        void OnLoad(object sender, EventArgs e)
        {
            SingletonIsRunning = true;
            Instance = this;
            session = new MstpCaptureSession();
            textBox1.Text = session.PipePath;
            label7.Text = "... start it with the command  : wireshark -ni " + session.PipePath;

            if (sharedSerial == null)
            {
                string[] ports = MstpCaptureSession.GetSerialPorts();
                portItems = ports;
                
                comboSpeed.Text = "38400";
                // select the last one which is certainly an Usb to Rs485 adapter
                comboPort.Text = MstpCaptureSession.DefaultPort(ports);
            }
            else // launch from Yabe
            {
                label2.IsVisible = label3.IsVisible = false;
                comboPortBox.IsVisible = comboSpeedBox.IsVisible = false;
                label6.IsVisible = true;
                buttonGo.IsVisible = false;
            }

            ToolTip.SetTip(this, "© Frederic Chaxel, Morten Kvistgaard 2015\r\n MIT License\r\n Thanks to http://icons8.com/");
            ToFrench();

            if (sharedSerial != null) OnGo(null, null);
        }

        void OnFormClosing(object sender, WindowClosingEventArgs e)
        {
            SingletonIsRunning = false;
            if (Instance == this) Instance = null;
            refreshTimer.Stop();
            // Do not close the Port if launched by Yabe: it is already in use (session.Stop leaves a shared transport alone)
            session?.Stop();
        }

        // change language - too much simple for using a resources class for that.
        void ToFrench()
        {
            if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" || CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "fr")
            {
                label1.Text = "Wireshark doit être à l'écoute sur le Pipeline :";
                label2.Text = "Port Série :";
                label3.Text = "Vitesse :";
                label4.Text = "Filtre de suppression";
                label5.Text = "Statistiques d'envois";
                label6.Text = "Cet outil peut être utilisé sans Yabe (mais le port ne peut pas être partagé)";
                label7.Text = "... démarrer le avec la commande : wireshark -ni " + session.PipePath;
                linkLabel1.Content = "Démarrer Wireshark avec capture immédiate";
                master = "Noeud ";
            }
        }

        // Serial Transport Listener start
        async void OnGo(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sharedSerial == null)   // Standalone application
                {
                    if (!int.TryParse(comboSpeed.Text, out int baud)) baud = 38400;
                    session.Start(comboPort.Text?.Trim() ?? "", baud);
                }
                else
                    session.Start(sharedSerial);

                if (session.Wireshark.Error != null)
                    await Dialogs.Message("Wireshark pipe : " + session.Wireshark.Error, "Mstp capture", this);

                session.FrameReceived += (_, __) => { lock (dirtyLock) dirty = true; };
                refreshTimer.Start();
                buttonGo.IsEnabled = false;
                linkLabel1.IsEnabled = true;
            }
            catch (Exception ex)
            {
                // the original silently ignores the error, here the user is told why nothing happens
                await Dialogs.Message("Cannot open the serial port : " + ex.Message, "Mstp capture", this);
            }
        }


        TreeViewItem MakeItem(Bitmap icon, string text, out TextBlock tb)
        {
            tb = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (icon != null) panel.Children.Add(new Image { Source = icon, Width = 16, Height = 16 });
            panel.Children.Add(tb);
            return new TreeViewItem { Header = panel };
        }

        // A new node on Mstp, add it to the list and into the treeview (sorted by source address)
        NodeUi MakeNewNode(byte address)
        {
            var ui = new NodeUi();
            ui.Item = MakeItem(iconPlc, master + address, out _);

            var global = MakeItem(iconStat, "Global", out _);
            var t1 = MakeItem(null, "", out ui.Total);
            var t2 = MakeItem(null, "", out ui.Mttr);
            global.ItemsSource = new List<TreeViewItem> { t1, t2 };

            var detail = MakeItem(iconLoupe, "Detail", out _);
            var rows = new List<TreeViewItem>();
            for (int i = 0; i < 10; i++) rows.Add(MakeItem(null, "", out ui.Detail[i]));
            detail.ItemsSource = rows;

            ui.Item.ItemsSource = new List<TreeViewItem> { global, detail };
            ui.Item.Tag = (int)address;

            var list = treeView.Items.Cast<TreeViewItem>().ToList();
            int idx = list.FindIndex(t => (int)t.Tag > address);
            if (idx < 0) idx = list.Count;
            list.Insert(idx, ui.Item);
            treeView.ItemsSource = list;
            nodeUi[address] = ui;

            if (list.Count == 1) // Expand the first node
            {
                ui.Item.IsExpanded = true; global.IsExpanded = true; detail.IsExpanded = true;
            }
            return ui;
        }

        // Update HMI and counters
        void RefreshTree()
        {
            lock (dirtyLock) { if (!dirty) return; dirty = false; }
            foreach (var n in session.Snapshot())
            {
                if (!nodeUi.TryGetValue(n.Num, out var ui)) ui = MakeNewNode(n.Num);
                for (int i = 0; i < 10; i++)
                    ui.Detail[i].Text = n.FrameTypeStatistic[i] + " - " + MstpCaptureSession.GetNiceName(i);
                ui.Total.Text = n.TotalFrames + " Total";
                ui.Mttr.Text = n.MeanTimeTokenRotation + " ms MTTR";
            }
        }

        // Frame filter for Wireshark
        void OnFilterChanged(object sender, RoutedEventArgs e)
        {
            if (session == null) return;   // raised while the XAML is loading
            // boolean affectation is thread safe, no need to do more
            var f = session.FiltreMessages;
            f[0] = checkToken.IsChecked == true;
            f[1] = f[2] = checkMaster.IsChecked == true;
            f[3] = f[4] = checkTest.IsChecked == true;
            f[5] = f[6] = f[7] = checkData.IsChecked == true;
        }

        async void OnLinkClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = new[] { "/Applications/Wireshark.app/Contents/MacOS/Wireshark", "/usr/local/bin/wireshark", "/opt/homebrew/bin/wireshark", "/usr/bin/wireshark" }
                    .FirstOrDefault(File.Exists);
                ProcessStartInfo psi;
                if (path != null) psi = new ProcessStartInfo(path);
                else psi = new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-a", "Wireshark", "--args" } };
                psi.ArgumentList.Add("-ni"); psi.ArgumentList.Add(session.PipePath); psi.ArgumentList.Add("-k");
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch
            {
                await Dialogs.Message("Error starting Wireshark", "Mstp capture", this);
            }
        }

        // Test / headless aid
        internal MstpCaptureSession Session => session;
        internal void RefreshNow() { lock (dirtyLock) dirty = true; RefreshTree(); }
    }
}
