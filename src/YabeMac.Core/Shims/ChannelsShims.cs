// macOS port shims for the original YabeDevice (Yabe as a BACnet server).
namespace Yabe.Properties
{
    /// <summary>Minimal stand-in for the original Properties.Settings used by YabeDevice.cs.</summary>
    internal sealed class Settings
    {
        public static Settings Default { get; } = new Settings();
        public int YabeDeviceId { get; set; } = -1;
    }
}

namespace Yabe
{
    /// <summary>Public wrapper around the (internal) original YabeDevice.</summary>
    public sealed class YabeServer
    {
        readonly YabeDevice m_dev;
        public YabeServer(uint deviceId)
        {
            Yabe.Properties.Settings.Default.YabeDeviceId = (int)deviceId;
            m_dev = new YabeDevice(deviceId);
        }
        public void AddCom(System.IO.BACnet.BacnetClient c) => m_dev.AddCom(c);
        public void RemoveCom(System.IO.BACnet.BacnetClient c) => m_dev.RemoveCom(c);
        public void Iam(System.IO.BACnet.BacnetClient c) => m_dev.Iam(c);
    }
}

namespace Yabe
{
    /// <summary>pcap interface enumeration (code of SearchDialog.FillEthernetInterface, kept in Core where SharpPcap is referenced).</summary>
    public static class EthernetInterfaces
    {
        static bool s_resolver;
        /// <summary>SharpPcap imports "wpcap" (Windows name); map it to the system libpcap on macOS/Linux.</summary>
        public static void EnsureNativeResolver()
        {
            if (s_resolver) return;
            s_resolver = true;
            try
            {
                System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(SharpPcap.LibPcap.LibPcapLiveDeviceList).Assembly, (name, asm, path) =>
                {
                    if (name.IndexOf("pcap", System.StringComparison.OrdinalIgnoreCase) < 0) return System.IntPtr.Zero;
                    foreach (var cand in new[] { "libpcap.dylib", "/usr/lib/libpcap.dylib", "/usr/lib/libpcap.A.dylib", "libpcap.so.0.8", "libpcap.so", "libpcap.so.1" })
                        if (System.Runtime.InteropServices.NativeLibrary.TryLoad(cand, out var h)) return h;
                    return System.IntPtr.Zero;
                });
            }
            catch { }
        }

        /// <summary>Returns "FriendlyName: Description" of every usable Ethernet capture device. Throws when libpcap is unavailable.</summary>
        public static System.Collections.Generic.List<string> List()
        {
            EnsureNativeResolver();
            var items = new System.Collections.Generic.List<string>();
            var devices = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(SharpPcap.LibPcap.LibPcapLiveDeviceList.Instance, dev => dev.Interface != null));
            foreach (var device in devices)
            {
                if (device.Opened == false) // Don't re-open and list an already open interface
                {
                    try
                    {
                        device.Open();
                        if (device.LinkType == PacketDotNet.LinkLayers.Ethernet && device.Interface.MacAddress != null)
                            items.Add(device.Interface.FriendlyName + ": " + device.Interface.Description);
                        device.Close();
                    }
                    catch { }
                }
            }
            return items;
        }
    }
}

namespace Yabe
{
    public static class ChannelsInternals
    {
        /// <summary>BacnetEthernetProtocolTransport is internal in the original code.</summary>
        public static System.IO.BACnet.BacnetClient CreateEthernetClient(string interfaceName, int timeout, int retries)
            { EthernetInterfaces.EnsureNativeResolver(); return new System.IO.BACnet.BacnetClient(new System.IO.BACnet.BacnetEthernetProtocolTransport(interfaceName), timeout, retries); }

        /// <summary>Original SCEditor.GetRemoteCertificate: connects to the hub just to read its certificate (which is then rejected).</summary>
        public static void GetRemoteCertificate(string hubUri, System.Action<System.Security.Cryptography.X509Certificates.X509Certificate> onCertificate)
        {
            var uri = new System.Uri(hubUri); // WebSocket ctor fails if the string contains spaces
            var ws = new WebSocketSharp.WebSocket(uri.ToString());
            ws.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13;
            ws.SslConfiguration.ServerCertificateValidationCallback = (sender, cert, chain, errors) => { onCertificate(cert); return false; };
            ws.ConnectAsync();
        }
    }
}
