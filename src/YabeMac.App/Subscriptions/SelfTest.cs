#if SUBS_SELFTEST
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using System;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    // Developer-only end-to-end test (compiled only with -p:DefineConstants=SUBS_SELFTEST).
    public partial class MainWindow
    {
        void SubsSelfTest()
        {
            string outDir = Environment.GetEnvironmentVariable("YABE_SUBS_OUT");
            if (string.IsNullOrEmpty(outDir)) return;
            if (Environment.GetEnvironmentVariable("YABE_SUBS_TREND") == "1") _ = RunTrendSelfTest(outDir); else _ = RunSubsSelfTest(outDir);
        }

        async Task RunSubsSelfTest(string outDir)
        {
            var sb = new StringBuilder();
            void L(string s) { sb.AppendLine(s); File.WriteAllText(Path.Combine(outDir, "selftest.log"), sb.ToString()); }
            try
            {
                var client = new BacnetClient(new BacnetIpUdpProtocolTransport(47811, false, false, 1472, "127.0.0.1"), 1000, 3);
                AddChannel(client, "selftest", false);
                client.WhoIs(-1, -1, new BacnetAddress(BacnetAddressTypes.IP, (Environment.GetEnvironmentVariable("YABE_SUBS_SIM") ?? "127.0.0.1:47809")));
                BACnetDevice dev = null;
                for (int i = 0; i < 50 && dev == null; i++) { await Task.Delay(100); dev = AllKnownDevices().FirstOrDefault(); }
                L("device: " + (dev == null ? "NONE" : dev.deviceId + " " + dev.BacAdr));
                if (dev == null) { Close(); return; }
                CovOpn.IsChecked = true;
                var ai1 = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1);
                var ai2 = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 2);
                var av1 = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1);
                var bv1 = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1);
                L("sub ai1 cov: " + await CreateSubscription(dev, ai1, true));
                L("sub ai2 poll300: " + await CreateSubscription(dev, ai2, true, 300));
                L("sub av1 cov: " + await CreateSubscription(dev, av1, false, 0));
                L("sub bv1 file-style P cov: " + await CreateSubscription(dev, bv1, true, 0));
                await SubscribeFromSetupFile(Path.Combine(outDir, "test.cov"));
                await Task.Delay(9000);
                DumpRows(L);
                Dispatcher.UIThread.Post(() => { });
                // switch ai1 COV->polling, ai2 polling->COV
                SubscriptionGrid.SelectedItems.Clear();
                SubscriptionGrid.SelectedItems.Add(_subRows.First(r => r.ObjectIdText == "AI:1"));
                ChangePollingType(false);
                await Task.Delay(300);
                SubscriptionGrid.SelectedItems.Clear();
                SubscriptionGrid.SelectedItems.Add(_subRows.First(r => r.ObjectIdText == "AI:2"));
                ChangePollingType(true);
                await Task.Delay(6000);
                L("--- after switching (AI1 poll, AI2 cov) ---");
                DumpRows(L);
                // pause: points must stop
                int before = _subRows.First(r => r.ObjectIdText == "AI:1").Series.Points.Count;
                TogglePlotter(false);
                await Task.Delay(4000);
                int after = _subRows.First(r => r.ObjectIdText == "AI:1").Series.Points.Count;
                L($"pause: points {before} -> {after}");
                TogglePlotter(true);
                await Task.Delay(3000);
                CovGraph.SavePng(Path.Combine(outDir, "plot_live.png"));
                var rtb = new RenderTargetBitmap(new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height), new Avalonia.Vector(96, 96));
                rtb.Render(this); rtb.Save(Path.Combine(outDir, "window.png"));
                // export setup
                var export = new StringBuilder();
                foreach (var r in _subRows.Where(r => r.Sub != null)) export.AppendLine((r.WithGraph ? "P;" : "D;") + r.Sub.device.deviceId + ";" + r.Sub.object_id + ";" + r.PeriodForExport);
                L("export:\n" + export);
                // trend review: no trendlog in sim -> check the error path doesn't crash
                L("columns: " + SaveGridColumns(SubscriptionGrid));
                // delete a row
                SubscriptionGrid.SelectedItems.Clear();
                SubscriptionGrid.SelectedItems.Add(_subRows.First(r => r.ObjectIdText == "AV:1"));
                OnSubscriptionGridKeyDown(null, new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Delete });
                L("rows after delete: " + _subRows.Count + " series: " + CovGraph.Series.Count);
                // event row hook
                var ev = new BacnetEventNotificationData { initiatingObjectIdentifier = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, 599), eventObjectIdentifier = ai1, fromState = BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_NORMAL, toState = BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_OFFNORMAL, notifyType = BacnetEventNotificationData.BacnetNotifyTypes.NOTIFY_ALARM, timeStamp = new BacnetGenericTime { Tag = BacnetTimestampTags.TIME_STAMP_DATETIME, Time = DateTime.Now } };
                AddEventRow(dev, ev);
                await Task.Delay(1500);
                DumpRows(L);
                L("DONE"); L("LOG:\n" + LogText.Text);
            }
            catch (Exception ex) { L("EXCEPTION " + ex); }
            Close();
        }

        async Task RunTrendSelfTest(string outDir)
        {
            var sb = new StringBuilder();
            void L(string s) { sb.AppendLine(s); File.WriteAllText(Path.Combine(outDir, "trend.log"), sb.ToString()); }
            try
            {
                // fake device 777 with a TrendLog:1 of 150 real records, served by a second client on port 47812
                System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Path.Combine(outDir, "trace.txt")) ); System.Diagnostics.Trace.AutoFlush = true;
                var tl = new BacnetObjectId(BacnetObjectTypes.OBJECT_TRENDLOG, 1);
                var srv = new BacnetClient(new BacnetIpUdpProtocolTransport(47812, false, false, 1472, "127.0.0.1"), 1000, 3);
                var t0 = DateTime.Now.AddHours(-3);
                var recs = Enumerable.Range(0, 150).Select(i => new BacnetLogRecord(BacnetTrendLogValueType.TL_TYPE_REAL, (float)(20 + 5 * Math.Sin(i / 10.0)), t0.AddMinutes(i), 0)).ToList();
                srv.OnReadPropertyRequest += (snd, adr, id, oid, prop, ms) =>
                {
                    L("srv RP " + prop.propertyIdentifier);
                    try {
                    var seg = snd.GetSegmentBuffer(ms);
                    if (prop.propertyIdentifier == (uint)BacnetPropertyIds.PROP_RECORD_COUNT)
                        snd.ReadPropertyResponse(adr, id, seg, oid, prop, new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, (uint)recs.Count) });
                    else if (prop.propertyIdentifier == (uint)BacnetPropertyIds.PROP_OBJECT_NAME)
                        snd.ReadPropertyResponse(adr, id, seg, oid, prop, new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, "Fake Trend") });
                    else snd.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, id, BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY);
                    } catch (Exception ex) { L("srv ex " + ex); }
                };
                srv.OnReadRange += (snd, adr, id, oid, prop, rt, pos, time, count, ms) =>
                {
                    var seg = snd.GetSegmentBuffer(ms);
                    var b = new System.IO.BACnet.Serialize.EncodeBuffer();
                    int first = (int)pos - 1, n = Math.Min(count, recs.Count - first);
                    for (int i = 0; i < n; i++) System.IO.BACnet.Serialize.Services.EncodeLogRecord(b, recs[first + i]);
                    snd.ReadRangeResponse(adr, id, seg, oid, prop, BacnetResultFlags.NONE, (uint)n, b.ToArray(), rt, pos);
                };
                if (Environment.GetEnvironmentVariable("YABE_SUBS_TRENDSRV") != "ext") srv.Start();
                var client = new BacnetClient(new BacnetIpUdpProtocolTransport(47811, false, false, 1472, "127.0.0.1"), 1000, 3);
                AddChannel(client, "trendtest", false);
                var dev0 = new BACnetDevice(client, new BacnetAddress(BacnetAddressTypes.IP, "127.0.0.1:47812"), 777);
                BACnetDevice dev = AddDevice(client, dev0.BacAdr, 777).Device;
                { bool ok = dev.ReadPropertyRequest(tl, BacnetPropertyIds.PROP_RECORD_COUNT, out var vv); L("direct RP: " + ok + " " + (vv != null && vv.Count > 0 ? vv[0].Value.ToString() : "-")); }
                var on = new ObjectNode { Device = dev, ObjectId = tl, Title = "TL" };
                // review mode
                ChangeGraphType(false);
                await DropTrendLogs(new System.Collections.Generic.List<ObjectNode> { on });
                await Task.Delay(3000);
                L($"review rows: {_trendRows.Count}; " + string.Join(",", _trendRows.Select(r => $"{r.Name}/{r.Records}/{r.VisibleRecords}")));
                OnRefreshTrendReview(null, null);
                await Task.Delay(3000);
                L($"after refresh rows: {_trendRows.Count}; series {TrendReviewGraph.Series.Count}; pts {TrendReviewGraph.Series.FirstOrDefault()?.Points.Count}");
                var rtb = new RenderTargetBitmap(new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height), new Avalonia.Vector(96, 96));
                rtb.Render(this); rtb.Save(Path.Combine(outDir, "window_trend.png"));
                // trend log display form
                var win = new TrendLogDisplay(dev, tl);
                _ = win.ShowDialog(this);
                await Task.Delay(4000);
                var rtb2 = new RenderTargetBitmap(new Avalonia.PixelSize((int)win.Bounds.Width, (int)win.Bounds.Height), new Avalonia.Vector(96, 96));
                rtb2.Render(win); rtb2.Save(Path.Combine(outDir, "trendlogdisplay.png"));
                win.Close();
                L("LOG:" + LogText.Text); L("DONE");
            }
            catch (Exception ex) { L("EXCEPTION " + ex); }
            Close();
        }

        void DumpRows(Action<string> L)
        {
            foreach (var r in _subRows)
                L($"{r.DeviceText} | {r.ObjectIdText} | {r.Name} | {r.Value} | {r.Time} | {r.Status} | cov={r.Sub?.is_COV_subscription} per={r.Sub?.Periode} pts={r.Series?.Points.Count}");
        }
    }
}
#endif
