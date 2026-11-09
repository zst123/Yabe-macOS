using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace YabeMac.Plot
{
    public struct PlotPt
    {
        public double X, Y;
        public PlotPt(double x, double y) { X = x; Y = y; }
    }

    /// <summary>One curve of the plot (the ZedGraph LineItem + its RollingPointPairList). X is an OADate (like ZedGraph XDate).</summary>
    public class PlotSeries
    {
        public string Name = "";           // legend text
        public string Tooltip = "";        // text shown on the hover tooltip (ZedGraph curve.Tag)
        public Color Color = Colors.Red;
        public bool Visible = true;
        public string Symbol = "Square";   // ZedGraph SymbolType name
        public double LineWidth = 1;
        public int MaxPoints = 0;          // 0 = unlimited; otherwise rolling window (RollingPointPairList)
        public List<PlotPt> Points = new List<PlotPt>();

        public void Add(double x, double y)
        {
            Points.Add(new PlotPt(x, y));
            if (MaxPoints > 0 && Points.Count > MaxPoints) Points.RemoveRange(0, Points.Count - MaxPoints);
        }
        public bool HasPoints => Points.Count > 0;
        public PlotPt Peek() => Points[Points.Count - 1];
    }

    /// <summary>
    /// Cross-platform replacement of the ZedGraph control: date X axis, auto-scaled Y axis, grid, legend,
    /// multi-series lines with symbols, zoom (drag rectangle / wheel), pan (shift-drag / middle-drag), hover point values.
    /// </summary>
    public class PlotControl : Control
    {
        public List<PlotSeries> Series { get; } = new List<PlotSeries>();
        public string TimeFormat { get; set; } = "HH:mm:ss";
        public string Title { get; set; }
        public string XTitle { get; set; }
        public string YTitle { get; set; }
        public bool ShowLegend { get; set; } = true;
        public bool GradientBackground { get; set; }

        // Colours for the margin, plot area and ink (axes, tick labels, legend text). Defaults = original ZedGraph look;
        // the glass theme sets a transparent background with light ink.
        IBrush _outerBrush = Brushes.White, _areaBrush = Brushes.White, _inkBrush = Brushes.Black;
        public IBrush OuterBrush { get => _outerBrush; set { _outerBrush = value; InvalidateVisual(); } }
        public IBrush AreaBrush { get => _areaBrush; set { _areaBrush = value; InvalidateVisual(); } }
        public IBrush InkBrush { get => _inkBrush; set { _inkBrush = value; InvalidateVisual(); } }
        public Func<PlotSeries, PlotPt, string> PointText { get; set; }
        public event Action ZoomChanged;

        // current zoom window (null = auto-scaled)
        double[] _view;
        public bool IsZoomed => _view != null;
        public bool IsDragging { get; private set; }

        Rect _plot;
        Point? _selStart, _selNow;
        bool _panning;
        Point _panLast;
        PlotSeries _hoverSeries; int _hoverIdx = -1;

        static readonly Typeface Face = new Typeface("Helvetica Neue,Arial,sans-serif");
        static readonly double[] TimeSteps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800, 2592000, 7776000, 31536000 };

        public PlotControl()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        public void AxisChange() { InvalidateVisual(); }
        public void Invalidate() { InvalidateVisual(); }
        public void RestoreScale() { _view = null; ZoomChanged?.Invoke(); InvalidateVisual(); }

        /// <summary>Shifts the visible X window (used by "Always Scroll" while zoomed).</summary>
        public void ShiftX(double dx) { if (_view != null) { _view[0] += dx; _view[1] += dx; InvalidateVisual(); } }
        public double XMax { get { GetRange(out _, out var x1, out _, out _); return x1; } }

        // ------------------------------------------------------------------ ranges
        void GetRange(out double x0, out double x1, out double y0, out double y1)
        {
            if (_view != null) { x0 = _view[0]; x1 = _view[1]; y0 = _view[2]; y1 = _view[3]; return; }
            double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
            foreach (var s in Series)
            {
                if (!s.Visible) continue;
                foreach (var p in s.Points)
                {
                    if (double.IsNaN(p.Y) || double.IsInfinity(p.Y)) continue;
                    if (p.X < xmin) xmin = p.X; if (p.X > xmax) xmax = p.X;
                    if (p.Y < ymin) ymin = p.Y; if (p.Y > ymax) ymax = p.Y;
                }
            }
            if (xmin > xmax) { double now = DateTime.Now.ToOADate(); xmin = now - 60.0 / 86400; xmax = now; ymin = 0; ymax = 1; }
            if (xmax - xmin < 5.0 / 86400) xmax = xmin + 5.0 / 86400;
            if (ymax - ymin < 1e-9) { double d = Math.Abs(ymax) > 1e-9 ? Math.Abs(ymax) * 0.1 : 1; ymin -= d; ymax += d; }
            double plotH = Math.Max(_plot.Height, 100);
            double step = NiceStep(ymax - ymin, Math.Max(2, (int)(plotH / 40)));
            y0 = Math.Floor(ymin / step) * step; y1 = Math.Ceiling(ymax / step) * step;
            x0 = xmin; x1 = xmax;
        }

        static double NiceStep(double range, int target)
        {
            double raw = range / Math.Max(1, target);
            if (raw <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            double s = n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10;
            return s * mag;
        }

        // ------------------------------------------------------------------ drawing
        FormattedText Text(string s, double size, IBrush brush) =>
            new FormattedText(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush);

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            ctx.FillRectangle(_outerBrush, new Rect(size));
            if (size.Width < 120 || size.Height < 80) return;

            var legendEntries = ShowLegend ? Series.Where(s => s.Visible && !string.IsNullOrEmpty(s.Name)).ToList() : new List<PlotSeries>();
            // legend layout (bottom flush left, horizontal stack, wraps)
            var legendPos = new List<(PlotSeries s, FormattedText t, Point p)>();
            double legendH = 0;
            if (legendEntries.Count > 0)
            {
                double x = 8, y = 0, rowH = 16;
                foreach (var s in legendEntries)
                {
                    var t = Text(s.Name, 10, _inkBrush);
                    double w = 26 + t.Width + 14;
                    if (x + w > size.Width - 8 && x > 8) { x = 8; y += rowH; }
                    legendPos.Add((s, t, new Point(x, y)));
                    x += w;
                }
                legendH = y + rowH + 6;
            }

            double left = 64, right = 14, top = string.IsNullOrEmpty(Title) ? 10 : 28;
            double bottom = 26 + (string.IsNullOrEmpty(XTitle) ? 0 : 16) + legendH;
            if (!string.IsNullOrEmpty(YTitle)) left += 14;
            _plot = new Rect(left, top, Math.Max(10, size.Width - left - right), Math.Max(10, size.Height - top - bottom));

            GetRange(out var x0, out var x1, out var y0, out var y1);
            if (x1 <= x0) x1 = x0 + 1e-6; if (y1 <= y0) y1 = y0 + 1e-6;
            double MX(double x) => _plot.Left + (x - x0) / (x1 - x0) * _plot.Width;
            double MY(double y) => _plot.Bottom - (y - y0) / (y1 - y0) * _plot.Height;

            // plot area background
            IBrush area = _areaBrush;
            if (GradientBackground)
                area = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromRgb(255, 255, 190), 1) }
                };
            ctx.FillRectangle(area, _plot);

            var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(128, 128, 128)), 1, new DashStyle(new double[] { 1, 3 }, 0));
            var axisPen = new Pen(_inkBrush, 1);
            var textBrush = _inkBrush;

            // Y ticks
            double ystep = NiceStep(y1 - y0, Math.Max(2, (int)(_plot.Height / 40)));
            for (double v = Math.Ceiling(y0 / ystep) * ystep; v <= y1 + ystep * 1e-6; v += ystep)
            {
                double py = MY(v);
                ctx.DrawLine(gridPen, new Point(_plot.Left, py), new Point(_plot.Right, py));
                var t = Text(Math.Round(v, 10).ToString("G7", CultureInfo.CurrentCulture), 10, textBrush);
                ctx.DrawText(t, new Point(_plot.Left - 6 - t.Width, py - t.Height / 2));
            }
            // X ticks (date axis)
            double rangeSec = (x1 - x0) * 86400;
            int target = Math.Max(2, (int)(_plot.Width / 90));
            double stepSec = TimeSteps.FirstOrDefault(s => rangeSec / s <= target);
            if (stepSec == 0) stepSec = TimeSteps[TimeSteps.Length - 1];
            double stepDay = stepSec / 86400;
            string fmt = stepSec >= 86400 ? "yyyy-MM-dd" : (rangeSec > 86400 ? "MM-dd HH:mm" : TimeFormat);
            if (stepSec < 1) fmt = TimeFormat;
            double lastLabelRight = double.MinValue;
            for (double v = Math.Ceiling(x0 / stepDay) * stepDay; v <= x1 + stepDay * 1e-6; v += stepDay)
            {
                double px = MX(v);
                ctx.DrawLine(gridPen, new Point(px, _plot.Top), new Point(px, _plot.Bottom));
                ctx.DrawLine(axisPen, new Point(px, _plot.Bottom), new Point(px, _plot.Bottom + 4));
                string label;
                try { label = DateTime.FromOADate(Math.Round(v * 86400) / 86400).ToString(fmt, CultureInfo.CurrentCulture); } catch { label = ""; }
                var t = Text(label, 10, textBrush);
                if (px - t.Width / 2 > lastLabelRight + 4)
                {
                    ctx.DrawText(t, new Point(px - t.Width / 2, _plot.Bottom + 5));
                    lastLabelRight = px + t.Width / 2;
                }
            }

            if (!string.IsNullOrEmpty(Title))
            {
                var t = Text(Title, 13, textBrush);
                ctx.DrawText(t, new Point((size.Width - t.Width) / 2, 5));
            }
            if (!string.IsNullOrEmpty(XTitle))
            {
                var t = Text(XTitle, 11, textBrush);
                ctx.DrawText(t, new Point(_plot.Left + (_plot.Width - t.Width) / 2, _plot.Bottom + 21));
            }
            if (!string.IsNullOrEmpty(YTitle))
            {
                var t = Text(YTitle, 11, textBrush);
                // rotation about the origin only (no translation component in the matrix), text placed in pre-rotation space
                using (ctx.PushTransform(new Matrix(0, -1, 1, 0, 0, 0)))
                    ctx.DrawText(t, new Point(-(_plot.Top + _plot.Height / 2) - t.Width / 2, 10 - t.Height / 2));
            }

            // curves
            using (ctx.PushClip(_plot))
            {
                foreach (var s in Series)
                {
                    if (!s.Visible || s.Points.Count == 0) continue;
                    var brush = new SolidColorBrush(s.Color);
                    var pen = new Pen(brush, s.LineWidth);
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        bool open = false;
                        foreach (var p in s.Points)
                        {
                            if (double.IsNaN(p.Y) || double.IsInfinity(p.Y)) { if (open) { g.EndFigure(false); open = false; } continue; }
                            var pt = new Point(MX(p.X), MY(p.Y));
                            if (!open) { g.BeginFigure(pt, false); open = true; } else g.LineTo(pt);
                        }
                        if (open) g.EndFigure(false);
                    }
                    ctx.DrawGeometry(null, pen, geo);
                    if (s.Symbol != "None")
                        foreach (var p in s.Points)
                        {
                            if (double.IsNaN(p.Y)) continue;
                            double px = MX(p.X), py = MY(p.Y);
                            if (px < _plot.Left - 4 || px > _plot.Right + 4 || py < _plot.Top - 4 || py > _plot.Bottom + 4) continue;
                            DrawSymbol(ctx, s.Symbol, brush, px, py, 7);
                        }
                }
            }

            // frame
            ctx.DrawRectangle(null, axisPen, _plot);

            // legend
            if (legendPos.Count > 0)
            {
                double baseY = size.Height - legendH + 3;
                foreach (var (s, t, p) in legendPos)
                {
                    var brush = new SolidColorBrush(s.Color);
                    double cy = baseY + p.Y + 8;
                    ctx.DrawLine(new Pen(brush, 1.5), new Point(p.X, cy), new Point(p.X + 22, cy));
                    if (s.Symbol != "None") DrawSymbol(ctx, s.Symbol, brush, p.X + 11, cy, 7);
                    ctx.DrawText(t, new Point(p.X + 26, cy - t.Height / 2));
                }
            }

            // zoom rectangle
            if (_selStart.HasValue && _selNow.HasValue)
            {
                var r = new Rect(_selStart.Value, _selNow.Value);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 255)), new Pen(Brushes.Black, 1, DashStyle.Dash), r);
            }

            // hover tooltip
            if (_hoverSeries != null && _hoverIdx >= 0 && _hoverIdx < _hoverSeries.Points.Count && !IsDragging)
            {
                var p = _hoverSeries.Points[_hoverIdx];
                double px = MX(p.X), py = MY(p.Y);
                ctx.DrawEllipse(null, new Pen(Brushes.Black, 1.5), new Point(px, py), 5, 5);
                string txt = (PointText ?? DefaultPointText)(_hoverSeries, p);
                var t = Text(txt, 11, Brushes.Black);
                var box = new Rect(px + 10, py + 10, t.Width + 12, t.Height + 8);
                if (box.Right > size.Width) box = box.WithX(px - 10 - box.Width);
                if (box.Bottom > size.Height) box = box.WithY(py - 10 - box.Height);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(255, 255, 225)), new Pen(Brushes.Black, 1), box, 2, 2);
                ctx.DrawText(t, new Point(box.X + 6, box.Y + 4));
            }
        }

        string DefaultPointText(PlotSeries s, PlotPt p)
        {
            string time;
            try { time = DateTime.FromOADate(p.X).ToString(TimeFormat); } catch { time = p.X.ToString(); }
            return (s.Tooltip ?? s.Name) + Environment.NewLine + time + "    " + p.Y.ToString();
        }

        static void DrawSymbol(DrawingContext ctx, string symbol, IBrush brush, double x, double y, double size)
        {
            double h = size / 2;
            var pen = new Pen(brush, 1);
            switch (symbol)
            {
                case "Circle": ctx.DrawEllipse(null, pen, new Point(x, y), h, h); break;
                case "Diamond":
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open()) { c.BeginFigure(new Point(x, y - h), true); c.LineTo(new Point(x + h, y)); c.LineTo(new Point(x, y + h)); c.LineTo(new Point(x - h, y)); c.EndFigure(true); }
                        ctx.DrawGeometry(null, pen, g); break;
                    }
                case "Triangle":
                case "TriangleDown":
                    {
                        double dir = symbol == "Triangle" ? -1 : 1;
                        var g = new StreamGeometry();
                        using (var c = g.Open()) { c.BeginFigure(new Point(x, y + dir * h), true); c.LineTo(new Point(x + h, y - dir * h)); c.LineTo(new Point(x - h, y - dir * h)); c.EndFigure(true); }
                        ctx.DrawGeometry(null, pen, g); break;
                    }
                case "XCross":
                    ctx.DrawLine(pen, new Point(x - h, y - h), new Point(x + h, y + h));
                    ctx.DrawLine(pen, new Point(x - h, y + h), new Point(x + h, y - h)); break;
                case "Plus":
                    ctx.DrawLine(pen, new Point(x - h, y), new Point(x + h, y));
                    ctx.DrawLine(pen, new Point(x, y - h), new Point(x, y + h)); break;
                case "Star":
                    ctx.DrawLine(pen, new Point(x - h, y), new Point(x + h, y));
                    ctx.DrawLine(pen, new Point(x, y - h), new Point(x, y + h));
                    ctx.DrawLine(pen, new Point(x - h, y - h), new Point(x + h, y + h));
                    ctx.DrawLine(pen, new Point(x - h, y + h), new Point(x + h, y - h)); break;
                case "HDash": ctx.DrawLine(pen, new Point(x - h, y), new Point(x + h, y)); break;
                case "VDash": ctx.DrawLine(pen, new Point(x, y - h), new Point(x, y + h)); break;
                default: ctx.DrawRectangle(null, pen, new Rect(x - h, y - h, size, size)); break;   // Square / Default
            }
        }

        // ------------------------------------------------------------------ interaction
        double DataX(double px) { GetRange(out var x0, out var x1, out _, out _); return x0 + (px - _plot.Left) / _plot.Width * (x1 - x0); }
        double DataY(double py) { GetRange(out _, out _, out var y0, out var y1); return y0 + (_plot.Bottom - py) / _plot.Height * (y1 - y0); }

        void EnsureView() { if (_view == null) { GetRange(out var a, out var b, out var c, out var d); _view = new[] { a, b, c, d }; } }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pt = e.GetCurrentPoint(this);
            var pos = pt.Position;
            if (!_plot.Contains(pos)) return;
            if (pt.Properties.IsMiddleButtonPressed || (pt.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            {
                _panning = true; _panLast = pos; IsDragging = true; e.Pointer.Capture(this);
            }
            else if (pt.Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount >= 2) { RestoreScale(); return; }
                _selStart = pos; _selNow = pos; IsDragging = true; e.Pointer.Capture(this);
            }
            _hoverSeries = null;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);
            if (_panning)
            {
                EnsureView();
                double dx = (pos.X - _panLast.X) / _plot.Width * (_view[1] - _view[0]);
                double dy = (pos.Y - _panLast.Y) / _plot.Height * (_view[3] - _view[2]);
                _view[0] -= dx; _view[1] -= dx; _view[2] += dy; _view[3] += dy;
                _panLast = pos;
                ZoomChanged?.Invoke();
                InvalidateVisual();
            }
            else if (_selStart.HasValue)
            {
                _selNow = new Point(Math.Max(_plot.Left, Math.Min(_plot.Right, pos.X)), Math.Max(_plot.Top, Math.Min(_plot.Bottom, pos.Y)));
                InvalidateVisual();
            }
            else UpdateHover(pos);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_selStart.HasValue && _selNow.HasValue)
            {
                var a = _selStart.Value; var b = _selNow.Value;
                if (Math.Abs(a.X - b.X) > 5 && Math.Abs(a.Y - b.Y) > 5)
                {
                    double xa = DataX(Math.Min(a.X, b.X)), xb = DataX(Math.Max(a.X, b.X));
                    double yb = DataY(Math.Min(a.Y, b.Y)), ya = DataY(Math.Max(a.Y, b.Y));
                    _view = new[] { xa, xb, ya, yb };
                    ZoomChanged?.Invoke();
                }
            }
            _selStart = _selNow = null; _panning = false; IsDragging = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); if (_hoverSeries != null) { _hoverSeries = null; InvalidateVisual(); } }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var pos = e.GetPosition(this);
            if (!_plot.Contains(pos) || e.Delta.Y == 0) return;
            double cx = DataX(pos.X), cy = DataY(pos.Y);
            EnsureView();
            double f = e.Delta.Y > 0 ? 0.8 : 1.25;
            _view[0] = cx - (cx - _view[0]) * f; _view[1] = cx + (_view[1] - cx) * f;
            _view[2] = cy - (cy - _view[2]) * f; _view[3] = cy + (_view[3] - cy) * f;
            ZoomChanged?.Invoke();
            InvalidateVisual();
            e.Handled = true;
        }

        void UpdateHover(Point pos)
        {
            PlotSeries best = null; int bestIdx = -1; double bestD = 12 * 12;
            if (_plot.Contains(pos))
            {
                GetRange(out var x0, out var x1, out var y0, out var y1);
                foreach (var s in Series)
                {
                    if (!s.Visible) continue;
                    for (int i = 0; i < s.Points.Count; i++)
                    {
                        var p = s.Points[i];
                        if (double.IsNaN(p.Y)) continue;
                        double px = _plot.Left + (p.X - x0) / (x1 - x0) * _plot.Width;
                        if (Math.Abs(px - pos.X) > 12) continue;
                        double py = _plot.Bottom - (p.Y - y0) / (y1 - y0) * _plot.Height;
                        double d = (px - pos.X) * (px - pos.X) + (py - pos.Y) * (py - pos.Y);
                        if (d < bestD) { bestD = d; best = s; bestIdx = i; }
                    }
                }
            }
            if (best != _hoverSeries || bestIdx != _hoverIdx) { _hoverSeries = best; _hoverIdx = bestIdx; InvalidateVisual(); }
        }

        /// <summary>Renders the control to a PNG file.</summary>
        public void SavePng(string path)
        {
            int w = Math.Max(1, (int)Bounds.Width), h = Math.Max(1, (int)Bounds.Height);
            _hoverSeries = null;
            using (var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96)))
            {
                rtb.Render(this);
                rtb.Save(path);
            }
        }
    }
}
