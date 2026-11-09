using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Linq;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>
    /// Port of Forms/NotificationEditor.cs: edits Recipient_List and Priority of a Notification Class object.
    /// Entry point: <c>await NotificationEditor.Open(device, objectId)</c> (modal over the main window).
    /// </summary>
    public class NotificationEditor : Window
    {
        readonly BacnetObjectId object_id;
        readonly BACnetDevice device;

        readonly TabControl RecipientsTab = new TabControl();
        readonly TextBlock labelRecipient = new TextBlock { FontWeight = FontWeight.SemiBold };
        readonly TextBlock labelEmpty = new TextBlock { Text = "Empty list", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        readonly TextBox P_Off = new TextBox { Width = 60 }, P_Fault = new TextBox { Width = 60 }, P_Normal = new TextBox { Width = 60 };
        readonly List<RecipientTab> tabs = new List<RecipientTab>();

        /// <summary>Opens the editor (reads the object first). Equivalent of "new NotificationEditor(device, object_id).ShowDialog()".</summary>
        public static async Task Open(BACnetDevice device, BacnetObjectId object_id)
        {
            try
            {
                var ed = new NotificationEditor(device, object_id);
                await ed.LoadProperties();
                await ed.ShowDialog(MainWindow.Instance);
            }
            catch (Exception ex) { MainWindow.Instance?.Log("Error loading Notification : " + ex.Message); }
        }

        NotificationEditor(BACnetDevice device, BacnetObjectId object_id)
        {
            this.device = device;
            this.object_id = object_id;
            Title = "Notification Editor";
            Width = 520; Height = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;

            labelRecipient.Text = "Recipient List : " + object_id.ToString().Substring(7);

            // context menu of the tab control: Delete / Add New
            var del = new MenuItem { Header = "Delete" }; del.Click += (_, __) => DeleteTab();
            var add = new MenuItem { Header = "Add New" }; add.Click += (_, __) => AddNewTab();
            RecipientsTab.ContextMenu = new ContextMenu { ItemsSource = new[] { del, add } };

            var prio = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8) };
            prio.Children.Add(Lbl("To_OffNormal")); prio.Children.Add(P_Off);
            prio.Children.Add(Lbl("To_Fault")); prio.Children.Add(P_Fault);
            prio.Children.Add(Lbl("To_Normal")); prio.Children.Add(P_Normal);
            var grp = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(8, 4) };
            var grpIn = new StackPanel(); grpIn.Children.Add(new TextBlock { Text = "Priority", Margin = new Thickness(8, 2, 0, 0), FontWeight = FontWeight.SemiBold }); grpIn.Children.Add(prio);
            grp.Child = grpIn;

            var bt = new Button { Content = "Write & Read back", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8), Padding = new Thickness(16, 8) };
            bt.Click += async (_, __) => await ReadWrite();

            var dock = new DockPanel();
            var top = new StackPanel { Margin = new Thickness(8, 6, 8, 2) }; top.Children.Add(labelRecipient);
            DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
            DockPanel.SetDock(bt, Dock.Bottom); dock.Children.Add(bt);
            DockPanel.SetDock(grp, Dock.Bottom); dock.Children.Add(grp);
            var center = new Grid { Margin = new Thickness(8, 0) };
            center.Children.Add(RecipientsTab); center.Children.Add(labelEmpty);
            dock.Children.Add(center);
            Content = dock;
        }

        static TextBlock Lbl(string t) => new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center };

        async Task LoadProperties()
        {
            var props = new List<BacnetPropertyReference>
            {
                new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_RECIPIENT_LIST, ASN1.BACNET_ARRAY_ALL),
                new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_PRIORITY, ASN1.BACNET_ARRAY_ALL)
            };
            IList<BacnetReadAccessResult> PropertiesValues = null;
            bool ok = await Task.Run(() => device.ReadPropertyMultipleRequest(object_id, props, out PropertiesValues));
            if (!ok || PropertiesValues == null || PropertiesValues.Count == 0)
            {
                MainWindow.Instance?.Log("Notification Editor: ReadPropertyMultiple failed on " + object_id);
                throw new Exception("Cannot read " + object_id);
            }

            RecipientsTab.Items.Clear();
            tabs.Clear();
            labelEmpty.IsVisible = true;

            foreach (BacnetPropertyValue aProp in PropertiesValues[0].values)
            {
                if (aProp.property.propertyIdentifier == (uint)BacnetPropertyIds.PROP_PRIORITY)
                {
                    // priorities are common to all recipients
                    P_Off.Text = aProp.value[0].Value.ToString();
                    P_Fault.Text = aProp.value[1].Value.ToString();
                    P_Normal.Text = aProp.value[2].Value.ToString();
                }
                else
                {
                    // a tab for each recipient
                    for (int i = 0; i < aProp.value.Count / 7; i++)
                    {
                        var recipient = new DeviceReportingRecipient(aProp.value[i * 7], aProp.value[i * 7 + 1], aProp.value[i * 7 + 2], aProp.value[i * 7 + 3], aProp.value[i * 7 + 4], aProp.value[i * 7 + 5], aProp.value[i * 7 + 6]);
                        var t = new RecipientTab(recipient) { TabName = i.ToString() };
                        t.DeviceAddrOK += content_DeviceAddrOK;
                        tabs.Add(t);
                        RecipientsTab.Items.Add(t.Item);
                        labelEmpty.IsVisible = false;
                    }
                }
            }
        }

        async Task WriteProperties()
        {
            // Priorities
            try
            {
                var PropVal = new List<BacnetValue>
                {
                    new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, Convert.ToUInt32(P_Off.Text)),
                    new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, Convert.ToUInt32(P_Fault.Text)),
                    new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, Convert.ToUInt32(P_Normal.Text))
                };
                await Task.Run(() => device.WritePropertyRequest(object_id, BacnetPropertyIds.PROP_PRIORITY, PropVal));
            }
            catch { }
            // Recipient list
            try
            {
                var PropVal = new List<BacnetValue>();
                foreach (var r in tabs)
                {
                    if (r.TabName != "Not Set") // Entry is OK ?
                    {
                        DeviceReportingRecipient newrp;
                        if (r.adr != null) // recipient is an IP address
                            newrp = new DeviceReportingRecipient(r.WeekOfDay, r.FromTime, r.ToTime, r.adr, Convert.ToUInt16(r.ProcessId.Text), r.AckRequired.IsChecked == true, r.EventType);
                        else // recipient is a deviceId
                            newrp = new DeviceReportingRecipient(r.WeekOfDay, r.FromTime, r.ToTime, new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, r.deviceid), Convert.ToUInt16(r.ProcessId.Text), r.AckRequired.IsChecked == true, r.EventType);
                        PropVal.Add(new BacnetValue(newrp));
                    }
                }
                await Task.Run(() => device.WritePropertyRequest(object_id, BacnetPropertyIds.PROP_RECIPIENT_LIST, PropVal));
            }
            catch { }
        }

        void Renumber()
        {
            int i = 1;
            foreach (var t in tabs)
                if (t.TabName != "Not Set") { t.TabName = i.ToString(); i++; }
        }

        // Handler called on each change in the recipient address TextBox
        void content_DeviceAddrOK(RecipientTab sender, bool AddrOk)
        {
            sender.TabName = AddrOk ? "" : "Not Set";
            Renumber(); // re-numbering of each tab
        }

        void DeleteTab()
        {
            int idx = RecipientsTab.SelectedIndex;
            if (idx >= 0 && idx < tabs.Count)
            {
                tabs.RemoveAt(idx);
                RecipientsTab.Items.RemoveAt(idx);
            }
            labelEmpty.IsVisible = tabs.Count == 0;
            int i = 1;
            foreach (var t in tabs) { t.TabName = i.ToString(); i++; }
        }

        void AddNewTab()
        {
            if (tabs.Any(t => t.TabName == "Not Set")) return;
            var nt = new RecipientTab(null) { TabName = "Not Set" };
            nt.DeviceAddrOK += content_DeviceAddrOK;
            tabs.Add(nt);
            RecipientsTab.Items.Add(nt.Item);
            RecipientsTab.SelectedItem = nt.Item;
            labelEmpty.IsVisible = false;
        }

        async Task ReadWrite()
        {
            await WriteProperties();
            int idx = RecipientsTab.SelectedIndex;
            try { await LoadProperties(); } catch (Exception ex) { await Dialogs.Message(ex.Message, "Notification Editor", this); }
            try { RecipientsTab.SelectedIndex = idx; } catch { }
        }
    }

    /// <summary>Port of RecipientUserCtrl: one tab page with the content of a DeviceReportingRecipient.</summary>
    public class RecipientTab
    {
        public readonly TabItem Item = new TabItem { FontSize = 13 };   // Fluent default header size (24) is far too large
        public BacnetBitString EventType; // Updated by the related 3 CheckBox
        public BacnetBitString WeekOfDay; // Updated by the related 7 CheckBox
        public BacnetAddress adr;
        public uint deviceid;

        public readonly CheckBox AckRequired = new CheckBox { Content = "Ack Required" };
        public readonly TextBox ProcessId = new TextBox { Text = "0", Width = 80 };
        readonly TextBox Device = new TextBox { Width = 200 };
        readonly CheckBox[] days = new CheckBox[7];
        readonly CheckBox[] events = new CheckBox[3];
        readonly TimePicker fromTime = new TimePicker { UseSeconds = true, ClockIdentifier = "24HourClock", MinuteIncrement = 1 };
        readonly TimePicker toTime = new TimePicker { UseSeconds = true, ClockIdentifier = "24HourClock", MinuteIncrement = 1 };
        bool loading;

        public event Action<RecipientTab, bool> DeviceAddrOK;

        public string TabName { get => _name; set { _name = value; Item.Header = value; } }
        string _name = "";

        public DateTime FromTime => new DateTime(2000, 1, 1, (fromTime.SelectedTime ?? TimeSpan.Zero).Hours, (fromTime.SelectedTime ?? TimeSpan.Zero).Minutes, (fromTime.SelectedTime ?? TimeSpan.Zero).Seconds);
        public DateTime ToTime => new DateTime(2000, 1, 1, (toTime.SelectedTime ?? TimeSpan.Zero).Hours, (toTime.SelectedTime ?? TimeSpan.Zero).Minutes, (toTime.SelectedTime ?? TimeSpan.Zero).Seconds);

        static Border Group(string title, Control content)
        {
            var sp = new StackPanel { Spacing = 4, Margin = new Thickness(8, 2, 8, 8) };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
            sp.Children.Add(content);
            return new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 8), Child = sp };
        }

        public RecipientTab(DeviceReportingRecipient? recipient)
        {
            loading = true;
            string[] dayNames = { "Monday", "Tuesday", "Wedesnday", "Thursday", "Friday", "Saturday", "Sunday" };
            for (int i = 0; i < 7; i++)
            {
                int bit = i;
                days[i] = new CheckBox { Content = dayNames[i], MinWidth = 120 };
                days[i].IsCheckedChanged += (_, __) => { if (!loading) WeekOfDay.SetBit((byte)bit, days[bit].IsChecked == true); };
            }
            string[] evNames = { "To_OffNormal", "To_Fault", "To_Normal" };
            for (int i = 0; i < 3; i++)
            {
                int bit = i;
                events[i] = new CheckBox { Content = evNames[i], IsChecked = true, MinWidth = 100 };
                events[i].IsCheckedChanged += (_, __) => { if (!loading) EventType.SetBit((byte)bit, events[bit].IsChecked == true); };
            }

            // group 1: Ack Required / Process Id
            var g1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
            g1.Children.Add(AckRequired);
            var pid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            pid.Children.Add(new TextBlock { Text = "Process Id", VerticalAlignment = VerticalAlignment.Center }); pid.Children.Add(ProcessId);
            g1.Children.Add(pid);
            var group1 = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(8), Child = g1 };

            // EventType
            var ev = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            foreach (var c in events) ev.Children.Add(c);

            // Validity
            var val = new StackPanel { Spacing = 4 };
            var r1 = new StackPanel { Orientation = Orientation.Horizontal }; for (int i = 0; i < 4; i++) r1.Children.Add(days[i]);
            var r2 = new StackPanel { Orientation = Orientation.Horizontal }; for (int i = 4; i < 7; i++) r2.Children.Add(days[i]);
            var r3 = new WrapPanel { Orientation = Orientation.Horizontal };     // the two 3-column time pickers do not fit side by side in the tab
            var r3a = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 16, 4) };
            r3a.Children.Add(new TextBlock { Text = "fromTime", VerticalAlignment = VerticalAlignment.Center }); r3a.Children.Add(fromTime);
            var r3b = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
            r3b.Children.Add(new TextBlock { Text = "toTime", VerticalAlignment = VerticalAlignment.Center }); r3b.Children.Add(toTime);
            r3.Children.Add(r3a); r3.Children.Add(r3b);
            val.Children.Add(r1); val.Children.Add(r2); val.Children.Add(r3);

            var host = new StackPanel { Margin = new Thickness(12) };
            host.Children.Add(group1);
            host.Children.Add(Group("EventType", ev));
            host.Children.Add(Group("Validity", val));
            host.Children.Add(Group("Receiver :  deviceId or IP:Port (like 4000 or 192.168.0.1:47808)", Device));
            Item.Content = new ScrollViewer { Content = host };

            if (recipient == null)
            {
                for (byte i = 0; i < 7; i++) { WeekOfDay.SetBit(i, true); days[i].IsChecked = true; }
                for (byte i = 0; i < 3; i++) EventType.SetBit(i, true);
                fromTime.SelectedTime = TimeSpan.Zero; toTime.SelectedTime = new TimeSpan(23, 59, 59);
            }
            else
            {
                var r = recipient.Value;
                if (r.adr != null) { adr = r.adr; Device.Text = adr.ToString(); }
                else { deviceid = r.Id.instance; Device.Text = deviceid.ToString(); }

                AckRequired.IsChecked = r.Ack_Required;
                ProcessId.Text = r.processIdentifier.ToString();

                WeekOfDay = r.WeekofDay;
                for (int i = 0; i < 7; i++) days[i].IsChecked = (WeekOfDay.value[0] & (1 << i)) != 0;
                EventType = r.evenType;
                for (int i = 0; i < 3; i++) events[i].IsChecked = (EventType.value[0] & (1 << i)) != 0;

                toTime.SelectedTime = new TimeSpan(r.toTime.Hour, r.toTime.Minute, r.toTime.Second);
                fromTime.SelectedTime = new TimeSpan(r.fromTime.Hour, r.fromTime.Minute, r.fromTime.Second);
            }
            loading = false;

            // Event is hooked after the initial text so loaded tabs keep their number (as in the original, where
            // the handler was attached after construction)
            Device.TextChanged += Device_TextChanged;
        }

        void Device_TextChanged(object sender, TextChangedEventArgs e)
        {
            adr = null;
            try
            {
                deviceid = Convert.ToUInt16(Device.Text);
                DeviceAddrOK?.Invoke(this, true); // it's a deviceId
            }
            catch
            {
                try
                {
                    adr = new BacnetAddress(BacnetAddressTypes.IP, Device.Text);
                    DeviceAddrOK?.Invoke(this, true); // it's a xxx.xxx.xxx.xxx:xxx
                }
                catch
                {
                    DeviceAddrOK?.Invoke(this, false);
                }
            }
        }
    }
}
