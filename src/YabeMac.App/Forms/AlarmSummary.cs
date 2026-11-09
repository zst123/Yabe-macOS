using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.BACnet;
using System.Linq;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>One line of the alarm tree. Root nodes carry the BacnetGetEventInformationData in Alarm.</summary>
    public class AlarmNode
    {
        public string Text { get; set; }
        public string ToolTipText { get; set; } = "";
        public BacnetGetEventInformationData? Alarm;
        public ObservableCollection<AlarmNode> Nodes { get; } = new ObservableCollection<AlarmNode>();
        public override string ToString() => Text;
    }

    /// <summary>
    /// Port of Forms/AlarmSummary.cs: active alarms of a device (GetEventInformation, falling back to GetAlarmSummary),
    /// with acknowledgement of the selected alarms. Entry point: <c>await AlarmSummary.Open(device)</c>. F4 refreshes.
    /// </summary>
    public class AlarmSummary : Window
    {
        readonly BACnetDevice device;
        IList<BacnetGetEventInformationData> Alarms = new List<BacnetGetEventInformationData>();

        readonly TreeView TAlarmList = new TreeView { SelectionMode = SelectionMode.Multiple };
        readonly TextBlock LblInfo = new TextBlock { Text = "Loading alarms, please wait ...", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        readonly Button AckBt = new Button { Content = "Ack selected alarm(s)", IsEnabled = false };
        readonly TextBox AckText = new TextBox { Text = "Ack by Yabe", MinWidth = 160, IsEnabled = false };
        readonly ObservableCollection<AlarmNode> roots = new ObservableCollection<AlarmNode>();

        public static async Task Open(BACnetDevice device)
        {
            var w = new AlarmSummary(device);
            w.Opened += async (_, __) => await w.Reload();
            await w.ShowDialog(MainWindow.Instance);
        }

        AlarmSummary(BACnetDevice device)
        {
            this.device = device;
            Title = "Active Alarms on Device Id " + device.deviceId.ToString();
            Width = 460; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;

            TAlarmList.ItemsSource = roots;
            TAlarmList.ItemTemplate = new FuncTreeDataTemplate<AlarmNode>(
                (n, _) => new TextBlock { Text = n.Text, [ToolTip.TipProperty] = string.IsNullOrEmpty(n.ToolTipText) ? null : n.ToolTipText },
                n => n.Nodes);
            TAlarmList.SelectionChanged += TAlarmList_AfterSelect;
            AckBt.Click += async (_, __) => await AckSelected();
            KeyDown += async (_, e) => { if (e.Key == Key.F4) { Alarms = new List<BacnetGetEventInformationData>(); await Reload(); } };

            var dock = new DockPanel { Margin = new Thickness(12) };
            var lbl = new TextBlock { Text = "Key F4 to refresh", Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(lbl, Dock.Top); dock.Children.Add(lbl);
            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            DockPanel.SetDock(AckText, Dock.Right); bottom.Children.Add(AckText);
            bottom.Children.Add(AckBt);
            dock.Children.Add(bottom);
            var center = new Grid();
            center.Children.Add(TAlarmList); center.Children.Add(LblInfo);
            dock.Children.Add(center);
            Content = dock;
        }

        // Get the Alarm summary. Addendum 135-2012av-1 : GetAlarmSummary deprecated, GetEventInformation first.
        async Task Reload()
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            LblInfo.IsVisible = true;
            LblInfo.Text = "Loading alarms, please wait ...";
            try
            {
                var list = new List<BacnetGetEventInformationData>();
                bool Ret = await Task.Run(() =>
                {
                    IList<BacnetGetEventInformationData> a = list;
                    bool r = device.channel.GetAlarmSummaryOrEventRequest(device.BacAdr, true, ref a);   // try GetEventInformation
                    if (!r) { list.Clear(); a = list; r = device.channel.GetAlarmSummaryOrEventRequest(device.BacAdr, false, ref a); } // try GetAlarmSummary
                    list = a.ToList();
                    return r;
                });
                if (Ret)
                {
                    Alarms = list;
                    LblInfo.IsVisible = false;
                    await FillTreeNode();
                    AckText.IsEnabled = AckBt.IsEnabled = true;
                }
                else
                    LblInfo.Text = "Service not available on this device";
            }
            finally { Cursor = Cursor.Default; }
        }

        static string TitleCase(string s) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower());
        static string GetEventStateNiceName(string name) => TitleCase(name.Substring(12).Replace('_', ' '));
        static string GetEventEnableNiceName(string name) => TitleCase(name.Substring(13).Replace('_', ' '));

        async Task FillTreeNode()
        {
            var alarms = Alarms.ToList();
            bool showDescr = AppSettings.Default.ShowDescriptionWhenUseful, idWithName = AppSettings.Default.DisplayIdWithName;

            var result = await Task.Run(() =>
            {
                var nodes = new List<AlarmNode>();
                // Only one network read request to get the object name
                int _retries = device.channel.Retries;
                device.channel.Retries = 1;
                try
                {
                    foreach (var alarm in alarms)
                    {
                        if (alarm.acknowledgedTransitions.ToString() != "111" || alarm.eventState != BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_NORMAL)
                        {
                            string nameStr = device.GetObjectName(alarm.objectIdentifier);
                            if (nameStr == null)
                            {
                                // network activity, time consuming
                                if (device.channel.ReadPropertyRequest(device.BacAdr, alarm.objectIdentifier, BacnetPropertyIds.PROP_OBJECT_NAME, out var name))
                                    nameStr = name[0].Value.ToString();
                            }

                            AlarmNode tn;
                            if (nameStr != null)
                            {
                                tn = new AlarmNode { Text = idWithName ? nameStr + " (" + TitleCase(alarm.objectIdentifier.ToString()) + ")" : nameStr, ToolTipText = alarm.objectIdentifier.ToString() };
                            }
                            else
                                tn = new AlarmNode { Text = alarm.objectIdentifier.ToString() };
                            tn.Alarm = alarm;

                            if (showDescr)
                            {
                                string Descr = "";
                                try
                                {
                                    if (device.channel.ReadPropertyRequest(device.BacAdr, alarm.objectIdentifier, BacnetPropertyIds.PROP_DESCRIPTION, out var d))
                                        Descr = d[0].Value.ToString();
                                }
                                catch { }
                                tn.Nodes.Add(new AlarmNode { Text = "Description : " + Descr });
                            }

                            tn.Nodes.Add(new AlarmNode { Text = "Alarm state : " + GetEventStateNiceName(alarm.eventState.ToString()) });

                            var tn2 = new AlarmNode { Text = "Ack Required :" };
                            bool ackrequired = false;
                            for (int i = 0; i < 3; i++)
                            {
                                if (alarm.acknowledgedTransitions.ToString()[i] == '0')
                                {
                                    var bee = (BacnetEventNotificationData.BacnetEventEnable)(1 << i);
                                    string ts = alarm.eventTimeStamps != null ? alarm.eventTimeStamps[i].Time.ToString() : "?";
                                    tn2.Nodes.Add(new AlarmNode { Text = GetEventEnableNiceName(bee.ToString()) + " since " + ts });
                                    ackrequired = true;
                                }
                            }
                            if (!ackrequired) tn2 = new AlarmNode { Text = "No Ack Required, already done" };
                            tn.Nodes.Add(tn2);
                            nodes.Add(tn);
                        }
                    }
                }
                finally { device.channel.Retries = _retries; }
                return nodes;
            });

            roots.Clear();
            foreach (var n in result) roots.Add(n);
            if (roots.Count == 0)
            {
                LblInfo.IsVisible = true;
                LblInfo.Text = "Empty event list ... all is OK";
            }
        }

        bool AcqAlarm(BacnetGetEventInformationData alarm)
        {
            bool SomeChanges = false;
            for (int i = 0; i < 3; i++) // 3 transitions maybe to be ack To_OffNormal, To_Fault, To_Normal
            {
                if (alarm.acknowledgedTransitions.ToString()[i] == '0') // Transition to be ack, 1 means ok/already done
                {
                    BacnetGenericTime bgt;

                    if (alarm.eventTimeStamps != null)
                        bgt = alarm.eventTimeStamps[i];
                    else // GetAlarmSummary was used: read the event time stamp, we do not have it
                    {
                        if (device.channel.ReadPropertyRequest(device.BacAdr, alarm.objectIdentifier, BacnetPropertyIds.PROP_EVENT_TIME_STAMPS, out var values, 0, (uint)i) == false)
                        {
                            Trace.TraceWarning("Error reading PROP_EVENT_TIME_STAMPS");
                            return false;
                        }
                        string s1 = ((BacnetValue[])(values[0].Value))[0].ToString(); // Date & 00:00:00 for Hour
                        string s2 = ((BacnetValue[])(values[0].Value))[1].ToString(); // 00:00:00 & Time
                        DateTime dt = Convert.ToDateTime(s1.Split(' ')[0] + " " + s2.Split(' ')[1]);
                        bgt = new BacnetGenericTime(dt, BacnetTimestampTags.TIME_STAMP_DATETIME);
                    }

                    var eventstate = (BacnetEventNotificationData.BacnetEventStates)(2 - i);
                    string text = null;
                    Dispatcher.UIThread.Invoke(() => text = AckText.Text);
                    if (device.channel.AlarmAcknowledgement(device.BacAdr, alarm.objectIdentifier, eventstate, text, bgt,
                                new BacnetGenericTime(DateTime.Now, BacnetTimestampTags.TIME_STAMP_DATETIME)) == true)
                    {
                        alarm.acknowledgedTransitions.SetBit((byte)i, true);
                        SomeChanges = true;
                    }
                }
            }
            return SomeChanges;
        }

        async Task AckSelected()
        {
            // go to the root element of each selected node
            var listacq = new List<AlarmNode>();
            foreach (var o in TAlarmList.SelectedItems.OfType<AlarmNode>())
            {
                var root = roots.FirstOrDefault(r => r == o || r.Nodes.Contains(o) || r.Nodes.Any(c => c.Nodes.Contains(o)));
                if (root != null && !listacq.Contains(root)) listacq.Add(root);
            }

            Cursor = new Cursor(StandardCursorType.Wait);
            try
            {
                await Task.Run(() => { foreach (var t in listacq) if (t.Alarm.HasValue) AcqAlarm(t.Alarm.Value); });
                // The original refilled from its stale list; re-query the device so the result is visible.
                Alarms = new List<BacnetGetEventInformationData>();
                await Reload();
            }
            finally { Cursor = Cursor.Default; }
        }

        // Used if the Read without retries has failed (no name known)
        async void TAlarmList_AfterSelect(object sender, SelectionChangedEventArgs e)
        {
            var tn = e.AddedItems.OfType<AlarmNode>().FirstOrDefault();
            if (tn == null) return;
            var root = roots.FirstOrDefault(r => r == tn || r.Nodes.Contains(tn) || r.Nodes.Any(c => c.Nodes.Contains(tn)));
            if (root == null || root.ToolTipText != "" || !root.Alarm.HasValue) return;
            var alarm = root.Alarm.Value;
            IList<BacnetValue> name = null;
            bool ok = await Task.Run(() => device.channel.ReadPropertyRequest(device.BacAdr, alarm.objectIdentifier, BacnetPropertyIds.PROP_OBJECT_NAME, out name));
            if (!ok || name == null || name.Count == 0) return;
            root.ToolTipText = root.Text;
            int idx = roots.IndexOf(root);
            root.Text = AppSettings.Default.DisplayIdWithName ? name[0].Value.ToString() + " (" + TitleCase(root.ToolTipText) + ")" : name[0].Value.ToString();
            if (idx >= 0) { roots.RemoveAt(idx); roots.Insert(idx, root); }
        }
    }
}
