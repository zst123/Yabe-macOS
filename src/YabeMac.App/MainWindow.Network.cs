using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    public partial class MainWindow
    {
        readonly System.Collections.ObjectModel.ObservableCollection<DeviceNode> _deviceRoots = new System.Collections.ObjectModel.ObservableCollection<DeviceNode>();
        readonly System.Collections.ObjectModel.ObservableCollection<ObjectNode> _objectRoots = new System.Collections.ObjectModel.ObservableCollection<ObjectNode>();
        readonly Dictionary<BacnetClient, DeviceNode> _channelNodes = new Dictionary<BacnetClient, DeviceNode>();

        void InitNetwork()
        {
            ObjectTree.ItemsSource = _objectRoots;
            InitProperties();
            InitDeviceActions();     // device tree roots (DeviceTree.ItemsSource), object tree, highlight filters, object names database
        }

        // ---------------------------------------------------------------------------------
        // Channels
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Registers an already-constructed (not yet started) client as a channel: wires events
        /// like the original MainDialog.Menus.cs, starts it and adds it to the device tree.
        /// </summary>
        public void AddChannel(BacnetClient comm, string title, bool sendWhoIs = true)
        {
            comm.Timeout = (int)AppSettings.Default.DefaultTimeout;
            comm.MaxSegments = BacnetClient.GetSegmentsCount(AppSettings.Default.Segments_Max);
            comm.OnWhoIs += (_, __, ___, ____) => { };               // YABE ignores incoming Who-Is unless it is a device itself
            comm.OnIam += OnIam;
            comm.OnCOVNotification += OnCOVNotification;
            comm.OnEventNotify += OnEventNotify;
            comm.Start();
            Channels.Add(comm);
            var node = new DeviceNode { Title = title, Channel = comm };
            _channelNodes[comm] = node;
            _deviceRoots.Add(node);
            Log($"Channel added: {title}");
            if (sendWhoIs) { try { comm.WhoIs(); } catch (Exception ex) { Log("Who-Is failed: " + ex.Message); } }
        }

        void OnIam(BacnetClient sender, BacnetAddress adr, uint deviceId, uint maxApdu, BacnetSegmentations seg, ushort vendorId)
        {
            Dispatcher.UIThread.Post(() => AddDevice(sender, adr, deviceId, maxApdu, seg, vendorId));
        }

        // AddDevice / DeviceTitle moved to MainWindow.DeviceView.cs (router grouping, Device Class View, naming)

        void OnSendWhoIs(object s, RoutedEventArgs e)
        {
            // Mirrors the original: Who-Is on the selected channel, else on all channels.
            var chan = SelectedDeviceNode?.Channel ?? SelectedDevice?.channel;
            foreach (var c in chan != null ? new[] { chan } : Channels.ToArray())
                try { c.WhoIs(); } catch (Exception ex) { Log("Who-Is failed: " + ex.Message); }
            Log("Who-Is sent");
        }

        async void OnRemoveChannelOrDevice(object s, RoutedEventArgs e)
        {
            var node = SelectedDeviceNode;
            if (node == null) return;
            if (node.Channel != null)
            {
                if (!await Dialogs.Confirm("Remove this channel?")) return;
                RemoveSubscriptions(null, node.Channel);
                YabeServerRemove(node.Channel);
                try { node.Channel.Dispose(); } catch { }
                Channels.Remove(node.Channel);
                _channelNodes.Remove(node.Channel);
                _deviceRoots.Remove(node);
            }
            else if (node.Device != null)
            {
                RemoveSubscriptions(node.Device, null);
                DeleteTreeNodeDevice(node.Device);          // removes every clone (Device Class View)
            }
            _objectRoots.Clear();
            _propertyRows.Clear();
        }

        // ---------------------------------------------------------------------------------
        // Selection -> object tree -> properties
        // ---------------------------------------------------------------------------------

        // OnDeviceSelected / OnDeviceDoubleTapped / OnRefreshObjects / LoadObjects / ObjectTitle moved to MainWindow.DeviceView.cs / MainWindow.ObjectTree.cs

        async void OnObjectSelected(object s, SelectionChangedEventArgs e) => await ShowProperties();
        async void OnRefreshProperties(object s, RoutedEventArgs e) => await ShowProperties();

    }
}
