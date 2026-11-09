using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    // Area "Channels & settings dialogs". Ports MainDialog.Menus.cs (channel creation, IP services,
    // settings) and the helper FetchTransportClientEndPoint from MainDialog.cs.
    public partial class MainWindow
    {
        /// <summary>Yabe acting as a BACnet device (Settings > Device ID >= 0). Shared by all channels.</summary>
        YabeServer m_Server;

        /// <summary>Per-channel MS/TP bookkeeping (BacnetDeviceLine.mstp_* of the original).</summary>
        class MstpLine { public readonly HashSet<byte> SourcesSeen = new HashSet<byte>(); public readonly HashSet<byte> PfmDestinationsSeen = new HashSet<byte>(); }
        readonly Dictionary<BacnetClient, MstpLine> _mstpLines = new Dictionary<BacnetClient, MstpLine>();

        // ---------------------------------------------------------------------------------
        // Add channel
        // ---------------------------------------------------------------------------------

        async void OnAddChannel(object s, RoutedEventArgs e)
        {
            var dlg = new SearchDialog();
            await dlg.ShowDialog(this);
            var comm = dlg.Result;
            if (comm == null) return;

            int idMin = -1, idMax = -1;
            int.TryParse(dlg.WhoLimitLowText, out idMin); int.TryParse(dlg.WhoLimitHighText, out idMax);
            if (!int.TryParse(dlg.WhoLimitLowText, out _)) idMin = -1;
            if (!int.TryParse(dlg.WhoLimitHighText, out _)) idMax = -1;
            StartChannel(comm, dlg.SendWhoIs, idMin, idMax);
        }

        /// <summary>Wires, starts and registers a freshly created client (original addDevicesearchToolStripMenuItem_Click).</summary>
        public void StartChannel(BacnetClient comm, bool sendWhoIs, int idMin = -1, int idMax = -1)
        {
            var S = AppSettings.Default;
            try
            {
                comm.ProposedWindowSize = S.Segments_ProposedWindowSize;
                comm.Retries = (int)S.DefaultRetries;
                comm.Timeout = (int)S.DefaultTimeout;
                comm.MaxSegments = BacnetClient.GetSegmentsCount(S.Segments_Max);

                if (S.YabeDeviceId >= 0) // Yabe gets a device id
                {
                    if (m_Server == null) m_Server = new YabeServer((uint)S.YabeDeviceId);
                    m_Server.AddCom(comm);
                }

                AddChannel(comm, comm.ToString(), false);   // wires OnIam/COV/Event, starts, adds to tree

                m_Server?.Iam(comm); // not done when not appropriate (e.g. MS/TP without source address)

                if (sendWhoIs)
                {
                    // WhoIs Min & Max limits
                    if (idMin == 0) idMin = -1; if (idMax == 0) idMax = -1;
                    if (idMin != -1 && idMax == -1) idMax = 0x3FFFFF;
                    if (idMax != -1 && idMin == -1) idMin = 0;

                    if (comm.Transport.Type == BacnetAddressTypes.IP
                        || comm.Transport.Type == BacnetAddressTypes.Ethernet
                        || comm.Transport.Type == BacnetAddressTypes.IPV6
                        || comm.Transport.Type == BacnetAddressTypes.SC
                        || (comm.Transport is BacnetMstpProtocolTransport mt && mt.SourceAddress != -1)
                        || comm.Transport.Type == BacnetAddressTypes.PTP)
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                for (int i = 0; i < comm.Retries; i++)
                                {
                                    comm.WhoIs(idMin, idMax);
                                    System.Threading.Thread.Sleep(comm.Timeout);
                                }
                            }
                            catch (Exception ex) { Log("Who-Is failed: " + ex.Message); }
                        });
                    }
                }

                // special MSTP auto discovery
                if (comm.Transport is BacnetMstpProtocolTransport mstp)
                {
                    _mstpLines[comm] = new MstpLine();
                    mstp.FrameRecieved += MSTP_FrameRecieved;
                }

                if (_channelNodes.TryGetValue(comm, out var node)) DeviceTree.SelectedItem = node;
            }
            catch (Exception ex)
            {
                // roll back what AddChannel may have done
                if (_channelNodes.TryGetValue(comm, out var node)) { _deviceRoots.Remove(node); _channelNodes.Remove(comm); }
                Channels.Remove(comm);
                m_Server?.RemoveCom(comm);
                try { comm.Dispose(); } catch { }
                _ = Dialogs.Message("Couldn't start BACnet communication: " + ex.Message, "Error", this);
            }
        }

        void MSTP_FrameRecieved(BacnetMstpProtocolTransport sender, BacnetMstpFrameTypes frame_type, byte destination_address, byte source_address, int msg_length)
        {
            try
            {
                var comm = Channels.FirstOrDefault(c => c.Transport == sender);
                if (comm == null || !_mstpLines.TryGetValue(comm, out var line)) return;
                bool newSource = false, newFree = false;
                lock (line)
                {
                    if (line.SourcesSeen.Add(source_address)) newSource = true;
                    if (frame_type == BacnetMstpFrameTypes.FRAME_TYPE_POLL_FOR_MASTER && !line.PfmDestinationsSeen.Contains(destination_address) && sender.SourceAddress != destination_address)
                    {
                        line.PfmDestinationsSeen.Add(destination_address);
                        if (!line.SourcesSeen.Contains(destination_address) && AppSettings.Default.MSTP_DisplayFreeAddresses) newFree = true;
                    }
                }
                if (newSource)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_channelNodes.TryGetValue(comm, out var parent)) return;
                        var free = parent.Children.FirstOrDefault(n => n.Title == "free" + source_address);
                        var dev = new BACnetDevice(comm, new BacnetAddress(BacnetAddressTypes.MSTP, 0, new byte[] { source_address }), 0x3FFFFF);
                        var node = new DeviceNode { Device = dev, Title = "device" + source_address };
                        dev.Tag = node;
                        if (free != null) parent.Children.Remove(free);
                        parent.Children.Add(node);
                        if (source_address == sender.SourceAddress) Log("Mstp Collision detected");
                    });
                if (newFree)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_channelNodes.TryGetValue(comm, out var parent))
                            parent.Children.Add(new DeviceNode { Title = "free" + destination_address });
                    });
            }
            catch { /* closing down */ }
        }

        /// <summary>
        /// Original device-selection behaviour for an unconfigured MS/TP transport: ask for a source address.
        /// Returns false when the user declined/cancelled (caller should abort). Callable by other areas.
        /// </summary>
        public async Task<bool> EnsureMstpSourceAddress(BacnetClient comm)
        {
            if (!(comm.Transport is BacnetMstpProtocolTransport mt) || mt.SourceAddress != -1) return true;
            if (!await Dialogs.Confirm("The MSTP transport is not yet configured. Would you like to set source_address now?", "Set Source Address")) return false;

            byte address = 0xFF;   // suggested address
            if (_mstpLines.TryGetValue(comm, out var line))
                lock (line)
                    foreach (byte b in line.PfmDestinationsSeen)
                        if (b < address && !line.SourcesSeen.Contains(b)) address = b;
            if (address > 127) address = 127;

            var chosen = await SourceAddressDialog.Prompt(address, this);
            if (chosen == null) return false;
            mt.SourceAddress = chosen.Value;
            return true;
        }

        // ---------------------------------------------------------------------------------
        // Transport selection (FetchTransportClientEndPoint)
        // ---------------------------------------------------------------------------------

        async Task<BacnetClient> FetchTransportClientEndPoint(Type transportType, bool withErrorMessage = true)
        {
            BacnetClient comm = SelectedDeviceNode?.Channel ?? SelectedDevice?.channel;

            async Task Wrong()
            {
                if (!withErrorMessage) return;
                if (transportType == typeof(BacnetIpUdpProtocolTransport))
                    await Dialogs.Message("Please select an \"IP V4 transport\" node first", "Wrong node");
                else if (transportType == typeof(BacnetMstpProtocolTransport))
                    await Dialogs.Message("Please select an \"Mstp transport\" node first", "Wrong node");
                else
                    await Dialogs.Message("Please select a \"transport " + transportType.Name + "\" node first", "Wrong node");
            }

            if (comm != null && comm.Transport != null && comm.Transport.GetType() != transportType)
            {
                await Wrong();
                return null;
            }

            if (comm == null)
            {
                var clients = Channels.Where(c => c.Transport != null && c.Transport.GetType() == transportType).ToList();
                if (clients.Count == 1) comm = clients[0];
                else if (clients.Count > 1)
                {
                    int idx = await ChannelsUiPick.PickFromList(this, "Transport layer operation", "Select a transport layer:", clients.Select(c => c.ToString()).ToList());
                    if (idx < 0) return null;
                    comm = clients[idx];
                }
            }

            if (comm == null)
            {
                await Wrong();
                return null;
            }
            return comm;
        }

        // ---------------------------------------------------------------------------------
        // IP services
        // ---------------------------------------------------------------------------------

        async void OnForeignDeviceRegistration(object s, RoutedEventArgs e)
        {
            var comm = await FetchTransportClientEndPoint(typeof(BacnetIpUdpProtocolTransport));
            if (comm == null) return;
            await ForeignRegistry.GetInstance(comm).ShowDialog(this);
        }

        async void OnAddRemoteIpNode(object s, RoutedEventArgs e)
        {
            var comm = await FetchTransportClientEndPoint(typeof(BacnetIpUdpProtocolTransport));
            if (comm == null) return;

            try
            {
                string input = await Dialogs.Input("DeviceId - xx.xx.xx.xx:47808\n(Unknown device Id can be replaced by 4194303, ? or omitted)", "Ipv4/Udp Bacnet Node", "", this);
                if (input == null) return;

                uint devId = 4194303;
                string ep;
                string[] entry = input.Split('-');
                if (entry.Length == 2)
                {
                    if (entry[0].Trim().Length != 0) uint.TryParse(entry[0], out devId);
                    ep = entry[1].Trim();
                }
                else ep = entry[0].Trim();

                if (!ep.Contains(':')) ep += ":47808"; // implicit port
                if (!TryParseIPEndPoint(ep, out _)) throw new FormatException();

                var adr = new BacnetAddress(BacnetAddressTypes.IP, ep);
                if (devId >= 0x3FFFFF && _channelNodes.TryGetValue(comm, out var chanNode))
                {
                    // Unknown device id: ask the node directly (unicast Who-Is) so that its real I-Am gives the id.
                    comm.WhoIs(-1, -1, adr);
                    Log("Unicast Who-Is sent to " + ep);
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(100);
                        if (chanNode.Children.Any(n => n.Device != null && n.Device.BacAdr.Equals(adr))) return;
                    }
                    // no answer: add it as an unknown device like the original does
                }
                OnIam(comm, adr, devId, 0, BacnetSegmentations.SEGMENTATION_NONE, 0);
            }
            catch
            {
                await Dialogs.Message("Invalid parameter", "Wrong node or IP @");
            }
        }

        static bool TryParseIPEndPoint(string input, out IPEndPoint endPoint)
        {
            endPoint = null;
            string[] ep = input.Split(':');
            if (ep.Length != 2) return false;
            if (!IPAddress.TryParse(ep[0], out var ip)) return false;
            if (!int.TryParse(ep[1], System.Globalization.NumberStyles.None, System.Globalization.NumberFormatInfo.CurrentInfo, out int port)) return false;
            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort) return false;
            endPoint = new IPEndPoint(ip, port);
            return true;
        }

        async void OnAddRemoteIpNodeList(object s, RoutedEventArgs e)
        {
            var comm = await FetchTransportClientEndPoint(typeof(BacnetIpUdpProtocolTransport));
            if (comm == null) return;

            string fileName = await ChannelsUi.PickFile(this, "Select remote node list", "Text files", null);
            if (fileName == null) return;

            try
            {
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
                string[] lines = await Task.Run(() => File.ReadAllLines(fileName));
                foreach (string line in lines)
                {
                    if (line.StartsWith("#")) continue;   // comment
                    string[] entry = line.Split('-');
                    string bad = $"Failed to add a remote IPv4 node: \"{line.Trim()}\" is not in the correct format (DeviceId - IP1.IP2.IP3.IP4:Port).";
                    if (entry.Length != 2) { Log(bad); continue; }
                    if (entry[0].Contains('?')) entry[0] = "4194303";
                    if (!uint.TryParse(entry[0].Trim(), out uint deviceIdIn)) { Log(bad); continue; }
                    if (!TryParseIPEndPoint(entry[1].Trim(), out IPEndPoint ipIn)) { Log(bad); continue; }
                    try
                    {
                        OnIam(comm, new BacnetAddress(BacnetAddressTypes.IP, entry[1].Trim()), deviceIdIn, 0, BacnetSegmentations.SEGMENTATION_NONE, 0);
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to add a remote IPv4 node: {ex.GetType().Name} - {ex.Message}");
                        continue;
                    }
                    Log($"Added remote IPv4 node: {deviceIdIn} - {ipIn}");
                }
            }
            catch { }
            finally { Cursor = Avalonia.Input.Cursor.Default; }
        }

        async void OnEditBbmd(object s, RoutedEventArgs e)
        {
            var device = SelectedDevice;
            if (device != null && device.channel.Transport is BacnetIpUdpProtocolTransport && device.BacAdr != null && device.BacAdr.RoutedSource == null)
                await new BBMDEditor(device.channel, device.BacAdr).ShowDialog(this);
            else
                await Dialogs.Message("An IPv4 device is required", "Wrong device");
        }

        // ---------------------------------------------------------------------------------
        // Settings / tools
        // ---------------------------------------------------------------------------------

        async void OnSettings(object s, RoutedEventArgs e)
        {
            bool prevVertOrientation = AppSettings.Default.Vertical_Object_Splitter_Orientation;
            await new SettingsDialog(AppSettings.Default).ShowDialog(this);
            if (prevVertOrientation ^ AppSettings.Default.Vertical_Object_Splitter_Orientation)
                ApplyObjectSplitterOrientation(AppSettings.Default.Vertical_Object_Splitter_Orientation);
        }

        /// <summary>Devices/Objects trees stacked (horizontal splitter) or side by side (vertical splitter).</summary>
        void ApplyObjectSplitterOrientation(bool vertical)
        {
            try
            {
                var g = LeftGrid;
                if (g.Children.Count != 3) return;
                if (vertical)
                {
                    g.ColumnDefinitions = new ColumnDefinitions("*,4,*");
                    g.RowDefinitions = new RowDefinitions("*");
                    for (int i = 0; i < 3; i++) { Grid.SetRow(g.Children[i], 0); Grid.SetColumn(g.Children[i], i); }
                    ((GridSplitter)g.Children[1]).ResizeDirection = GridResizeDirection.Columns;
                }
                else
                {
                    g.RowDefinitions = new RowDefinitions("*,4,*");
                    g.ColumnDefinitions = new ColumnDefinitions();
                    for (int i = 0; i < 3; i++) { Grid.SetColumn(g.Children[i], 0); Grid.SetRow(g.Children[i], i); }
                    ((GridSplitter)g.Children[1]).ResizeDirection = GridResizeDirection.Rows;
                }
            }
            catch { }
        }

        async void OnMstpCapture(object s, RoutedEventArgs e)
        {
            // Mstp.BacnetCapture (Forms/MstpCapture): sniffs the selected MS/TP transport, singleton window
            if (MstpCapture.SingletonIsRunning) { MstpCapture.Instance?.Activate(); return; }
            var comm = await FetchTransportClientEndPoint(typeof(BacnetMstpProtocolTransport));
            if (comm == null) return;
            new MstpCapture((BacnetMstpProtocolTransport)comm.Transport).Show(this);
        }
    }
}
