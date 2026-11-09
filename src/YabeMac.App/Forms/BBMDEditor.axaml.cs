using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.BACnet;
using System.Net;
using System.Threading.Tasks;

namespace YabeMac
{
    public class BbmdRow { public string IPAddress { get; set; } = ""; public string Port { get; set; } = "47808"; public string Mask { get; set; } = "255.255.255.255"; }
    public class FdrRow { public string IPendpoint { get; set; } public string TTL { get; set; } public string Remain { get; set; } }

    /// <summary>BBMD broadcast distribution / foreign device table editor (port of Forms/BBMDEditor).</summary>
    public partial class BBMDEditor : Window
    {
        readonly BacnetIpUdpProtocolTransport transport;
        readonly IPEndPoint BBMDep;
        bool WriteInOperation = false;
        readonly ObservableCollection<BbmdRow> _bbmd = new ObservableCollection<BbmdRow>();
        readonly ObservableCollection<FdrRow> _fdr = new ObservableCollection<FdrRow>();

        public BBMDEditor(BacnetClient comm, BacnetAddress adr)
        {
            InitializeComponent();
            BBMDTable.ItemsSource = _bbmd;
            FDRTable.ItemsSource = _fdr;

            transport = (BacnetIpUdpProtocolTransport)comm.Transport;
            var parts = adr.ToString().Split(':');
            BBMDep = new IPEndPoint(System.Net.IPAddress.Parse(parts[0]), Convert.ToInt32(parts[1]));

            transport.Bvlc.MessageReceived += Bvlc_MessageReceived;
            Opened += BBMDEditor_Load;
            Closing += (_, __) => transport.Bvlc.MessageReceived -= Bvlc_MessageReceived;
        }

        void Bvlc_MessageReceived(IPEndPoint sender, BacnetBvlcFunctions function, BacnetBvlcResults result, object data)
        {
            if (!sender.Equals(BBMDep)) return; // don't care
            Dispatcher.UIThread.Post(() => Handle(function, result, data));   // GUI call back
        }

        void Handle(BacnetBvlcFunctions function, BacnetBvlcResults result, object data)
        {
            if (function == BacnetBvlcFunctions.BVLC_READ_BROADCAST_DIST_TABLE_ACK) // a table (could be empty)
            {
                var entries = (List<Tuple<IPEndPoint, IPAddress>>)data;
                _bbmd.Clear();
                foreach (var tpl in entries)
                    _bbmd.Add(new BbmdRow { IPAddress = tpl.Item1.Address.ToString(), Port = tpl.Item1.Port.ToString(), Mask = tpl.Item2.ToString() });
                LbBBMDInfo.IsVisible = false;
            }

            if (function == BacnetBvlcFunctions.BVLC_READ_FOREIGN_DEVICE_TABLE_ACK)
            {
                var entries = (List<Tuple<IPEndPoint, ushort, ushort>>)data;
                _fdr.Clear();
                foreach (var tpl in entries)
                    _fdr.Add(new FdrRow { IPendpoint = tpl.Item1.ToString(), TTL = tpl.Item2.ToString(), Remain = tpl.Item3.ToString() });
                LbFDRInfo.IsVisible = false;
            }
            if (function == BacnetBvlcFunctions.BVLC_RESULT && result == BacnetBvlcResults.BVLC_RESULT_SUCCESSFUL_COMPLETION && WriteInOperation)
            {
                WriteInOperation = false;
                transport.Bvlc.SendReadBroadCastTable(BBMDep);
            }

            if (function == BacnetBvlcFunctions.BVLC_RESULT && result == BacnetBvlcResults.BVLC_RESULT_READ_BROADCAST_DISTRIBUTION_TABLE_NAK)
            {
                LbBBMDInfo.IsVisible = true;
                LbBBMDInfo.Text = "Read Broadcast Table Rejected";
            }
            if (function == BacnetBvlcFunctions.BVLC_RESULT && result == BacnetBvlcResults.BVLC_RESULT_WRITE_BROADCAST_DISTRIBUTION_TABLE_NAK && WriteInOperation)
            {
                LbBBMDInfo.IsVisible = true;
                WriteInOperation = false;
                LbBBMDInfo.Text = "Write Broadcast Table Rejected";
            }
            if (function == BacnetBvlcFunctions.BVLC_RESULT && result == BacnetBvlcResults.BVLC_RESULT_READ_FOREIGN_DEVICE_TABLE_NAK)
            {
                LbFDRInfo.IsVisible = true;
                LbFDRInfo.Text = "Read FDR Table Rejected";
            }
        }

        async void BBMDEditor_Load(object sender, EventArgs e)
        {
            transport.Bvlc.SendReadBroadCastTable(BBMDep);
            await Task.Delay(100); // wait a little
            transport.Bvlc.SendReadFDRTable(BBMDep);
        }

        void OnAddRow(object s, RoutedEventArgs e) => _bbmd.Add(new BbmdRow());

        void OnDeleteRow(object s, RoutedEventArgs e)
        {
            if (BBMDTable.SelectedItem is BbmdRow r) _bbmd.Remove(r);
        }

        void OnWrite(object s, RoutedEventArgs e)
        {
            var entries = new List<Tuple<IPEndPoint, IPAddress>>();
            int idx = 0;
            try
            {
                foreach (var row in _bbmd)
                {
                    var ep = new IPEndPoint(System.Net.IPAddress.Parse(row.IPAddress), Convert.ToInt32(row.Port));
                    var mask = System.Net.IPAddress.Parse(row.Mask);
                    entries.Add(new Tuple<IPEndPoint, IPAddress>(ep, mask));
                    idx++;
                }
                WriteInOperation = true;
                transport.Bvlc.SendWriteBroadCastTable(BBMDep, entries);
            }
            catch (Exception ex)
            {
                Dialogs.Message(ex.Message, "Error at line " + (idx + 1), this);
            }
        }

        void OnRefresh(object s, RoutedEventArgs e) => transport.Bvlc.SendReadFDRTable(BBMDep);
    }
}
