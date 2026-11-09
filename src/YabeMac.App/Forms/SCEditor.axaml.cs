using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Serialization;
using Yabe;

namespace YabeMac
{
    /// <summary>BACnet/SC channel configuration editor (port of Forms/SCEditor).</summary>
    public partial class SCEditor : Window
    {
        // Password not saved in the XML file between Yabe sessions
        public static string YabeCertificateFilePassword;

        readonly BACnetSCConfigChannel config;
        readonly string FileName;
        readonly XmlSerializer Xmlser = new XmlSerializer(typeof(BACnetSCConfigChannel));

        public SCEditor() : this(AppSettings.Default.BACnetSCConfigFile) { }

        public SCEditor(string fileName)
        {
            InitializeComponent();
            FileName = fileName;
            try
            {
                using (var sr = new StreamReader(FileName))
                    config = (BACnetSCConfigChannel)Xmlser.Deserialize(sr);

                UUID.Text = Guid.TryParse(config.UUID, out _) ? config.UUID : Guid.NewGuid().ToString();
                HubURI.Text = config.primaryHubURI;
                HubCert.Text = config.TrustedCertificatesFile;
                YabeCert.Text = config.OwnCertificateFile;
                ChkVerifyHubCert.IsChecked = config.ValidateHubCertificate;
                ChkDirectConnect.IsChecked = config.DirectConnect;
                if (config.VMAC != null && config.VMAC.Length == 6)
                    VMAC.Text = string.Join("", config.VMAC.Select(by => by.ToString("X2")));
                CapturePort.Value = config.WiresharkCapturePort;
                YabeCertPassword.Text = YabeCertificateFilePassword;
            }
            catch
            {
                Trace.TraceError("Error with BACnet/SC configuration file");
                MainWindow.Instance?.Log("Error with BACnet/SC configuration file: " + FileName + " (a new one will be created on Save)");
                config = new BACnetSCConfigChannel();
                UUID.Text = Guid.NewGuid().ToString();
            }
        }

        void OnSave(object s, RoutedEventArgs e)
        {
            config.UUID = UUID.Text;
            config.primaryHubURI = HubURI.Text ?? "";
            config.TrustedCertificatesFile = HubCert.Text;
            config.OwnCertificateFile = YabeCert.Text;
            config.ValidateHubCertificate = ChkVerifyHubCert.IsChecked == true;
            config.DirectConnect = ChkDirectConnect.IsChecked == true;

            // Password is kept in memory only, never saved in the XML file.
            YabeCertificateFilePassword = YabeCertPassword.Text;

            var vm = VMAC.Text ?? "";
            if (vm.Length == 12)
            {
                try
                {
                    config.VMAC = new byte[6];
                    for (int i = 0; i < 6; i++) config.VMAC[i] = Convert.ToByte(vm.Substring(i * 2, 2), 16);
                }
                catch { config.VMAC = null; }
            }
            else config.VMAC = null;

            config.WiresharkCapturePort = (int)(CapturePort.Value ?? -1);

            try
            {
                using (var sw = new StreamWriter(FileName))
                    Xmlser.Serialize(sw, config);
                Close();
            }
            catch (Exception ex)
            {
                Trace.TraceError("Error with BACnet/SC configuration file");
                Dialogs.Message("Error saving BACnet/SC configuration file: " + ex.Message, "BACnet/SC", this);
            }
        }

        void OnClear(object s, RoutedEventArgs e) => HubCert.Text = string.Empty;

        void OnViewCert(object s, RoutedEventArgs e)
        {
            try
            {
                X509Certificate2 cert = (s as Control)?.Tag as string == "yabe"
                    ? new X509Certificate2(YabeCert.Text, YabeCertPassword.Text)
                    : new X509Certificate2(HubCert.Text);
                ChannelsUi.ShowText(this, "Certificate", ChannelsUi.DescribeCertificate(cert), 560, 300);
            }
            catch
            {
                Trace.TraceError("Error with certificate file");
                Dialogs.Message("Error with certificate file", "BACnet/SC", this);
            }
        }

        async void OnSelCert(object s, RoutedEventArgs e)
        {
            string dir = null;
            try { dir = Path.GetDirectoryName(AppSettings.Default.Auto_Store_Object_Names_File); } catch { }
            var f = await ChannelsUi.PickFile(this, "Select certificate", "Certificates (*.p12;*.pfx;*.crt;*.pem;*.cer;*.der)",
                new[] { "*.p12", "*.pfx", "*.crt", "*.pem", "*.cer", "*.der" }, dir);
            if (f == null) return;
            if ((s as Control)?.Tag as string == "yabe") YabeCert.Text = f; else HubCert.Text = f;
        }

        void OnDirectConnectChanged(object s, RoutedEventArgs e)
        {
            if (ChkDirectConnect.IsChecked == true)
            {
                LblRemoteCertificate.Text = "Device or CA Certificate File";
                LblUri.Text = "Device URI";
            }
            else
            {
                LblRemoteCertificate.Text = "Hub or CA Certificate File";
                LblUri.Text = "Hub URI";
            }
        }

        void OnGetRemoteCertificate(object s, RoutedEventArgs e)
        {
            try
            {
                ChannelsInternals.GetRemoteCertificate(HubURI.Text, cert =>
                {
                    // Only here to get and display the certificate
                    var text = ChannelsUi.DescribeCertificate(cert);
                    Dispatcher.UIThread.Post(() => ChannelsUi.ShowText(this, "Remote certificate", text, 560, 300));
                });
            }
            catch { }
        }

        void OnCancel(object s, RoutedEventArgs e) => Close();

        void OnCertInformation(object s, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("For the \"Yabe Certificate File\", generate a certificate signing request (CSR) for Yabe, e.g. from OpenSSL. Note that the private key created alongside" +
                " the CSR is needed as well.");
            sb.AppendLine();
            sb.AppendLine("E.g.");
            sb.AppendLine("# openssl req -new -newkey rsa:2048 -nodes -keyout Yabe_BACnet_SC.key -out Yabe_BACnet_SC.csr -sha256");
            sb.AppendLine();
            sb.AppendLine("Or from the EasyRSA shell:");
            sb.AppendLine("# easyrsa gen-req Yabe_BACnet_SC nopass");
            sb.AppendLine();
            sb.AppendLine("Then import the CSR into wherever the certificate authority resides - your BACnet/SC device directly, the BACnet/SC device's configuration tool, an on-site CA" +
                " server, etc. Sign the CSR from the CA (the private key is required for signing). You should then have a signed certificate (.cer/.crt/.der/.pem/.pfx/.p12/etc.) which can" +
                " be loaded into Yabe and used to connect to the BACnet/SC device. If the signed certificate does not have the private key stored inside it (i.e. it is not a .p12 or .pfx)," +
                " you will also need the private key file next to the certificate (same file name but with the .key file extension).");
            sb.AppendLine();
            sb.AppendLine("A certificate and private key can be combined into a single package (usually a .p12/.pfx file). A .p12/.pfx can also include multiple certificates - i.e., the" +
                " CA certificate can be bundled with it.");
            sb.AppendLine();
            sb.AppendLine("For the \"HUB and/or CA Certificate(s) File\", we need a copy of at least one of the the CA chain's certificates (root CA or intermediate certificate) that was" +
                " used to sign the BACnet device's host certificate (and was perhaps also used to sign Yabe's certificate). However, this is only needed if \"Validate Remote Certificate\"" +
                " is checked. If the hub CA is the same as Yabe's certificate CA, and the certificate we specified for Yabe was a .p12/.pfx bundle with this CA certificate included, it" +
                " does not need to be manually specified here a second time. Note that it is not sufficient for this certificate to be present in your computer's certificate store -" +
                " it must be specified here. Remember that the CA certificate does not need to contain (and should not contain, for security reasons) the CA's private key.");
            ChannelsUi.ShowText(this, "Certificate Generation", sb.ToString(), 695, 450);
        }
    }
}
