using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using Yabe;

namespace YabeMac
{
    // View layer only: all BACnet behaviour comes from the original YABE sources
    // (BacnetClient, BACnetDevice, ...) linked in via YabeMac.Core.
    //
    // The window is split across partial classes by feature area:
    //   MainWindow.axaml.cs      shell, selection state, logging
    //   MainWindow.Network.cs    channels, discovery, device tree, object tree, properties
    //   Stubs.*.cs               feature areas still to be implemented (one file per area)
    public partial class MainWindow : Window
    {
        public static MainWindow Instance { get; private set; }

        /// <summary>All open BACnet channels (one BacnetClient each).</summary>
        public readonly List<BacnetClient> Channels = new List<BacnetClient>();

        public MainWindow()
        {
            Instance = this;
            InitializeComponent();
            InitNetwork();
            ApplyGlass();
            Closing += (_, __) => SaveWindowState();
            Opened += (_, __) => { RestoreWindowState(); Log("Yabe for macOS ready. Use Functions > Add BACnet channel."); OnStartup(); InitNativeMenu(); RunShotMode(); };
        }

        void OnStartup() { Startup.LoadOriginalData(); InitExportArea(); }
        void Todo(string what) => Log("Not implemented yet: " + what);

        void RestoreWindowState() { }
        void SaveWindowState() { AppSettings.Default.Save(); }

        public void Log(string text)
        {
            void Append() { LogText.Text += $"{DateTime.Now:HH:mm:ss}  {text}\n"; LogText.CaretIndex = LogText.Text.Length; }
            if (Dispatcher.UIThread.CheckAccess()) Append(); else Dispatcher.UIThread.Post(Append);
        }

        // ---- current selection -------------------------------------------------------------
        public DeviceNode SelectedDeviceNode => DeviceTree.SelectedItem as DeviceNode;
        public BACnetDevice SelectedDevice => SelectedDeviceNode?.Device;
        public ObjectNode SelectedObjectNode => ObjectTree.SelectedItem as ObjectNode;
        public BacnetObjectId? SelectedObjectId => SelectedObjectNode?.ObjectId;

        // ---- trivial handlers ----------------------------------------------------------------
        void OnQuit(object s, RoutedEventArgs e) => Close();
        void OnRestart(object s, RoutedEventArgs e) => Close();

        async void OnAbout(object s, RoutedEventArgs e) =>
            await Dialogs.Message("Yet Another Bacnet Explorer - Yabe\nmacOS port (Avalonia) of the SourceForge YABE project.\nBACnet logic is compiled from the original sources.", "About");

        void OnHelp(object s, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://sourceforge.net/projects/yetanotherbacnetexplorer/") { UseShellExecute = true }); } catch { }
        }
    }
}
