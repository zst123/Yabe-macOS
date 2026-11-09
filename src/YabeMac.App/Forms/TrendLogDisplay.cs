using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.BACnet;
using System.Threading;
using Yabe;
using YabeMac.Plot;

namespace YabeMac
{
    /// <summary>Row of the log list of the TrendLog display.</summary>
    public class TrendLogRow
    {
        public string Sequence { get; set; }
        public string Time { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
        public string Status { get; set; }
    }

    /// <summary>Port of Forms/TrendLogDisplay.cs: downloads a whole trend log with ReadRange, lists the records and plots them.</summary>
    public class TrendLogDisplay : Window
    {
        int Logsize;
        List<PlotPt>[] Pointslists;
        readonly BACnetDevice device; readonly BacnetObjectId object_id;
        int NbRecordsByStep = 60;
        int CurvesNumber = 1;
        volatile bool StopDownload = false;

        readonly PlotControl _plot = new PlotControl { ShowLegend = false, GradientBackground = true, XTitle = "Date/Time", YTitle = "Values" };
        readonly ProgressBar _progressBar = new ProgressBar { Width = 231, Height = 23, Minimum = 0 };
        readonly TextBlock _progressLabel = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
        readonly DataGrid _list = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, GridLinesVisibility = DataGridGridLinesVisibility.All };
        readonly ObservableCollection<TrendLogRow> _rows = new ObservableCollection<TrendLogRow>();
        readonly StackPanel _progressPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
        static readonly Color[] Rotator = { Colors.Red, Colors.Blue, Colors.Green, Colors.Yellow, Colors.Black, Colors.Purple, Colors.Pink, Colors.Gray };

        public TrendLogDisplay(BACnetDevice device, BacnetObjectId object_id)
        {
            this.device = device; this.object_id = object_id;
            Title = "Yabe TrendLog Display";
            Width = 989; Height = 360; MinWidth = 500; MinHeight = 250;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _list.FontSize = 12;
            _list.Columns.Add(new DataGridTextColumn { Header = "Sequence no", Binding = new Binding("Sequence"), Width = new DataGridLength(90) });
            _list.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding("Time"), Width = new DataGridLength(165) });
            _list.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding("Type"), Width = new DataGridLength(60) });
            _list.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding("Value"), Width = new DataGridLength(70) });
            _list.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(50) });
            _list.ItemsSource = _rows;
            _list.IsVisible = false;     // to avoid flicker during download

            ToolTip.SetTip(_progressBar, "Click here to stop download");
            _progressBar.PointerPressed += (_, __) => StopDownload = true;
            _progressPanel.Children.Add(_progressLabel);
            _progressPanel.Children.Add(_progressBar);

            var menu = new ContextMenu();
            var unzoom = new MenuItem { Header = "Un-Zoom" }; unzoom.Click += (_, __) => _plot.RestoreScale();
            var csv = new MenuItem { Header = "Export Data as CSV" }; csv.Click += async (_, __) => await ShowSaveAsForExportCSV();
            var img = new MenuItem { Header = "Save Image As..." };
            img.Click += async (_, __) =>
            {
                var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { DefaultExtension = "png", SuggestedFileName = "trendlog.png", FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } } });
                var p = f?.TryGetLocalPath(); if (p != null) _plot.SavePng(p);
            };
            menu.Items.Add(unzoom); menu.Items.Add(csv); menu.Items.Add(img);
            _plot.ContextMenu = menu;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("420,4,*") };
            Grid.SetColumn(_list, 0);
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(splitter, 1);
            var right = new Grid();
            right.Children.Add(_plot);
            right.Children.Add(_progressPanel);
            Grid.SetColumn(right, 2);
            grid.Children.Add(_list); grid.Children.Add(splitter); grid.Children.Add(right);
            Content = grid;

            Opened += (_, __) => Start();
            Closed += (_, __) => StopDownload = true;
        }

        void Start()
        {
            _plot.TimeFormat = AppSettings.Default.COVTimeFormater;
            _progressBar.IsEnabled = true;
            string name = null;
            var th = new Thread(() =>
            {
                int size = ReadRangeSize(device, object_id); if (size < 0) size = 0;
                name = ReadCurveName(device, object_id);
                int curves = 1;
                if (object_id.type == BacnetObjectTypes.OBJECT_TREND_LOG_MULTIPLE) curves = ReadNumberofCurves(device, object_id);
                Dispatcher.UIThread.Post(() =>
                {
                    Logsize = size; CurvesNumber = curves;
                    _plot.Title = name;
                    _progressLabel.Text = "Downloads of " + Logsize + " records in progress (0%)";
                    _progressBar.Maximum = Math.Max(1, Logsize);
                    if (Logsize != 0 && CurvesNumber != 0)
                    {
                        Pointslists = new List<PlotPt>[CurvesNumber];
                        for (int i = 0; i < CurvesNumber; i++) Pointslists[i] = new List<PlotPt>();
                        NbRecordsByStep = NbRecordsByStep - 5 * CurvesNumber;
                        Cursor = new Cursor(StandardCursorType.Wait);
                        var dl = new Thread(DownloadFullTrendLog) { IsBackground = true };
                        dl.Start();
                    }
                    else
                    {
                        _progressBar.IsVisible = false;
                        _progressLabel.Text = "The trendlog is empty, nothing to display";
                    }
                });
            }) { IsBackground = true };
            th.Start();
        }

        void UpdateProgress(int add, List<TrendLogRow> rows)
        {
            Cursor = null;
            foreach (var r in rows) _rows.Add(r);
            _progressBar.Value += add;
            int percent = (int)(_progressBar.Value * 100 / Math.Max(1, Logsize));
            _progressLabel.Text = "Downloads of " + Logsize + " records in progress (" + percent + "%)";
        }

        void UpdateEnd(bool state)
        {
            // even if an error occurs during download, maybe some values are OK
            if (Pointslists != null)
                for (int i = 0; i < CurvesNumber; i++)
                    _plot.Series.Add(new PlotSeries { Color = Rotator[i % Rotator.Length], Symbol = "None", LineWidth = 2, Points = Pointslists[i], Tooltip = _plot.Title ?? "" });
            _plot.RestoreScale();
            Cursor = null;
            _progressBar.IsVisible = false;
            if (!state) _progressLabel.Text = "Error loading trend"; else _progressLabel.IsVisible = false;
            _progressPanel.IsHitTestVisible = false;
            _list.IsVisible = true;
        }

        static int ReadRangeSize(BACnetDevice device, BacnetObjectId object_id)
        {
            try
            {
                if (!device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_RECORD_COUNT, out var value)) return -1;
                if (value == null || value.Count == 0) return -1;
                return (int)Convert.ChangeType(value[0].Value, typeof(int));
            }
            catch { return -1; }
        }

        static string ReadCurveName(BACnetDevice device, BacnetObjectId object_id)
        {
            try
            {
                if (!device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_OBJECT_NAME, out var value)) return "";
                if (value == null || value.Count == 0) return "";
                return value[0].Value.ToString();
            }
            catch { return ""; }
        }

        static int ReadNumberofCurves(BACnetDevice device, BacnetObjectId object_id)
        {
            try
            {
                if (!device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_LOG_DEVICE_OBJECT_PROPERTY, out var value)) return 0;
                return value == null ? 0 : value.Count;
            }
            catch { return 0; }
        }

        // Download the TrendLog in many blocks of NbRecordsByStep values.
        void DownloadFullTrendLog()
        {
            try
            {
                int Idx = 1;
                do
                {
                    uint ItemCount = (uint)Math.Min(NbRecordsByStep, Logsize - Idx + 1);
                    if (!device.ReadRangeRequest(object_id, (uint)Idx, ref ItemCount, out byte[] TrendBuffer) || ItemCount <= 0)
                    {
                        Dispatcher.UIThread.Post(() => UpdateEnd(false)); return;
                    }
                    var rows = new List<TrendLogRow>();
                    int len = 0;
                    for (int itm = 0; itm < ItemCount; itm++)
                    {
                        int l;
                        if ((l = System.IO.BACnet.Serialize.Services.DecodeLogRecord(TrendBuffer, len, TrendBuffer.Length - len, CurvesNumber, out BacnetLogRecord[] records)) < 0)
                        {
                            Dispatcher.UIThread.Post(() => UpdateEnd(false)); return;
                        }
                        len += l;
                        for (int i = 0; i < records.Length; i++, Idx++)
                        {
                            var t = records[i].type;
                            double x = records[i].timestamp.ToOADate();
                            if (t == BacnetTrendLogValueType.TL_TYPE_UNSIGN || t == BacnetTrendLogValueType.TL_TYPE_SIGN || t == BacnetTrendLogValueType.TL_TYPE_REAL || t == BacnetTrendLogValueType.TL_TYPE_ENUM)
                                Pointslists[i].Add(new PlotPt(x, (double)Convert.ChangeType(records[i].Value, typeof(double))));
                            else
                                Pointslists[i].Add(new PlotPt(x, double.NaN));
                            rows.Add(new TrendLogRow
                            {
                                Sequence = Idx.ToString(), Time = records[i].timestamp.ToString(),
                                Type = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(t.ToString().ToLower().Substring(8)),
                                Value = records[i].Value != null ? records[i].Value.ToString() : "NULL",
                                Status = records[i].statusFlags.ConvertToInt().ToString()
                            });
                        }
                    }
                    int add = (int)ItemCount;
                    Dispatcher.UIThread.Post(() => UpdateProgress(add, rows));
                } while ((Idx + 1) < Logsize && !StopDownload);

                Dispatcher.UIThread.Post(() => UpdateEnd(true));
            }
            catch
            {
                try { Dispatcher.UIThread.Post(() => UpdateEnd(false)); } catch { }
            }
        }

        // CSV export: only the first trend is used for the X axis and the number of values
        async System.Threading.Tasks.Task ShowSaveAsForExportCSV()
        {
            try
            {
                if (Pointslists == null) return;
                var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    DefaultExtension = "csv", SuggestedFileName = "trendlog.csv",
                    FileTypeChoices = new[] { new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } } }
                });
                var path = f?.TryGetLocalPath();
                if (path == null) return;
                using (var w = new StreamWriter(path))
                {
                    w.Write("Date/Time");
                    for (int i = 0; i < Pointslists.Length; i++) w.Write(";Values");
                    w.WriteLine();
                    for (int i = 0; i < Pointslists[0].Count; i++)
                    {
                        w.Write(DateTime.FromOADate(Pointslists[0][i].X).ToString());
                        for (int cv = 0; cv < Pointslists.Length; cv++)
                            w.Write(";" + (i < Pointslists[cv].Count ? Pointslists[cv][i].Y.ToString() : ""));
                        w.WriteLine();
                    }
                }
            }
            catch { }
        }
    }
}
