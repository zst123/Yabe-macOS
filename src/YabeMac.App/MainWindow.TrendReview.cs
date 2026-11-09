using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yabe;
using YabeMac.Plot;

namespace YabeMac
{
    // Area "Subscriptions": historical plot of TrendLog objects (port of MainDialog.HistoricalTrendPlot.cs)
    // and the "Show trend log" dialog launcher.
    public partial class MainWindow
    {
        readonly ObservableCollection<TrendReviewRow> _trendRows = new ObservableCollection<TrendReviewRow>();
        readonly List<TrendLogObject> _trendLogsForReview = new List<TrendLogObject>();
        int _trendReviewAddsInProgress = 0;
        readonly AutoResetEvent _rereadTrendLogItemsForReviewSync = new AutoResetEvent(false);

        // ---- Show trend log (opens the TrendLogDisplay form) --------------------------------------
        async void OnShowTrendLog(object s, RoutedEventArgs e)
        {
            try
            {
                var device = SelectedObjectNode?.Device ?? SelectedDevice;
                var id = SelectedObjectId;
                if (device == null || id == null) return;
                if (id.Value.type != BacnetObjectTypes.OBJECT_TRENDLOG && id.Value.type != BacnetObjectTypes.OBJECT_TREND_LOG_MULTIPLE) return;
                await new TrendLogDisplay(device, id.Value).ShowDialog(this);
            }
            catch (Exception ex) { Log("Error loading TrendLog : " + ex.Message); }
        }

        // ---- drop of TrendLog objects on the review list / graph ---------------------------------
        async Task DropTrendLogs(List<ObjectNode> nodes)
        {
            var objs = nodes.Where(n => n.Device != null && n.ObjectId != null).Select(n => (dev: n.Device, id: n.ObjectId.Value)).OrderBy(o => o.id).ToList();
            int invalid = 0;
            for (int i = 0; i < objs.Count; i++)
            {
                if (objs[i].id.type != BacnetObjectTypes.OBJECT_TRENDLOG) { invalid++; continue; }
                var newTrend = new TrendLogObject(objs[i].dev, objs[i].id) { ColorIndex = (TrendReviewGraph.Series.Count + i) % GraphColor.Length };
                _trendLogsForReview.Add(newTrend);
                Cursor = new Cursor(StandardCursorType.Wait);
                _trendReviewAddsInProgress++;
                _ = Task.Run(() => newTrend.BeginReadingTrendLog(PlotRecordsCallback));
            }
            if (invalid > 0)
            {
                string message = invalid == objs.Count
                    ? "Error adding objects to graph: Only BACnet Trendlog objects can be added to the trend review graph."
                    : "Error adding objects to graph: Some of the objects dropped onto the trend review graph were not BACnet Trendlog objects. Only Trendlog objects can be added to the trend review graph, so these items were excluded.";
                await Dialogs.Message(message, "Adding Trendlogs for Review");
            }
        }

        string ReadTrendDescription(TrendLogObject t)
        {
            if (!AppSettings.Default.ShowDescriptionWhenUseful) return null;
            try { if (t.device.ReadPropertyRequest(t.object_id, BacnetPropertyIds.PROP_DESCRIPTION, out var v) && v != null && v.Count > 0) return v[0].Value.ToString(); } catch { }
            return "";
        }

        // Called from a worker thread when the download of a trend log is complete (or failed).
        void PlotRecordsCallback(TrendLogObject trend, bool success, string errorMessage)
        {
            string descr = success ? ReadTrendDescription(trend) : null;
            Dispatcher.UIThread.Post(async () =>
            {
                _trendReviewAddsInProgress--;
                if (_trendReviewAddsInProgress <= 0) Cursor = null;
                if (!success)
                {
                    Log("Trend log read failed: " + errorMessage);
                    await Dialogs.Message("Trend log object \"" + trend.Title + "\" - " + (errorMessage ?? "unknown error reading from the device."), "Read trend data");
                    return;
                }
                string toolTipDescription = string.IsNullOrEmpty(descr) ? string.Empty : Environment.NewLine + descr;
                var color = GraphColor[trend.ColorIndex];
                var row = new TrendReviewRow
                {
                    DeviceText = trend.device.deviceId.ToString(),
                    ObjectIdText = trend.object_id.ToString().Substring(7),
                    Name = trend.Title, Records = trend.Logsize.ToString(), VisibleRecords = trend.PlottableRecords.ToString(),
                    Description = descr ?? "", GraphBrush = new SolidColorBrush(color), Trend = trend,
                };
                trend.Row = row;
                CreateCurves(trend, trend.Title, toolTipDescription);
                row.ShowChanged = r =>
                {
                    if (r.Trend.AssociatedGraphCurves != null) foreach (var c in r.Trend.AssociatedGraphCurves) c.Visible = r.Show;
                    TrendReviewGraph.InvalidateVisual();
                };
                _trendRows.Add(row);
                row.Show = true;
                UpdateHints();
            });
        }

        void CreateCurves(TrendLogObject trend, string toolTip, string toolTipDescription)
        {
            if (trend.AssociatedGraphCurves != null)
            {
                foreach (var c in trend.AssociatedGraphCurves) TrendReviewGraph.Series.Remove(c);
                trend.AssociatedGraphCurves.Clear();
            }
            else trend.AssociatedGraphCurves = new List<PlotSeries>(trend.CurvesNumber);

            // all lines of one trendlog have the same colour (the list shows a single colour swatch)
            var color = GraphColor[trend.ColorIndex];
            bool visible = trend.Row == null || trend.Row.Show || !_trendRows.Contains(trend.Row);
            for (int i = 0; i < trend.CurvesNumber; i++)
            {
                var l = new PlotSeries
                {
                    Name = trend.Title, Color = color, Symbol = AppSettings.Default.GraphDotStyle, LineWidth = 1, Visible = visible,
                    Points = trend.Pointslists != null && i < trend.Pointslists.Length ? trend.Pointslists[i] : new List<PlotPt>()
                };
                l.Tooltip = trend.CurvesNumber > 1
                    ? toolTip + "_" + (i + 1).ToString("D" + ((int)Math.Log10(trend.CurvesNumber) + 1)) + toolTipDescription
                    : toolTip + toolTipDescription;
                trend.AssociatedGraphCurves.Add(l);
                TrendReviewGraph.Series.Add(l);
            }
            TrendReviewGraph.TimeFormat = AppSettings.Default.COVTimeFormater;
            TrendReviewGraph.InvalidateVisual();
        }

        // ---- Refresh -------------------------------------------------------------------------------
        void OnRefreshTrendReview(object s, RoutedEventArgs e)
        {
            Cursor = new Cursor(StandardCursorType.Wait);
            var list = _trendLogsForReview.ToList();
            Task.Run(() =>
            {
                foreach (var t in list)
                {
                    t.BeginReadingTrendLog(RereadItemCallback);
                    // Synchronise the reads one-by-one; the callback is always called, even on failure.
                    _rereadTrendLogItemsForReviewSync.WaitOne();
                }
                Dispatcher.UIThread.Post(() => Cursor = null);
            });
        }

        void RereadItemCallback(TrendLogObject trend, bool success, string errorMessage)
        {
            if (trend == null) { _rereadTrendLogItemsForReviewSync.Set(); return; }
            string descr = success ? ReadTrendDescription(trend) : null;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (!success)
                    {
                        await Dialogs.Message("Trend log object \"" + trend.Title + "\" - " + (errorMessage ?? "unknown error reading from the device."), "Read trend data");
                        return;
                    }
                    string toolTipDescription = string.IsNullOrEmpty(descr) ? string.Empty : Environment.NewLine + descr;
                    if (trend.Row != null) { trend.Row.Records = trend.Logsize.ToString(); trend.Row.VisibleRecords = trend.PlottableRecords.ToString(); }
                    CreateCurves(trend, trend.Title, toolTipDescription);
                }
                finally { _rereadTrendLogItemsForReviewSync.Set(); }
            });
        }

        // ---- list handling -------------------------------------------------------------------------
        void OnTrendReviewKeyDown(object s, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            foreach (var row in TrendReviewGrid.SelectedItems.OfType<TrendReviewRow>().ToList())
            {
                _trendRows.Remove(row);
                var t = row.Trend;
                if (t == null) continue;
                _trendLogsForReview.Remove(t);
                t.StopDownload = true;
                if (t.AssociatedGraphCurves != null)
                {
                    foreach (var c in t.AssociatedGraphCurves) TrendReviewGraph.Series.Remove(c);
                    t.AssociatedGraphCurves.Clear();
                }
            }
            UpdateHints();
            TrendReviewGraph.InvalidateVisual();
        }

        async void OnTrendReviewSelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (_syncSelection) return;
            var row = TrendReviewGrid.SelectedItems.OfType<TrendReviewRow>().FirstOrDefault();
            if (row?.Trend == null) return;
            _syncSelection = true;
            try { ObjectTree.SelectedItem = null; ObjectTree.SelectedItems?.Clear(); }
            catch { }
            finally { _syncSelection = false; }
            try { await ShowObjectProperties(row.Trend.device, row.Trend.object_id); } catch { }
        }

        // ---- CSV export of the stored data ---------------------------------------------------------
        async Task SaveTrendReviewCsv()
        {
            string dir = await PickFolder();
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                foreach (var row in _trendRows)
                {
                    var curves = row.Trend?.AssociatedGraphCurves;
                    if (curves == null) continue;
                    string baseName = Path.Combine(dir, "Device" + row.Trend.device.deviceId + "_" + SafeFileName(row.Trend.Title));
                    for (int j = 0; j < curves.Count; j++)
                    {
                        string name = curves.Count > 1 ? baseName + "_" + (j + 1).ToString("D" + ((int)Math.Log10(curves.Count) + 1)) + ".csv" : baseName + ".csv";
                        WriteSeriesCsv(name, curves[j]);
                    }
                }
                await Dialogs.Message("Exported " + _trendRows.Count + " curve(s)", "Export stored trendlog data");
            }
            catch (Exception ex) { await Dialogs.Message("Error - " + ex.Message, "Export stored trendlog data"); }
        }
    }
}
