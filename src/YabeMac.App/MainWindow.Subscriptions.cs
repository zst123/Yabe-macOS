using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yabe;
using YabeMac.Plot;

namespace YabeMac
{
    // Area "Subscriptions": COV / polling / live plot / event log.
    // Port of the subscription code of MainDialog.cs and MainDialog.Menus.cs.
    public partial class MainWindow
    {
        const int MIN_POLL_PERIOD = 100;      // ms
        const int MAX_POLL_PERIOD = 120000;   // ms
        const string SUBSCRIPTION_VIEW_TAB_TITLE = "Subscriptions, Periodic Polling, Events/Alarms";
        const string TREND_REVIEW_LIST_TAB_TITLE = "Trendlog Objects for Review";
        const string YabeObjectsFormat = "YabeObjects";

        static readonly Color[] GraphColor = { Colors.Red, Colors.Blue, Colors.Green, Colors.Violet, Colors.Chocolate, Colors.Orange };

        readonly Dictionary<string, SubscriptionRow> _subscriptionList = new Dictionary<string, SubscriptionRow>();   // sub_key -> row (m_subscription_list)
        readonly ObservableCollection<SubscriptionRow> _subRows = new ObservableCollection<SubscriptionRow>();
        uint _nextSubscriptionId = 0;
        bool _plotterRunning = true, _plotterLive = true, _plotterRunningBackup = true;
        readonly ManualResetEvent _plotterPause = new ManualResetEvent(true);
        readonly Random _rand = new Random();
        bool _subsInit;
        bool _syncSelection;
        Timer _renewTimer;
        int _renewBusy;
        double _covGraphZoomDiff = double.NaN;

        // ------------------------------------------------------------------------------------------
        // Initialisation (Loaded of the subscription pane)
        // ------------------------------------------------------------------------------------------
        void OnSubscriptionsLoaded(object s, RoutedEventArgs e)
        {
            if (_subsInit) return;
            _subsInit = true;
            SubscriptionGrid.ItemsSource = _subRows;
            TrendReviewGrid.ItemsSource = _trendRows;

            PollRate.Minimum = MIN_POLL_PERIOD; PollRate.Maximum = MAX_POLL_PERIOD;
            PollRate.Value = Math.Max(MIN_POLL_PERIOD, Math.Min(AppSettings.Default.Subscriptions_ReplacementPollingPeriod, MAX_POLL_PERIOD));
            PollRate.IsEnabled = AppSettings.Default.UsePollingByDefault;
            CovOpn.IsChecked = !AppSettings.Default.UsePollingByDefault;
            PollOpn.IsChecked = AppSettings.Default.UsePollingByDefault;
            PollRate.ValueChanged += (_, a) =>
            {
                if (a.NewValue == null) return;
                AppSettings.Default.Subscriptions_ReplacementPollingPeriod = (uint)Math.Max(MIN_POLL_PERIOD, Math.Min((uint)a.NewValue.Value, MAX_POLL_PERIOD));
            };
            PollOpn.IsCheckedChanged += (_, __) =>
            {
                bool poll = PollOpn.IsChecked == true;
                PollRate.IsEnabled = poll && _plotterLive;
                AppSettings.Default.UsePollingByDefault = poll;
            };
            AlwaysScroll.IsCheckedChanged += (_, __) => SetCovGraphZoomDiff();

            SetupPlots();
            SetupSubscriptionInput();
            RestoreGridColumns(SubscriptionGrid, AppSettings.Default.GUI_SubscriptionColumns);
            RestoreGridColumns(TrendReviewGrid, AppSettings.Default.GUI_TrendReviewColumns);

            ChangeGraphType(true);
            TogglePlotter(true);

            // renew COV subscriptions at half of their lifetime
            int lifetime = (int)AppSettings.Default.Subscriptions_Lifetime;
            if (lifetime > 1)
            {
                int ms = (lifetime / 2) * 1000;
                _renewTimer = new Timer(_ => SubscriptionRenewTick(), null, ms, ms);
            }

            Closing += (_, __) =>
            {
                try
                {
                    AppSettings.Default.GUI_SubscriptionColumns = SaveGridColumns(SubscriptionGrid);
                    AppSettings.Default.GUI_TrendReviewColumns = SaveGridColumns(TrendReviewGrid);
                    StopAlarmLog();
                    _renewTimer?.Dispose();
                    AppSettings.Default.Save();
                }
                catch { }
            };
#if SUBS_SELFTEST
            SubsSelfTest();
#endif
        }

        void SetupPlots()
        {
            foreach (var g in new[] { CovGraph, TrendReviewGraph })
            {
                g.TimeFormat = AppSettings.Default.COVTimeFormater;
                g.ShowLegend = true;
            }
            TrendReviewGraph.GradientBackground = true;
            CovGraph.ZoomChanged += SetCovGraphZoomDiff;
            CovGraph.ContextMenu = BuildPlotMenu(CovGraph, SaveCovGraphCsv);
            TrendReviewGraph.ContextMenu = BuildPlotMenu(TrendReviewGraph, SaveTrendReviewCsv);
        }

        ContextMenu BuildPlotMenu(PlotControl plot, Func<Task> saveCsv)
        {
            var unzoom = new MenuItem { Header = "Un-Zoom" };
            unzoom.Click += (_, __) => plot.RestoreScale();
            var img = new MenuItem { Header = "Save Image As..." };
            img.Click += async (_, __) => await SavePlotImage(plot);
            var csv = new MenuItem { Header = "Save all to CSV" };
            csv.Click += async (_, __) => await saveCsv();
            return new ContextMenu { Items = { unzoom, img, csv } };
        }

        async Task SavePlotImage(PlotControl plot)
        {
            try
            {
                var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Image As", DefaultExtension = "png", SuggestedFileName = "plot.png",
                    FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } }
                });
                var path = f?.TryGetLocalPath();
                if (path == null) return;
                plot.SavePng(path);
            }
            catch (Exception ex) { Log("Save image failed: " + ex.Message); }
        }

        void SetCovGraphZoomDiff() { _covGraphZoomDiff = CovGraph.XMax - DateTime.Now.ToOADate(); }

        void UpdateHints()
        {
            DropHint1.IsVisible = _plotterLive ? _subRows.Count == 0 : _trendRows.Count == 0;
            DropHint2.IsVisible = _plotterLive ? CovGraph.Series.Count == 0 : TrendReviewGraph.Series.Count == 0;
        }

        // ------------------------------------------------------------------------------------------
        // Grid columns persistence (GUI_SubscriptionColumns / GUI_TrendReviewColumns: "displayIndex;width;...")
        // ------------------------------------------------------------------------------------------
        static string SaveGridColumns(DataGrid g)
        {
            var sb = new StringBuilder();
            foreach (var c in g.Columns)
            {
                double w = c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth;
                if (w <= 0) w = c.ActualWidth > 0 ? c.ActualWidth : 60;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(c.DisplayIndex).Append(';').Append((int)w);
            }
            return sb.ToString();
        }

        static void RestoreGridColumns(DataGrid g, string setting)
        {
            try
            {
                if (string.IsNullOrEmpty(setting)) return;
                var p = setting.Split(';');
                if (p.Length != g.Columns.Count * 2) return;
                var order = new List<(int disp, DataGridColumn col)>();
                for (int i = 0; i < g.Columns.Count; i++)
                {
                    int w = Convert.ToInt32(p[i * 2 + 1]);
                    if (w > 0) g.Columns[i].Width = new DataGridLength(w);
                    order.Add((Convert.ToInt32(p[i * 2]), g.Columns[i]));
                }
                foreach (var (disp, col) in order.OrderBy(o => o.disp))
                    try { col.DisplayIndex = Math.Min(disp, g.Columns.Count - 1); } catch { }
            }
            catch { }
        }

        // ------------------------------------------------------------------------------------------
        // Drag & drop (objects from the address space, *.cov setup files)
        // ------------------------------------------------------------------------------------------
        Point? _subDragStart;
        bool _subDragging;
        List<ObjectNode> _subDragObjects;

        void SetupSubscriptionInput()
        {
            foreach (Control c in new Control[] { SubscriptionGrid, TrendReviewGrid, PlotHost })
            {
                DragDrop.SetAllowDrop(c, true);
                c.AddHandler(DragDrop.DragOverEvent, OnSubsDragOver);
                c.AddHandler(DragDrop.DropEvent, OnSubsDrop);
            }
            ObjectTree.AddHandler(PointerPressedEvent, OnObjTreePressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            ObjectTree.AddHandler(PointerMovedEvent, OnObjTreeMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            ObjectTree.AddHandler(PointerReleasedEvent, (_, __) => _subDragStart = null, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            ObjectTree.SelectionChanged += (_, __) =>
            {
                if (_syncSelection) return;
                _syncSelection = true;
                try { if (ObjectTree.SelectedItem != null) { SubscriptionGrid.SelectedItems.Clear(); TrendReviewGrid.SelectedItems.Clear(); } }
                finally { _syncSelection = false; }
            };
            SubscriptionGrid.SelectionChanged += OnSubscriptionSelectionChanged;
            TrendReviewGrid.SelectionChanged += OnTrendReviewSelectionChanged;
            SubscriptionGrid.AddHandler(KeyDownEvent, OnSubscriptionGridKeyDown, RoutingStrategies.Tunnel);
            TrendReviewGrid.AddHandler(KeyDownEvent, OnTrendReviewKeyDown, RoutingStrategies.Tunnel);
            EventSpaceMenu.Opening += (_, __) => UpdateEventSpaceMenu();
            // incoming event notifications (Alarms area) are listed in the same grid as the subscriptions
            EventNotified += (snd, adr, ev) => AddEventRow(new BACnetDevice(snd, adr, ev.initiatingObjectIdentifier.instance), ev);
        }

        void OnObjTreePressed(object s, PointerPressedEventArgs e)
        {
            _subDragStart = e.GetCurrentPoint(ObjectTree).Properties.IsLeftButtonPressed ? e.GetPosition(ObjectTree) : (Point?)null;
        }

        async void OnObjTreeMoved(object s, PointerEventArgs e)
        {
            if (_subDragStart == null || _subDragging) return;
            if (!e.GetCurrentPoint(ObjectTree).Properties.IsLeftButtonPressed) { _subDragStart = null; return; }
            var d = e.GetPosition(ObjectTree) - _subDragStart.Value;
            if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;
            _subDragStart = null;
            var nodes = ObjectTree.SelectedItems.OfType<ObjectNode>().Where(n => n.ObjectId != null).ToList();
            if (nodes.Count == 0) return;
            _subDragObjects = nodes; _subDragging = true;
            try
            {
                var data = new DataObject();
                data.Set(YabeObjectsFormat, "1");
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            catch (Exception ex) { Log("Drag failed: " + ex.Message); }
            finally { _subDragging = false; _subDragObjects = null; }
        }

        void OnSubsDragOver(object s, DragEventArgs e)
        {
            bool ok = e.Data.Contains(YabeObjectsFormat) || e.Data.Contains(DataFormats.Files);
            e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        async void OnSubsDrop(object s, DragEventArgs e)
        {
            e.Handled = true;
            try
            {
                if (e.Data.Contains(YabeObjectsFormat) && _subDragObjects != null)
                {
                    var nodes = _subDragObjects.ToList();
                    if (!_plotterLive) { await DropTrendLogs(nodes); return; }
                    bool withGraph = ReferenceEquals(s, PlotHost);
                    // objects are dropped in a deterministic order
                    foreach (var g in nodes.Where(n => n.Device != null && n.ObjectId != null).GroupBy(n => n.Device))
                        foreach (var id in g.Select(n => n.ObjectId.Value).OrderBy(i => i))
                            await CreateSubscription(g.Key, id, withGraph);
                }
                else if (e.Data.Contains(DataFormats.Files) && _plotterLive)
                {
                    var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
                    if (files == null || files.Count != 1) return;
                    await SubscribeFromSetupFile(files[0]);
                }
            }
            catch (Exception ex) { Log("Drop failed: " + ex.Message); }
        }

        /// <summary>All known devices of all channels / folders of the device tree.</summary>
        List<BACnetDevice> AllKnownDevices()
        {
            var l = new List<BACnetDevice>();
            void Walk(IEnumerable<DeviceNode> nodes) { foreach (var n in nodes) { if (n.Device != null) l.Add(n.Device); Walk(n.Children); } }
            Walk(_deviceRoots);
            return l;
        }

        /// <summary>
        /// Reads a setup file ("P|D;deviceId;OBJECT:instance[;pollPeriod]", '#' comments) such as
        /// Common Files/AdvertiseSample.cov and subscribes to every listed object of a known device.
        /// </summary>
        public async Task SubscribeFromSetupFile(string path)
        {
            try
            {
                var devices = AllKnownDevices();
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var description = line.Split(';');
                    if (description.Length != 3 && description.Length != 4) continue;
                    try
                    {
                        uint deviceId = Convert.ToUInt32(description[1]);
                        string objectIdString = description[2].Trim();
                        if (!objectIdString.StartsWith("OBJECT_")) objectIdString = "OBJECT_" + objectIdString;
                        var objectId = BacnetObjectId.Parse(objectIdString);
                        var dev = devices.FirstOrDefault(d => d.deviceId == deviceId);
                        if (dev == null) continue;       // device should be previously discovered or the line is ignored
                        if (description.Length == 4) await CreateSubscription(dev, objectId, description[0] == "P", int.Parse(description[3]));
                        else await CreateSubscription(dev, objectId, description[0] == "P");
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log("Setup file error: " + ex.Message); }
        }

        // ------------------------------------------------------------------------------------------
        // Subscribe (context menu of the address space)
        // ------------------------------------------------------------------------------------------
        async void OnSubscribe(object s, RoutedEventArgs e)
        {
            var device = SelectedDevice;
            if (device == null) return;
            var objs = ObjectTree.SelectedItems.OfType<ObjectNode>().Where(n => n.ObjectId != null).Select(n => n.ObjectId.Value).ToList();
            if (objs.Count == 0)
            {
                await Dialogs.Message("The marked object is not an object", "Not an object");
                return;
            }
            // advise all selected nodes, stop at the first failure
            foreach (var id in objs)
                if (!await CreateSubscription(device, id, false)) return;
        }

        // ------------------------------------------------------------------------------------------
        // Creation of a subscription
        // ------------------------------------------------------------------------------------------
        public async Task<bool> CreateSubscription(BACnetDevice device, BacnetObjectId object_id, bool WithGraph, int pollPeriod = -1)
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            try
            {
                BacnetAddress adr = device.BacAdr;
                var info = await Task.Run(() =>
                {
                    uint dev_id = device.deviceId;
                    if (dev_id >= System.IO.BACnet.Serialize.ASN1.BACNET_MAX_INSTANCE) dev_id = FetchDeviceId(device);
                    string name = device.ReadObjectName(object_id);
                    string descr = "";
                    if (AppSettings.Default.ShowDescriptionWhenUseful)
                        try { if (device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_DESCRIPTION, out var values) && values != null && values.Count > 0) descr = values[0].Value.ToString(); } catch { }
                    return (dev_id, name, descr);
                });

                string curveToolTip = info.name;
                if (info.descr != "") curveToolTip += Environment.NewLine + info.descr;

                _nextSubscriptionId++;
                uint subId = _nextSubscriptionId;
                string sub_key = adr.ToString() + ":" + info.dev_id + ":" + subId;
                var sub = new Subscription(device, object_id, sub_key, subId, pollPeriod);

                // If pollPeriod is <0, decide NOW between polling and COV from the option buttons. If it is >=0 the
                // subscription comes from a setup file: the period (0 == COV) overrides the option buttons.
                bool useCov = pollPeriod < 0 ? CovOpn.IsChecked == true : pollPeriod == 0;

                var color = GraphColor[CovGraph.Series.Count % GraphColor.Length];
                var series = new PlotSeries
                {
                    Name = info.name ?? "", Tooltip = curveToolTip, Color = color, Visible = WithGraph,
                    Symbol = AppSettings.Default.GraphDotStyle, MaxPoints = 10000
                };
                var row = new SubscriptionRow
                {
                    DeviceText = info.dev_id.ToString(),
                    ObjectIdText = BACnetDevice.ShortenObjectId(object_id.ToString().Substring(7)),
                    Name = info.name, Description = info.descr, Status = "Not started",
                    Sub = sub, WithGraph = WithGraph, Series = series, GraphBrush = new SolidColorBrush(color),
                };
                row.SetShowSilently(WithGraph);
                row.ShowChanged = r => { r.WithGraph = r.Show; CovGraph.InvalidateVisual(); };
                lock (_subscriptionList) _subscriptionList.Add(sub_key, row);
                _subRows.Add(row);
                CovGraph.Series.Add(series);
                UpdateHints();
                CovGraph.InvalidateVisual();

                // Now either subscribe to the data point, or set up a polling thread.
                bool SubscribeOK = false;
                if (useCov)
                {
                    SubscribeOK = await Task.Run(() =>
                    {
                        try { return device.SubscribeCOVRequest(object_id, subId, false, AppSettings.Default.Subscriptions_IssueConfirmedNotifies, AppSettings.Default.Subscriptions_Lifetime); }
                        catch (Exception ex) { Log($"The COV subscription request generated an error: {ex.GetType().Name} - {ex.Message}"); return false; }
                    });
                    if (SubscribeOK) sub.IsActive = true;
                    else Log($"Failed to subscribe to COV for {info.name}. Point will be polled instead.");
                }

                if (!SubscribeOK)   // failure or polling requested: launch periodic acquisition
                {
                    sub.is_COV_subscription = false;
                    sub.Periode = pollPeriod > 0 ? pollPeriod : (int)(PollRate.Value ?? 1000);
                    StartPolling(sub);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("Subscription failed: " + ex.Message);
                return false;
            }
            finally { Cursor = null; }
        }

        static uint FetchDeviceId(BACnetDevice device)
        {
            if (device.ReadPropertyRequest(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, System.IO.BACnet.Serialize.ASN1.BACNET_MAX_INSTANCE), BacnetPropertyIds.PROP_OBJECT_IDENTIFIER, out var value))
                if (value != null && value.Count > 0 && value[0].Value is BacnetObjectId oid) return oid.instance;
            return 0xFFFFFFFF;
        }

        // ------------------------------------------------------------------------------------------
        // Polling (alternative to COV): PROP_PRESENT_VALUE + PROP_STATUS_FLAGS every Periode ms
        // ------------------------------------------------------------------------------------------
        void StartPolling(Subscription sub)
        {
            int gen = Interlocked.Increment(ref sub.PollGeneration);
            var th = new Thread(() => ReadPropertySubscriptionPollingInsteadOfCOV(sub, gen)) { IsBackground = true, Name = "Yabe polling" };
            th.Start();
        }

        void ReadPropertySubscriptionPollingInsteadOfCOV(Subscription sub, int generation)
        {
            int errorCount = 0;
            bool wasPaused = !_plotterRunning;
            bool firstIteration = true;
            SubscriptionRow statusRow = null;
            lock (_subscriptionList) { _subscriptionList.TryGetValue(sub.sub_key, out statusRow); }

            var propertiesToPoll = new[]
            {
                new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_PRESENT_VALUE, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL),
                new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_STATUS_FLAGS, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL)
            };

            for (; ; )
            {
                if (!firstIteration)
                {
                    int period; lock (sub) period = sub.Periode;
                    Thread.Sleep(Math.Max(Math.Min(MAX_POLL_PERIOD, period), MIN_POLL_PERIOD));
                }
                else firstIteration = false;

                if (!_plotterPause.WaitOne(0)) { wasPaused = true; _plotterPause.WaitOne(); }
                if (wasPaused) Thread.Sleep(Math.Max(MIN_POLL_PERIOD, _rand.Next(0, 250)));

                if (sub == null || sub.device == null) break;
                if (generation != sub.PollGeneration) return;       // replaced by a newer polling thread
                lock (_subscriptionList) { if (!_subscriptionList.ContainsKey(sub.sub_key)) { statusRow = null; break; } }

                // Maybe the polling point has been changed to a COV point using the context menu.
                if (sub.is_COV_subscription) return;

                try
                {
                    bool ok = sub.device.ReadPropertyMultipleRequest(sub.object_id, propertiesToPoll, out var multi_value_list, sub.device.ReadMultipleAccepted == false);
                    if (ok)
                    {
                        sub.IsActive = true;
                        bool still; lock (_subscriptionList) still = _subscriptionList.ContainsKey(sub.sub_key);
                        if (!still) { statusRow = null; break; }
                        OnCOVNotification(sub.device.channel, sub.device.BacAdr, 0, sub.subscribe_id,
                            new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, sub.device.deviceId), sub.object_id, 0, false,
                            multi_value_list[0].values, BacnetMaxSegments.MAX_SEG0);
                        errorCount = 0;
                    }
                    else errorCount++;
                }
                catch { errorCount++; }

                if (errorCount >= 2)
                {
                    Log($"The Notify function (while polling device {sub.device.deviceId}, object {sub.object_id}) failed");
                    break;
                }
            }

            sub.IsActive = false;
            if (statusRow != null) Dispatcher.UIThread.Post(() => statusRow.Status = "Polling stopped");
        }

        // ------------------------------------------------------------------------------------------
        // COV notification (also fed by the polling threads)
        // ------------------------------------------------------------------------------------------
        void OnCOVNotification(BacnetClient sender, BacnetAddress adr, byte invoke_id, uint subscriberProcessIdentifier, BacnetObjectId initiatingDeviceIdentifier, BacnetObjectId monitoredObjectIdentifier, uint timeRemaining, bool need_confirm, ICollection<BacnetPropertyValue> values, BacnetMaxSegments max_segments)
        {
            string sub_key = adr.ToString() + ":" + initiatingDeviceIdentifier.instance + ":" + subscriberProcessIdentifier;
            var copy = values?.ToList() ?? new List<BacnetPropertyValue>();
            Dispatcher.UIThread.Post(() => ApplyCovValues(sub_key, monitoredObjectIdentifier, copy));
            if (need_confirm) sender.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_COV_NOTIFICATION, invoke_id);
        }

        static string ConvertToText(IList<BacnetValue> val)
        {
            if (val == null) return "[null]";
            if (val.Count == 0) return "";
            if (val.Count == 1)
            {
                if (val[0].Value is double || val[0].Value is float)
                {
                    // auto adjustable digit precision
                    double V = Convert.ToDouble(val[0].Value);
                    string ValStr = "0";
                    if (V != 0)
                    {
                        int resolution = (int)Math.Max(0, Math.Ceiling(4 - Math.Log10(Math.Abs(V))));
                        ValStr = Math.Round(V, Math.Min(resolution, 15)).ToString();
                    }
                    return ValStr;
                }
                return val[0].Value?.ToString() ?? "";
            }
            string ret = "{";
            foreach (var value in val) ret += value.Value + ",";
            return ret.Substring(0, ret.Length - 1) + "}";
        }

        void ApplyCovValues(string sub_key, BacnetObjectId monitored, List<BacnetPropertyValue> values)
        {
            try
            {
                SubscriptionRow row;
                lock (_subscriptionList) { if (!_subscriptionList.TryGetValue(sub_key, out row)) return; }
                foreach (var value in values)
                {
                    switch ((BacnetPropertyIds)value.property.propertyIdentifier)
                    {
                        case BacnetPropertyIds.PROP_PRESENT_VALUE:
                            row.Value = ConvertToText(value.value);
                            row.Time = DateTime.Now.ToString(AppSettings.Default.COVTimeFormater);
                            if (row.Status == "Not started") row.Status = "OK";
                            if (_plotterRunning) PlotLivePoint(row, monitored);
                            break;
                        case BacnetPropertyIds.PROP_STATUS_FLAGS:
                            if (value.value != null && value.value.Count > 0 && value.value[0].Value is BacnetBitString bits)
                            {
                                var status = (BacnetStatusFlags)bits.ConvertToInt();
                                string t = "";
                                if ((status & BacnetStatusFlags.STATUS_FLAG_FAULT) == BacnetStatusFlags.STATUS_FLAG_FAULT) t += "FAULT,";
                                else if ((status & BacnetStatusFlags.STATUS_FLAG_IN_ALARM) == BacnetStatusFlags.STATUS_FLAG_IN_ALARM) t += "ALARM,";
                                else if ((status & BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE) == BacnetStatusFlags.STATUS_FLAG_OUT_OF_SERVICE) t += "OOS,";
                                else if ((status & BacnetStatusFlags.STATUS_FLAG_OVERRIDDEN) == BacnetStatusFlags.STATUS_FLAG_OVERRIDDEN) t += "OR,";
                                row.Status = t != "" ? t.Substring(0, t.Length - 1) : "OK";
                            }
                            break;
                    }
                }
                AddLogAlarmEvent(row);
            }
            catch (Exception ex) { Log("Exception in subscribed value: " + ex.Message); }
        }

        void PlotLivePoint(SubscriptionRow row, BacnetObjectId monitored)
        {
            try
            {
                double Y = bool.TryParse(row.Value, out bool b) ? (b ? 1.0 : 0.0) : Convert.ToDouble(row.Value);
                double X = DateTime.Now.ToOADate();
                var series = row.Series;
                string step = AppSettings.Default.GraphLineStep;
                if ((step == "Stepped" || (step == "Automatic" && AutomaticGraphStepDeterminer.IsGraphSteppedInAutomaticMode(monitored))) && series.HasPoints)
                    series.Add(X, series.Peek().Y);

                if (AlwaysScroll.IsChecked == true && CovGraph.IsZoomed && !CovGraph.IsDragging)
                {
                    if (double.IsNaN(_covGraphZoomDiff)) SetCovGraphZoomDiff();
                    CovGraph.ShiftX(X + _covGraphZoomDiff - CovGraph.XMax);
                }
                series.Add(X, Y);
                CovGraph.TimeFormat = AppSettings.Default.COVTimeFormater;
                CovGraph.InvalidateVisual();
            }
            catch { }
        }

        // ------------------------------------------------------------------------------------------
        // COV renewal
        // ------------------------------------------------------------------------------------------
        void SubscriptionRenewTick()
        {
            if (Interlocked.Exchange(ref _renewBusy, 1) == 1) return;
            try
            {
                List<SubscriptionRow> rows;
                lock (_subscriptionList) rows = _subscriptionList.Values.ToList();
                foreach (var row in rows)
                {
                    var sub = row.Sub;
                    if (sub == null || !sub.is_COV_subscription) continue;      // polled points need no renewal
                    try
                    {
                        if (!sub.device.channel.SubscribeCOVRequest(sub.device.BacAdr, sub.object_id, sub.subscribe_id, false, AppSettings.Default.Subscriptions_IssueConfirmedNotifies, AppSettings.Default.Subscriptions_Lifetime))
                        {
                            SetSubscriptionStatus(row, "Offline");
                            Log("Couldn't renew subscription " + sub.subscribe_id);
                            sub.IsActive = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        SetSubscriptionStatus(row, "Offline");
                        Log("Exception during renew subscription: " + ex.Message);
                        sub.IsActive = false;
                    }
                }
            }
            finally { Interlocked.Exchange(ref _renewBusy, 0); }
        }

        void SetSubscriptionStatus(SubscriptionRow row, string status)
        {
            Dispatcher.UIThread.Post(() => { row.Status = status; row.Time = DateTime.Now.ToString(AppSettings.Default.COVTimeFormater); });
        }

        // ------------------------------------------------------------------------------------------
        // Removal of subscriptions
        // ------------------------------------------------------------------------------------------
        /// <summary>Removes the subscriptions of a device and/or of a channel (called when a device/channel is removed).</summary>
        public void RemoveSubscriptions(BACnetDevice device, BacnetClient comm)
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => RemoveSubscriptions(device, comm)); return; }
            List<SubscriptionRow> del;
            lock (_subscriptionList)
                del = _subscriptionList.Values.Where(r => r.Sub != null && ((device != null && r.Sub.device == device) || (comm != null && r.Sub.device.channel == comm))).ToList();
            foreach (var r in del) { DetachSubscription(r); _subRows.Remove(r); }
            UpdateHints();
            CovGraph.InvalidateVisual();
        }

        void DetachSubscription(SubscriptionRow r)
        {
            lock (_subscriptionList) _subscriptionList.Remove(r.Sub.sub_key);
            if (r.Series != null) CovGraph.Series.Remove(r.Series);
        }

        void OnSubscriptionGridKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            foreach (var row in SubscriptionGrid.SelectedItems.OfType<SubscriptionRow>().ToList())
            {
                if (row.Sub != null)
                {
                    var sub = row.Sub;
                    bool known; lock (_subscriptionList) known = _subscriptionList.ContainsKey(sub.sub_key);
                    DetachSubscription(row);
                    if (known && sub.is_COV_subscription)
                        Task.Run(() =>
                        {
                            try { if (!sub.device.SubscribeCOVRequest(sub.object_id, sub.subscribe_id, true, false, 0)) Log("Couldn't unsubscribe, Communication Error"); }
                            catch (Exception ex) { Log("Couldn't delete subscription: " + ex.Message); }
                        });
                }
                _subRows.Remove(row);
            }
            UpdateHints();
            CovGraph.InvalidateVisual();
        }

        // ------------------------------------------------------------------------------------------
        // Selection of a subscription -> its properties
        // ------------------------------------------------------------------------------------------
        /// <summary>Object of the first selected subscription row (for ack buttons, etc.), or null.</summary>
        public (BACnetDevice device, BacnetObjectId id)? SelectedSubscriptionObject
        {
            get
            {
                var sub = SubscriptionGrid.SelectedItems.OfType<SubscriptionRow>().FirstOrDefault()?.Sub;
                return sub == null ? ((BACnetDevice, BacnetObjectId)?)null : (sub.device, sub.object_id);
            }
        }

        async void OnSubscriptionSelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (_syncSelection) return;
            var row = SubscriptionGrid.SelectedItems.OfType<SubscriptionRow>().FirstOrDefault();
            if (row == null) return;
            _syncSelection = true;
            try { ObjectTree.SelectedItem = null; ObjectTree.SelectedItems?.Clear(); }
            catch { }
            finally { _syncSelection = false; }
            var sub = row.Sub;
            if (sub == null) return;           // event/alarm line
            try
            {
                if (row.Status != "Not started")
                {
                    if (!sub.IsActive)
                    {
                        if (await Dialogs.Confirm("Try to renew the subscription ?", "Polling fail / Subscription no longer active"))
                        {
                            if (sub.Periode > 0)
                            {
                                row.Status = "Not started";
                                sub.is_COV_subscription = false;
                                StartPolling(sub);
                            }
                            else
                            {
                                bool ok = await Task.Run(() => sub.device.SubscribeCOVRequest(sub.object_id, sub.subscribe_id, false, AppSettings.Default.Subscriptions_IssueConfirmedNotifies, AppSettings.Default.Subscriptions_Lifetime));
                                if (ok) { row.Status = "OK"; sub.IsActive = true; }
                                else Log("Couldn't renew subscription");
                            }
                        }
                    }
                    else await ShowObjectProperties(sub.device, sub.object_id);
                }
            }
            catch { }
        }

        /// <summary>Shows the properties of an object (subscription / trend-review selections) via the Properties area.</summary>
        async Task ShowObjectProperties(BACnetDevice device, BacnetObjectId id)
        {
            await ShowProperties(device, id);
            _ackDevice = device;      // acknowledge against the subscription's device, not the device-tree selection
        }

        // ------------------------------------------------------------------------------------------
        // Context menu of the subscription grid: change COV / polling / rate
        // ------------------------------------------------------------------------------------------
        void UpdateEventSpaceMenu()
        {
            ChangePollingRateMenuItem.IsEnabled = ChangeToPollingMenuItem.IsEnabled = ChangeToCovMenuItem.IsEnabled = false;
            foreach (var row in SubscriptionGrid.SelectedItems.OfType<SubscriptionRow>())
            {
                var sub = row.Sub;
                if (sub == null) continue;
                if (sub.is_COV_subscription || sub.Periode <= 0) { ChangeToPollingMenuItem.IsEnabled = true; continue; }
                ChangeToCovMenuItem.IsEnabled = true;
                ChangePollingRateMenuItem.IsEnabled = true;
                if (ChangePollingRateMenuItem.IsEnabled && ChangeToPollingMenuItem.IsEnabled && ChangeToCovMenuItem.IsEnabled) break;
            }
        }

        void OnChangeToCov(object s, RoutedEventArgs e) => ChangePollingType(true);
        void OnChangeToPolling(object s, RoutedEventArgs e) => ChangePollingType(false);

        async void ChangePollingType(bool useCov)
        {
            foreach (var row in SubscriptionGrid.SelectedItems.OfType<SubscriptionRow>().ToList())
            {
                var sub = row.Sub;
                if (sub == null) continue;
                if (!useCov)
                {
                    // COV -> polling: remove the existing subscription and create a polling thread
                    if (!sub.is_COV_subscription) continue;
                    await Task.Run(() =>
                    {
                        try { if (!sub.device.SubscribeCOVRequest(sub.object_id, sub.subscribe_id, true, false, 0)) Log("Couldn't unsubscribe, Communication Error"); }
                        catch (Exception ex) { Log("Couldn't delete subscription: " + ex.Message); }
                    });
                    sub.is_COV_subscription = false;
                    sub.Periode = (int)(PollRate.Value ?? 1000);
                    StartPolling(sub);
                }
                else
                {
                    // polling -> COV: the polling thread ends by itself
                    if (sub.is_COV_subscription) continue;
                    try
                    {
                        bool ok = await Task.Run(() => sub.device.SubscribeCOVRequest(sub.object_id, sub.subscribe_id, false, AppSettings.Default.Subscriptions_IssueConfirmedNotifies, AppSettings.Default.Subscriptions_Lifetime));
                        if (ok)
                        {
                            row.Status = "OK"; sub.IsActive = true; sub.is_COV_subscription = true;
                            lock (sub) sub.Periode = 0;
                        }
                        else
                        {
                            Log("Couldn't create subscription");
                            await Dialogs.Message("Failed to create COV subscription - the subscription will stay as polling.", "Change subscription to COV");
                        }
                    }
                    catch (Exception ex) { Log("Couldn't create subscription: " + ex.Message); }
                }
            }
        }

        async void OnChangePollingRate(object s, RoutedEventArgs e)
        {
            var polled = SubscriptionGrid.SelectedItems.OfType<SubscriptionRow>().Select(r => r.Sub).Where(x => x != null && !x.is_COV_subscription && x.Periode > 0).ToList();
            if (SubscriptionGrid.SelectedItems.Count == 0) return;
            if (polled.Count == 0) { await Dialogs.Message("No polled subscriptions selected.", "Error"); return; }
            string txt = await Dialogs.Input("New polling rate (ms)", "Change polling rate of selected points", ((int)(PollRate.Value ?? 1000)).ToString());
            if (string.IsNullOrWhiteSpace(txt)) return;
            if (!int.TryParse(txt.Trim(), out int rate)) { await Dialogs.Message("Invalid number", "Change polling rate"); return; }
            rate = Math.Max(MIN_POLL_PERIOD, Math.Min(MAX_POLL_PERIOD, rate));
            foreach (var sub in polled)
            {
                if (sub.is_COV_subscription || sub.Periode <= 0) continue;
                lock (sub) sub.Periode = rate;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Plot toolbar
        // ------------------------------------------------------------------------------------------
        void TogglePlotter(bool run)
        {
            _plotterRunning = run;
            if (run) _plotterPause.Set(); else _plotterPause.Reset();
            BtnPlay.IsEnabled = !_plotterRunning && _plotterLive;
            BtnPause.IsEnabled = _plotterRunning && _plotterLive;
        }

        void ChangeGraphType(bool live)
        {
            _plotterLive = live;
            BtnLive.IsEnabled = !live; BtnHistorical.IsEnabled = live;
            BtnClearPlot.IsEnabled = live; BtnExport.IsEnabled = live;
            CovOpn.IsEnabled = live; PollOpn.IsEnabled = live; PollRate.IsEnabled = live && PollOpn.IsChecked == true;
            AlwaysScroll.IsEnabled = live;
            BtnRefreshTrend.IsEnabled = !live;
            CovGraph.IsVisible = live; TrendReviewGraph.IsVisible = !live;
            SubscriptionGrid.IsVisible = live; TrendReviewGrid.IsVisible = !live;
            if (!live)
            {
                BtnPlay.IsEnabled = false; BtnPause.IsEnabled = false; BtnClearPlot.IsEnabled = false;
                SubscriptionTab.Header = TREND_REVIEW_LIST_TAB_TITLE;
            }
            else
            {
                BtnPlay.IsEnabled = !_plotterRunning; BtnPause.IsEnabled = _plotterRunning;
                SubscriptionTab.Header = SUBSCRIPTION_VIEW_TAB_TITLE;
            }
            UpdateHints();
        }

        void OnPlotPlay(object s, RoutedEventArgs e)
        {
            if (!_plotterLive) ChangeGraphType(true);
            TogglePlotter(true);
        }
        void OnPlotPause(object s, RoutedEventArgs e) => TogglePlotter(false);
        void OnPlotLive(object s, RoutedEventArgs e) { ChangeGraphType(true); TogglePlotter(_plotterRunningBackup); }
        void OnPlotHistorical(object s, RoutedEventArgs e)
        {
            // make sure the live plotter isn't running in the background
            _plotterRunningBackup = _plotterRunning;
            TogglePlotter(false);
            ChangeGraphType(false);
        }
        void OnPlotClear(object s, RoutedEventArgs e)
        {
            lock (_subscriptionList)
                foreach (var r in _subscriptionList.Values) { try { r.Series?.Points.Clear(); } catch { } }
            CovGraph.InvalidateVisual();
        }
        void OnPlotExport(object s, RoutedEventArgs e) => ExportCovGraph();

        // Export of the subscription setup (re-droppable setup file)
        async void ExportCovGraph()
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var row in _subRows.Where(r => r.Sub != null))
            {
                sb.Append(row.WithGraph ? "P;" : "D;");
                sb.Append(row.Sub.device.deviceId + ";");
                sb.Append(row.Sub.object_id + ";");
                sb.AppendLine(row.PeriodForExport.ToString());     // period or 0 if COV
                count++;
            }
            if (count == 0) { await Dialogs.Message("No valid setup on COV graph to write to file.", "Write to file fail"); return; }

            string path = string.Empty;
            string cfg = AppSettings.Default.COV_Export_Path;
            if (!string.IsNullOrWhiteSpace(cfg))
            {
                path = (cfg.EndsWith("/") || cfg.EndsWith("\\") || Directory.Exists(cfg)) ? cfg.TrimEnd('/', '\\') : (Path.GetDirectoryName(cfg) ?? "");
                if (path.Length > 0 && !Directory.Exists(path))
                {
                    try { Directory.CreateDirectory(path); }
                    catch (Exception ex) { await Dialogs.Message($"Failed to create directory \"{path}\". {ex.GetType()} - {ex.Message}", "Write to file error"); return; }
                }
            }
            if (string.IsNullOrWhiteSpace(path)) path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var now = DateTime.Now;
            string fileName = string.Format("COV_Graph_Setup_Export_{0:0000}-{1:00}-{2:00}_{3:00}.{4:00}.{5:00}.txt", now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
            string fullPath = Path.Combine(path, fileName);
            try { File.WriteAllText(fullPath, sb.ToString()); }
            catch (Exception ex) { await Dialogs.Message($"Failed to write COV graph setup data to file \"{fullPath}\". {ex.GetType()} - {ex.Message}", "Write to file error"); return; }
            await Dialogs.Message($"Wrote COV graph setup data to file \"{fullPath}\".", "Write to file success");
        }

        static string SafeFileName(string n)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n;
        }

        async Task<string> PickFolder()
        {
            var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the folder for the CSV files" });
            return r.Count > 0 ? r[0].TryGetLocalPath() : null;
        }

        static void WriteSeriesCsv(string file, PlotSeries series)
        {
            using (var sw = new StreamWriter(file))
            {
                sw.WriteLine("DateTime;Value");
                foreach (var p in series.Points)
                    sw.WriteLine(DateTime.FromOADate(p.X).ToString("yyyy-MM-dd HH:mm:ss.fff") + ";" + p.Y.ToString());
            }
        }

        // CovGraphSaveCSV_Click
        async Task SaveCovGraphCsv()
        {
            var rows = _subRows.Where(r => r.Sub != null).ToList();
            if (rows.Count == 0) return;
            string dir = await PickFolder();
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                foreach (var r in rows)
                    WriteSeriesCsv(Path.Combine(dir, "Device" + r.DeviceText + "_" + SafeFileName(r.Name ?? r.ObjectIdText) + ".csv"), r.Series);
                await Dialogs.Message("Done for " + rows.Count + " curve(s)");
            }
            catch { await Dialogs.Message("Error"); }
        }

        // ------------------------------------------------------------------------------------------
        // Cov / Event / Alarm log file
        // ------------------------------------------------------------------------------------------
        StreamWriter _alarmWriter;
        readonly object _alarmLock = new object();

        async void OnToggleEventLog(object s, RoutedEventArgs e)
        {
            if (_alarmWriter != null) { StopAlarmLog(); return; }
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Cov/Event/Alarm Log", DefaultExtension = "csv", SuggestedFileName = "CovEventAlarmLog.csv",
                FileTypeChoices = new[] { new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } }, FilePickerFileTypes.All }
            });
            var path = f?.TryGetLocalPath();
            if (path == null) return;
            try
            {
                lock (_alarmLock)
                {
                    _alarmWriter = new StreamWriter(path);
                    _alarmWriter.WriteLine("Device;ObjectId;Name;Value;Time;Status;Description");
                }
                EventLogMenuItem.Header = "Stop saving Cov/Event/Alarm Log";
            }
            catch
            {
                _alarmWriter = null;
                await Dialogs.Message("Unable to open the file", "File error");
            }
        }

        void StopAlarmLog()
        {
            lock (_alarmLock)
            {
                if (_alarmWriter == null) return;
                try { _alarmWriter.Close(); } catch { }
                _alarmWriter = null;
            }
            EventLogMenuItem.Header = "Start saving Cov/Event/Alarm Log";
        }

        void AddLogAlarmEvent(SubscriptionRow r)
        {
            lock (_alarmLock)
            {
                if (_alarmWriter == null) return;
                _alarmWriter.WriteLine(string.Join(";", r.DeviceText, r.ObjectIdText, r.Name, r.Value, r.Time, r.Status, r.Description));
                _alarmWriter.Flush();
            }
        }

        // ------------------------------------------------------------------------------------------
        // Event / alarm lines of the grid (the original shows them in the same grid as the subscriptions).
        // Called by the Alarms area from OnEventNotify.
        // ------------------------------------------------------------------------------------------
        /// <summary>
        /// Adds or updates the event/alarm line of an event notification in the subscription grid (and writes it to the
        /// Cov/Event/Alarm log when active). Port of the grid part of the original OnEventNotify; the caller
        /// still has to send the SimpleAck when need_confirm is true. Safe to call from any thread.
        /// </summary>
        public void AddEventRow(BACnetDevice device, BacnetEventNotificationData EventData)
        {
            string Nice(BacnetEventNotificationData.BacnetEventStates st) => st.ToString().Substring(12);
            uint deviceInstance = EventData.initiatingObjectIdentifier.instance;
            var objectId = EventData.eventObjectIdentifier;
            string key = deviceInstance + ":" + objectId.type + ":" + objectId.instance;
            string value = Nice(EventData.fromState) + " to " + Nice(EventData.toState);
            string time = EventData.timeStamp.Time.ToString(AppSettings.Default.COVTimeFormater);
            string status = EventData.notifyType.ToString();
            Task.Run(() =>
            {
                string name = null, descr = "";
                try { name = device.ReadObjectName(objectId); } catch { }
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("[")) name = objectId.ToString();
                if (AppSettings.Default.ShowDescriptionWhenUseful)
                    try { if (device.ReadPropertyRequest(objectId, BacnetPropertyIds.PROP_DESCRIPTION, out var d) && d != null && d.Count > 0) descr = d[0].Value.ToString(); } catch { }
                Dispatcher.UIThread.Post(() =>
                {
                    var row = _subRows.FirstOrDefault(r => r.EventKey == key);
                    if (row == null)
                    {
                        row = new SubscriptionRow
                        {
                            EventKey = key, DeviceText = deviceInstance.ToString(), ObjectIdText = BACnetDevice.ShortenObjectId(objectId.ToString()),
                            Name = name, Value = value, Time = time, Status = status, Description = descr
                        };
                        _subRows.Add(row);
                        UpdateHints();
                    }
                    else
                    {
                        if (row.Name == objectId.ToString()) row.Name = name;
                        row.Value = value; row.Time = time; row.Status = status;
                    }
                    AddLogAlarmEvent(row);
                });
            });
        }
    }
}
