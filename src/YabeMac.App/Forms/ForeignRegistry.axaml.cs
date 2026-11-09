using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO.BACnet;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace YabeMac
{
    /// <summary>Foreign device registration dialog (port of Forms/ForeignRegistry).</summary>
    public partial class ForeignRegistry : Window
    {
        readonly BacnetClient client;
        DispatcherTimer stopWatchingIamTimer;

        // Original is a hidden singleton: the field values persist between invocations.
        static string lastPort = "47808", lastTtl = "30";

        public ForeignRegistry(BacnetClient client)
        {
            this.client = client;
            InitializeComponent();
            BBMD_IP.Text = AppSettings.Default.DefaultBBMD;
            BBMD_Port.Text = lastPort;
            TTL_Input.Text = lastTtl;
            stopWatchingIamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4000) };
            stopWatchingIamTimer.Tick += (_, __) => StopIamWatcher();   // remote whois probably did not work: stop listening
            Opened += (_, __) => BBMD_IP.Focus();
            Closed += (_, __) => { StopIamWatcher(); lastPort = BBMD_Port.Text; lastTtl = TTL_Input.Text; };
        }

        public static ForeignRegistry GetInstance(BacnetClient client) => new ForeignRegistry(client);

        int PortNumber()
        {
            int.TryParse(BBMD_Port.Text, out int port);
            return port == 0 ? 47808 : port;
        }

        short TTL()
        {
            if (!short.TryParse(TTL_Input.Text, out short ttl)) ttl = 30;
            return ttl;
        }

        IPAddress ResolveBbmd()
        {
            var ips = Dns.GetHostAddresses(BBMD_IP.Text);
            if (client.Transport is BacnetIpUdpProtocolTransport)
                return ips.First(o => o.AddressFamily == AddressFamily.InterNetwork);
            return ips.First(o => o.AddressFamily == AddressFamily.InterNetworkV6);
        }

        void OnSendFDR(object s, RoutedEventArgs e)
        {
            StopIamWatcher();
            try
            {
                var ip = ResolveBbmd();
                AppSettings.Default.DefaultBBMD = ip.ToString();
                AppSettings.Default.Save();

                client.RegisterAsForeignDevice(ip.ToString(), TTL(), PortNumber());
                Thread.Sleep(50);

                client.RemoteWhoIs(ip.ToString(), PortNumber());
                SendWhois.IsEnabled = true;

                // Listen for remote "Iam" broadcasts to know when to close the window automatically.
                StartIamWatcher();
            }
            catch (Exception ex)
            {
                Trace.TraceError("Error sending FD registration - " + ex.Message);
                MainWindow.Instance?.Log("Error sending FD registration - " + ex.Message);
            }
        }

        void IamWatcher(BacnetClient sender, BacnetAddress adr, uint device_id, uint max_apdu, BacnetSegmentations segmentation, ushort vendor_id)
        {
            // When we hear the first remote "Iam", the request worked so the window can go away.
            Dispatcher.UIThread.Post(() => { StopIamWatcher(); Close(); });
        }

        void StartIamWatcher() { client.OnIam += IamWatcher; stopWatchingIamTimer.Start(); }
        void StopIamWatcher() { client.OnIam -= IamWatcher; stopWatchingIamTimer.Stop(); }

        void OnSendWhois(object s, RoutedEventArgs e)
        {
            StopIamWatcher();
            try { client.RemoteWhoIs(ResolveBbmd().ToString(), PortNumber()); }
            catch (Exception ex)
            {
                Trace.TraceError("Error sending remote WhoIs - " + ex.Message);
                MainWindow.Instance?.Log("Error sending remote WhoIs - " + ex.Message);
            }
        }

        void OnCancel(object s, RoutedEventArgs e) { StopIamWatcher(); Close(); }
    }
}
