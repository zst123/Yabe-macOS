using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Yabe;

namespace YabeMac
{
    /// <summary>"BACnet Communication Channel" dialog (port of Forms/SearchDialog).</summary>
    public partial class SearchDialog : Window
    {
        BacnetClient m_result;
        bool m_closed;
        bool m_loading = true;

        /// <summary>The client created by the chosen "Start" button (null when cancelled). Not started yet.</summary>
        public BacnetClient Result => m_result;
        public bool SendWhoIs => ChkSendWhois.IsChecked == true;
        public string WhoLimitLowText => WhoLimitLow.Text;
        public string WhoLimitHighText => WhoLimitHigh.Text;

        static AppSettings S => AppSettings.Default;

        public SearchDialog()
        {
            InitializeComponent();

            SCConfig.Text = S.BACnetSCConfigFile;

            // serial ports
            string[] ports = Array.Empty<string>();
            try { ports = System.IO.Ports.SerialPort.GetPortNames(); } catch { }
            SerialPortCombo.ItemsSource = ports.ToList();
            SerialPtpPortCombo.ItemsSource = ports.ToList();
            if (ports.Length > 0) { SerialPortCombo.Text = ports[0]; SerialPtpPortCombo.Text = ports[0]; }
            foreach (var ac in new[] { LocalUdpEndpointsCombo, SerialPortCombo, SerialPtpPortCombo, EthernetInterfaceCombo })
            {
                var box = ac;
                box.GotFocus += (_, __) => { if (box.IsEnabled) box.IsDropDownOpen = true; };
            }

            // settings-bound controls (original used data bindings with OnPropertyChanged)
            RetriesValue.Value = S.DefaultRetries;
            TimeoutValue.Value = S.DefaultTimeout;
            PortValue.Value = S.DefaultUdpPort;
            BaudValue.Value = S.DefaultBaudrate;
            PtpBaudRate.Value = S.DefaultBaudrate;
            SourceAddressValue.Value = S.DefaultSourceAddress;
            MaxMasterValue.Value = S.DefaultMaxMaster;
            MaxInfoFramesValue.Value = S.DefaultMaxInfoFrames;
            LocalUdpEndpointsCombo.Text = S.DefaultUdpIp;
            m_loading = false;

            RetriesValue.ValueChanged += (_, e) => { if (!m_loading && e.NewValue.HasValue) S.DefaultRetries = e.NewValue.Value; };
            TimeoutValue.ValueChanged += (_, e) => { if (!m_loading && e.NewValue.HasValue) S.DefaultTimeout = e.NewValue.Value; };
            PortValue.ValueChanged += (_, e) => { if (!m_loading && e.NewValue.HasValue) S.DefaultUdpPort = e.NewValue.Value; };
            BaudValue.ValueChanged += (_, e) => { if (!m_loading && e.NewValue.HasValue) { S.DefaultBaudrate = e.NewValue.Value; PtpBaudRate.Value = e.NewValue.Value; } };
            PtpBaudRate.ValueChanged += (_, e) => { if (!m_loading && e.NewValue.HasValue) { S.DefaultBaudrate = e.NewValue.Value; BaudValue.Value = e.NewValue.Value; } };
            SourceAddressValue.ValueChanged += (_, e) => { if (!m_loading && e.NewValue.HasValue) S.DefaultSourceAddress = e.NewValue.Value; };
            MaxMasterValue.ValueChanged += (_, e) => { if (!m_loading && e.NewValue.HasValue) S.DefaultMaxMaster = e.NewValue.Value; };
            MaxInfoFramesValue.ValueChanged += (_, e) => { if (!m_loading && e.NewValue.HasValue) S.DefaultMaxInfoFrames = e.NewValue.Value; };

            Opened += SearchDialog_Load;
            Closed += (_, __) => { m_closed = true; S.Save(); };
        }

        void SearchDialog_Load(object sender, EventArgs e)
        {
            // all local endpoints for udp
            var eps = GetAvailableIps();
            var current = S.DefaultUdpIp;
            LocalUdpEndpointsCombo.ItemsSource = eps;
            LocalUdpEndpointsCombo.Text = current;

            // Ethernet interfaces take a while: separate thread
            Task.Run(FillEthernetInterface);
        }

        int Timeout => (int)(TimeoutValue.Value ?? 1000);
        int Retries => (int)(RetriesValue.Value ?? 3);

        // Combo text bound to DefaultUdpIp
        string UdpIp
        {
            get { var t = LocalUdpEndpointsCombo.Text ?? ""; S.DefaultUdpIp = t; return t; }
        }

        void Accept(BacnetClient c) { m_result = c; Close(true); }

        void OnAddUdp(object s, RoutedEventArgs e)
        {
            try
            {
                string adr = UdpIp;
                int port = (int)(PortValue.Value ?? 47808);
                if (adr.Contains(':'))
                    Accept(new BacnetClient(
                        new BacnetIpV6UdpProtocolTransport(port, S.YabeDeviceId, S.Udp_ExclusiveUseOfSocket, S.Udp_DontFragment, S.Udp_MaxPayload, adr, S.IPv6_MulticastAddress),
                        Timeout, Retries));
                else
                    Accept(new BacnetClient(new BacnetIpUdpProtocolTransport(port, S.Udp_ExclusiveUseOfSocket, S.Udp_DontFragment, S.Udp_MaxPayload, adr), Timeout, Retries));
            }
            catch (Exception ex) { Dialogs.Message("Failed to create the BACnet/IP transport: " + ex.Message, "Error", this); }
        }

        void OnAddEthernet(object s, RoutedEventArgs e)
        {
            try
            {
                string[] sp = (EthernetInterfaceCombo.Text ?? "").Split(':');
                Accept(ChannelsInternals.CreateEthernetClient(sp[0], Timeout, Retries));
            }
            catch (Exception ex) { Dialogs.Message("Failed to create the BACnet/Ethernet transport: " + ex.Message, "Error", this); }
        }

        async void OnAddSc(object s, RoutedEventArgs e)
        {
            S.BACnetSCConfigFile = SCConfig.Text ?? "";
            var ser = new XmlSerializer(typeof(BACnetSCConfigChannel));
            BACnetSCConfigChannel configuration;
            try
            {
                using (var sr = new StreamReader(S.BACnetSCConfigFile))
                    configuration = (BACnetSCConfigChannel)ser.Deserialize(sr);
            }
            catch (Exception ex)
            {
                Trace.TraceError("Error with BACnet/SC XML configuration file : " + ex.Message);
                MainWindow.Instance?.Log("Error with BACnet/SC XML configuration file : " + ex.Message);
                return;
            }
            try
            {
                configuration.OwnCertificateFilePassword = SCEditor.YabeCertificateFilePassword;
                Accept(new BacnetClient(new BACnetTransportSecureConnect(configuration)));
            }
            catch (Exception ex)
            {
                await Dialogs.Message(ex.Message, "BACnet/SC Error", this);
            }
        }

        async void OnAddSerial(object s, RoutedEventArgs e)
        {
            try
            {
                string port = SerialPortCombo.Text ?? "";
                int com_number = 0;
                if (port.Length >= 3) int.TryParse(port.Substring(3), out com_number);
                BacnetMstpProtocolTransport transport;
                short src = (short)(SourceAddressValue.Value ?? -1);
                byte maxMaster = (byte)(MaxMasterValue.Value ?? 127);
                byte maxInfo = (byte)(MaxInfoFramesValue.Value ?? 1);
                if (com_number >= 1000)      // special "pipe" com ports (Windows only)
                    transport = new BacnetMstpProtocolTransport(new BacnetPipeTransport(port), src, maxMaster, maxInfo);
                else
                    transport = new BacnetMstpProtocolTransport(port, (int)(BaudValue.Value ?? 38400), src, maxMaster, maxInfo);
                transport.StateLogging = S.MSTP_LogStateMachine;
                Accept(new BacnetClient(transport, Timeout, Retries));
            }
            catch (Exception ex) { await Dialogs.Message("Failed to open the serial port: " + ex.Message, "Error", this); }
        }

        async void OnAddPtp(object s, RoutedEventArgs e)
        {
            try
            {
                string port = SerialPtpPortCombo.Text ?? "";
                int com_number = 0;
                if (port.Length >= 3) int.TryParse(port.Substring(3), out com_number);
                BacnetPtpProtocolTransport transport;
                if (com_number >= 1000)
                    transport = new BacnetPtpProtocolTransport(new BacnetPipeTransport(port), false);
                else
                    transport = new BacnetPtpProtocolTransport(port, (int)(PtpBaudRate.Value ?? 38400), false);
                transport.Password = PasswordText.Text;
                transport.StateLogging = S.MSTP_LogStateMachine;
                Accept(new BacnetClient(transport, Timeout, Retries));
            }
            catch (Exception ex) { await Dialogs.Message("Failed to open the serial port: " + ex.Message, "Error", this); }
        }

        void FillEthernetInterface() // not on the UI thread
        {
            void Ui(Action a) { try { Dispatcher.UIThread.Post(() => { if (!m_closed) a(); }); } catch { } }

            List<string> items;
            try { items = EthernetInterfaces.List(); }
            catch
            {
                Ui(() => { EthernetInterfaceCombo.Text = "libpcap not available"; });
                return;
            }
            Ui(() =>
            {
                EthernetInterfaceCombo.ItemsSource = items;
                EthernetInterfaceCombo.Text = items.Count > 0 ? items[0] : "";
                AddEthernetButton.IsEnabled = EthernetInterfaceCombo.IsEnabled = true;
            });
        }

        public static string[] GetAvailableIps()
        {
            var ips = new List<string>();
            foreach (var inf in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!inf.IsReceiveOnly && inf.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && inf.SupportsMulticast
                    && inf.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    foreach (var addr in inf.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                            (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && AppSettings.Default.IPv6_Support))
                            ips.Add(addr.Address.ToString());
                    }
                }
            }
            return ips.ToArray();
        }

        async void OnEditSC(object s, RoutedEventArgs e)
        {
            S.BACnetSCConfigFile = SCConfig.Text ?? "";
            await new SCEditor(S.BACnetSCConfigFile).ShowDialog(this);
        }

        async void OnSelectSC(object s, RoutedEventArgs e)
        {
            var opts = new FilePickerOpenOptions
            {
                Title = "Select BACnet/SC configuration file",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("XML Configuration Files (*.Config)") { Patterns = new[] { "*.Config", "*.config" } }, FilePickerFileTypes.All }
            };
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(S.BACnetSCConfigFile));
                if (!string.IsNullOrEmpty(dir)) opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);
            }
            catch { }
            var files = await StorageProvider.OpenFilePickerAsync(opts);
            if (files.Count == 0) return;
            SCConfig.Text = S.BACnetSCConfigFile = files[0].Path.LocalPath;
            S.Save();
        }

        void OnCancel(object s, RoutedEventArgs e) => Close(false);
    }
}
