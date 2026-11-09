using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using YabeMac.Plot;
using Yabe;

namespace YabeMac
{
    /// <summary>Port of Yabe.Subscription (Utils/Helpers.cs).</summary>
    public class Subscription
    {
        public readonly BACnetDevice device;
        public readonly BacnetObjectId object_id;
        public readonly string sub_key;
        public readonly uint subscribe_id;
        public bool is_COV_subscription = true;   // false if subscription is refused (fallback to polling) or polling is specified explicitly.
        public int Periode;
        public bool IsActive = false;
        public int PollGeneration;                // lets a stale polling thread notice it has been replaced
        public Subscription(BACnetDevice device, BacnetObjectId object_id, string sub_key, uint subscribe_id, int periode)
        {
            this.device = device; this.object_id = object_id; this.sub_key = sub_key; this.subscribe_id = subscribe_id; Periode = periode;
        }
    }

    /// <summary>Port of Yabe.AutomaticGraphStepDeterminer.</summary>
    public static class AutomaticGraphStepDeterminer
    {
        public static bool IsGraphSteppedInAutomaticMode(BacnetObjectId o) =>
            o.type != BacnetObjectTypes.OBJECT_ANALOG_INPUT && o.type != BacnetObjectTypes.OBJECT_ANALOG_OUTPUT && o.type != BacnetObjectTypes.OBJECT_ANALOG_VALUE;
        public static bool IsGraphSteppedInAutomaticMode(BacnetTrendLogValueType t) => t != BacnetTrendLogValueType.TL_TYPE_REAL;
    }

    /// <summary>
    /// Row of the subscription grid: either a subscription (COV/polled point, Sub != null) or an event/alarm line (Sub == null).
    /// </summary>
    public class SubscriptionRow : Observable
    {
        string _device, _objectId, _name, _value = "", _time = "", _status = "Not started", _description = "";
        bool _show;
        public string DeviceText { get => _device; set => Set(ref _device, value); }
        public string ObjectIdText { get => _objectId; set => Set(ref _objectId, value); }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Value { get => _value; set => Set(ref _value, value); }
        public string Time { get => _time; set => Set(ref _time, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public string Description { get => _description; set => Set(ref _description, value); }
        public bool Show
        {
            get => _show;
            set
            {
                if (Sub == null) { if (_show) { _show = false; Raise(); } return; }   // events/alarms can never be plotted
                if (Set(ref _show, value)) { if (Series != null) Series.Visible = value; ShowChanged?.Invoke(this); }
            }
        }
        public void SetShowSilently(bool v) { _show = v; Raise(nameof(Show)); if (Series != null) Series.Visible = v; }

        public IBrush GraphBrush { get; set; }          // "Graph Line Color" column
        public Subscription Sub;                         // null for an event/alarm row
        public string EventKey;                          // sub_key of an event row
        public bool WithGraph;
        public PlotSeries Series;
        public Action<SubscriptionRow> ShowChanged;

        /// <summary>Column [10] of the original ListView: 0 = COV, otherwise the polling period in ms.</summary>
        public int PeriodForExport => Sub == null ? -1 : (Sub.is_COV_subscription ? 0 : Sub.Periode);
    }

    /// <summary>Row of the "Trendlog Objects for Review" grid.</summary>
    public class TrendReviewRow : Observable
    {
        bool _show; string _records = "", _visible = "", _name = "", _description = "";
        public string DeviceText { get; set; }
        public string ObjectIdText { get; set; }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Records { get => _records; set => Set(ref _records, value); }
        public string VisibleRecords { get => _visible; set => Set(ref _visible, value); }
        public string Description { get => _description; set => Set(ref _description, value); }
        public IBrush GraphBrush { get; set; }
        public TrendLogObject Trend;
        public Action<TrendReviewRow> ShowChanged;
        public bool Show { get => _show; set { if (Set(ref _show, value)) ShowChanged?.Invoke(this); } }
    }

    /// <summary>Port of the private TrendLogObject class of MainDialog.HistoricalTrendPlot.cs.</summary>
    public class TrendLogObject
    {
        public int Logsize;
        public List<PlotPt>[] Pointslists;
        public BACnetDevice device; public BacnetObjectId object_id;
        public int NbRecordsByStep = 60;     // 60 is a good value for quite full UDP/IPv4/Ethernet packets
        public int CurvesNumber = 1;
        public int PlottableRecords = 0;
        public bool StopDownload = false;
        public string Title = string.Empty;
        public bool RecordsPresent = false;
        public List<PlotSeries> AssociatedGraphCurves = null;
        public TrendReviewRow Row;
        public int ColorIndex = 0;

        public TrendLogObject(BACnetDevice device, BacnetObjectId object_id) { this.device = device; this.object_id = object_id; }

        public void BeginReadingTrendLog(Action<TrendLogObject, bool, string> callback)
        {
            Logsize = ReadRangeSize(device, object_id);
            if (Logsize < 0) { callback(this, false, "Failed to read the log size of the trendlog."); return; }

            Title = ReadCurveName(device, object_id);
            if (Title == null) { callback(this, false, "Failed to read the trendlog name."); return; }

            CurvesNumber = object_id.type == BacnetObjectTypes.OBJECT_TREND_LOG_MULTIPLE ? ReadNumberofCurves(device, object_id) : 1;
            if (CurvesNumber < 0) { callback(this, false, "Failed to read the number of curves from the trendlog."); return; }

            if (Logsize > 0 && CurvesNumber > 0)
            {
                RecordsPresent = true;
                Pointslists = new List<PlotPt>[CurvesNumber];
                for (int i = 0; i < CurvesNumber; i++) Pointslists[i] = new List<PlotPt>();
                NbRecordsByStep = 60 - 5 * CurvesNumber;
                System.Threading.ThreadPool.QueueUserWorkItem(o => DownloadFullTrendLog((Action<TrendLogObject, bool, string>)o), callback);
            }
            else
            {
                RecordsPresent = false;
                Pointslists = new List<PlotPt>[Math.Max(1, CurvesNumber)];
                for (int i = 0; i < Pointslists.Length; i++) Pointslists[i] = new List<PlotPt>();
                PlottableRecords = 0;
                callback(this, true, "No records present.");
            }
        }

        public static int ReadRangeSize(BACnetDevice device, BacnetObjectId object_id)
        {
            try
            {
                if (!device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_RECORD_COUNT, out var value)) return -1;
                if (value == null || value.Count == 0) return -1;
                return (int)Convert.ChangeType(value[0].Value, typeof(int));
            }
            catch { return -1; }
        }

        public static string ReadCurveName(BACnetDevice device, BacnetObjectId object_id)
        {
            try
            {
                if (!device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_OBJECT_NAME, out var value)) return null;
                if (value == null) return null;
                if (value.Count == 0) return string.Empty;
                return value[0].Value.ToString();
            }
            catch { return null; }
        }

        public static int ReadNumberofCurves(BACnetDevice device, BacnetObjectId object_id)
        {
            try
            {
                if (!device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_LOG_DEVICE_OBJECT_PROPERTY, out var value)) return -1;
                return value == null ? -1 : value.Count;
            }
            catch { return -1; }
        }

        // Download the TrendLog in many blocks of NbRecordsByStep values.
        void DownloadFullTrendLog(Action<TrendLogObject, bool, string> callback)
        {
            try
            {
                PlottableRecords = 0;
                var steppedInAuto = new bool[Pointslists.Length];
                int Idx = 1;
                do
                {
                    uint ItemCount = (uint)Math.Min(NbRecordsByStep, Logsize - Idx + 1);
                    if (!device.ReadRangeRequest(object_id, (uint)Idx, ref ItemCount, out byte[] TrendBuffer) || ItemCount <= 0)
                    {
                        callback(this, false, "Couldn't load log data"); return;
                    }
                    int len = 0;
                    for (int itm = 0; itm < ItemCount; itm++)
                    {
                        int l;
                        if ((l = System.IO.BACnet.Serialize.Services.DecodeLogRecord(TrendBuffer, len, TrendBuffer.Length - len, CurvesNumber, out BacnetLogRecord[] records)) < 0)
                        {
                            callback(this, false, "Couldn't decode log data"); return;
                        }
                        len += l;
                        for (int i = 0; i < records.Length; i++, Idx++)
                        {
                            var t = records[i].type;
                            if (t == BacnetTrendLogValueType.TL_TYPE_UNSIGN || t == BacnetTrendLogValueType.TL_TYPE_SIGN || t == BacnetTrendLogValueType.TL_TYPE_REAL || t == BacnetTrendLogValueType.TL_TYPE_ENUM)
                            {
                                Pointslists[i].Add(new PlotPt(records[i].timestamp.ToOADate(), (double)Convert.ChangeType(records[i].Value, typeof(double))));
                                PlottableRecords++;
                                if (AppSettings.Default.GraphLineStep == "Automatic" && AutomaticGraphStepDeterminer.IsGraphSteppedInAutomaticMode(t))
                                    steppedInAuto[i] = true;
                            }
                        }
                    }
                } while ((Idx + 1) < Logsize && !StopDownload);

                for (int i = 0; i < Pointslists.Length; i++)
                {
                    Pointslists[i].Sort((a, b) => a.X.CompareTo(b.X));
                    if (AppSettings.Default.GraphLineStep == "Stepped" || (AppSettings.Default.GraphLineStep == "Automatic" && steppedInAuto[i]))
                    {
                        var stepped = new List<PlotPt>();
                        for (int j = 0; j < Pointslists[i].Count; j++)
                        {
                            if (j > 0) stepped.Add(new PlotPt(Pointslists[i][j].X, Pointslists[i][j - 1].Y));
                            stepped.Add(Pointslists[i][j]);
                        }
                        Pointslists[i] = stepped;
                    }
                }
                callback(this, true, string.Empty);
            }
            catch (Exception ex)
            {
                callback(this, false, "Error during DownloadFullTrendLog: " + ex.Message);
            }
        }
    }
}
