using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace YabeMac
{
    /// <summary>One bar in the <see cref="CalendarView"/> (replacement of the original CalendarView.dll CalendarViewItem).</summary>
    public class CalendarViewItem
    {
        public DateTime StartDate;
        public DateTime EndDate;
        public string Text = "";
        public object Tag;
        public Color Color = Colors.Green;
        public CalendarViewItem(DateTime start, DateTime end, string text) { StartDate = start; EndDate = end; Text = text ?? ""; }
        public void ApplyColor(Color c) { Color = c; }
    }

    public class CalendarViewItemEventArgs : EventArgs
    {
        public CalendarViewItem Item;
        public CalendarViewItemEventArgs(CalendarViewItem i) { Item = i; }
    }

    /// <summary>
    /// Month grid showing date items (replacement of the WinForms "Calendar" control of CalendarView.dll).
    /// SetViewRange() clears the items and raises LoadItems so the owner can place its items again,
    /// exactly like the original control. Mouse: click/drag on days selects a range, click an item selects it,
    /// drag an item (or one of its ends) changes its dates, double-click an item raises ItemDoubleClick,
    /// double-click on a day creates an item from the selection, Delete/Backspace deletes the selected item.
    /// </summary>
    public class CalendarView : Control
    {
        public event EventHandler LoadItems;
        public event EventHandler<CalendarViewItemEventArgs> ItemCreated, ItemDeleted, ItemSelected, ItemDoubleClick, ItemDatesChanged;

        public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;
        public DateTime[] Days { get; private set; } = new DateTime[0];
        public List<CalendarViewItem> Items { get; } = new List<CalendarViewItem>();
        public CalendarViewItem SelectedItem { get; private set; }
        public DateTime? SelectionStart { get; private set; }
        public DateTime? SelectionEnd { get; private set; }
        public DateTime ViewStart { get; private set; }
        public DateTime ViewStop { get; private set; }

        const double HeaderH = 22, DayNumH = 16, ItemH = 15, ItemGap = 1;

        readonly List<(Rect rect, CalendarViewItem item, bool hasStart, bool hasEnd)> _hits = new List<(Rect, CalendarViewItem, bool, bool)>();
        enum Drag { None, Select, Move, ResizeStart, ResizeEnd }
        Drag _drag = Drag.None;
        DateTime _dragAnchor, _dragOrigStart, _dragOrigEnd;

        public CalendarView()
        {
            Focusable = true;
            ClipToBounds = true;
            MinWidth = 300; MinHeight = 200;
        }

        // ---------------------------------------------------------------- model
        public void SetViewRange(DateTime start, DateTime stop)
        {
            ViewStart = start.Date; ViewStop = stop;
            var first = start.Date;
            while (first.DayOfWeek != FirstDayOfWeek) first = first.AddDays(-1);
            var last = stop.Date;
            DayOfWeek lastDow = (DayOfWeek)(((int)FirstDayOfWeek + 6) % 7);
            while (last.DayOfWeek != lastDow) last = last.AddDays(1);
            var days = new List<DateTime>();
            for (var d = first; d <= last; d = d.AddDays(1)) days.Add(d);
            Days = days.ToArray();
            Items.Clear();
            SelectedItem = null;
            LoadItems?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        public void SetSelection(DateTime? start, DateTime? end = null)
        {
            SelectionStart = start?.Date; SelectionEnd = (end ?? start)?.Date;
            InvalidateVisual();
        }

        /// <summary>Original: CreateItemOnSelection(text, edit). Raises ItemCreated with the selected day range.</summary>
        public void CreateItemOnSelection(string text = "", bool edit = true)
        {
            if (SelectionStart == null) return;
            var a = SelectionStart.Value; var b = (SelectionEnd ?? a);
            if (b < a) { var t = a; a = b; b = t; }
            var item = new CalendarViewItem(a, b.AddHours(23).AddMinutes(59).AddSeconds(59), text);
            ItemCreated?.Invoke(this, new CalendarViewItemEventArgs(item));
        }

        public void DeleteSelectedItems()
        {
            var it = SelectedItem;
            if (it == null) return;
            Items.Remove(it); SelectedItem = null;
            ItemDeleted?.Invoke(this, new CalendarViewItemEventArgs(it));
            InvalidateVisual();
        }

        public void SelectItem(CalendarViewItem it, bool raise = true)
        {
            SelectedItem = it; InvalidateVisual();
            if (raise && it != null) ItemSelected?.Invoke(this, new CalendarViewItemEventArgs(it));
        }

        // ---------------------------------------------------------------- geometry
        int Weeks => Days.Length / 7;
        double CellW => Bounds.Width / 7.0;
        double CellH => Weeks == 0 ? 0 : (Bounds.Height - HeaderH) / Weeks;

        int DayIndexAt(Point p)
        {
            if (Days.Length == 0 || p.Y < HeaderH) return -1;
            int col = Math.Clamp((int)(p.X / CellW), 0, 6);
            int row = Math.Clamp((int)((p.Y - HeaderH) / CellH), 0, Weeks - 1);
            return row * 7 + col;
        }

        // ---------------------------------------------------------------- rendering
        static readonly Typeface TF = new Typeface(FontFamily.Default);
        static readonly Typeface TFB = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

        FormattedText Text(string s, double size, IBrush b, bool bold = false) =>
            new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? TFB : TF, size, b);

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);
            _hits.Clear();
            var bounds = new Rect(Bounds.Size);
            bool dark = AppSettings.Default.UseGlassTheme && AppSettings.Default.GlassDark && OperatingSystem.IsMacOS();
            IBrush paper = dark ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x31)) : Brushes.White, ink = dark ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)) : Brushes.Black;
            ctx.DrawRectangle(paper, null, bounds);
            if (Days.Length == 0) return;

            var gridPen = new Pen(new SolidColorBrush(dark ? Color.FromRgb(0x4A, 0x4C, 0x52) : Color.FromRgb(0xC8, 0xC8, 0xC8)), 1);
            var outBrush = new SolidColorBrush(dark ? Color.FromRgb(0x23, 0x25, 0x29) : Color.FromRgb(0xEC, 0xEC, 0xEC));
            var selBrush = new SolidColorBrush(Color.FromArgb(90, 0x33, 0x99, 0xFF));
            var headBrush = new SolidColorBrush(dark ? Color.FromRgb(0x33, 0x35, 0x3B) : Color.FromRgb(0xF0, 0xF0, 0xF0));
            double cw = CellW, ch = CellH;

            // header with the day names
            ctx.DrawRectangle(headBrush, null, new Rect(0, 0, bounds.Width, HeaderH));
            for (int c = 0; c < 7; c++)
            {
                string n = CultureInfo.CurrentCulture.DateTimeFormat.DayNames[((int)FirstDayOfWeek + c) % 7];
                var ft = Text(n, 12, ink, true);
                ctx.DrawText(ft, new Point(c * cw + (cw - ft.Width) / 2, (HeaderH - ft.Height) / 2));
            }

            var selA = SelectionStart; var selB = SelectionEnd ?? SelectionStart;
            if (selA != null && selB != null && selB < selA) { var t = selA; selA = selB; selB = t; }

            for (int i = 0; i < Days.Length; i++)
            {
                int r = i / 7, c = i % 7;
                var rect = new Rect(c * cw, HeaderH + r * ch, cw, ch);
                var d = Days[i];
                bool inMonth = d.Month == ViewStart.Month && d.Year == ViewStart.Year;
                ctx.DrawRectangle(inMonth ? paper : outBrush, gridPen, rect);
                if (d.Date == DateTime.Today)
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 200, 0)), null, rect.Deflate(1));
                if (selA != null && d.Date >= selA && d.Date <= selB)
                    ctx.DrawRectangle(selBrush, null, rect.Deflate(1));
                string label = d.Day == 1 ? d.ToString("d MMM", CultureInfo.CurrentCulture) : d.Day.ToString();
                var ft = Text(label, 11, inMonth ? ink : Brushes.Gray, d.Day == 1);
                ctx.DrawText(ft, new Point(rect.Right - ft.Width - 3, rect.Y + 1));
            }

            // items, one bar per week row, stacked in lanes
            for (int r = 0; r < Weeks; r++)
            {
                var weekFirst = Days[r * 7].Date; var weekLast = Days[r * 7 + 6].Date;
                var lanes = new List<int>(); // last occupied column per lane
                foreach (var it in Items)
                {
                    var s = it.StartDate.Date; var e = it.EndDate.Date;
                    if (e < weekFirst || s > weekLast) continue;
                    int c0 = s < weekFirst ? 0 : (int)(s - weekFirst).TotalDays;
                    int c1 = e > weekLast ? 6 : (int)(e - weekFirst).TotalDays;
                    int lane = 0;
                    while (lane < lanes.Count && lanes[lane] >= c0) lane++;
                    if (lane == lanes.Count) lanes.Add(-1);
                    lanes[lane] = c1;
                    double y = HeaderH + r * ch + DayNumH + lane * (ItemH + ItemGap);
                    if (y + ItemH > HeaderH + (r + 1) * ch) continue;  // no room left in this week row
                    var br = new Rect(c0 * cw + 2, y, (c1 - c0 + 1) * cw - 4, ItemH);
                    bool hasStart = s >= weekFirst, hasEnd = e <= weekLast;
                    var fill = new SolidColorBrush(Color.FromArgb(200, it.Color.R, it.Color.G, it.Color.B));
                    bool sel = ReferenceEquals(it, SelectedItem);
                    var pen = new Pen(sel ? Brushes.Black : new SolidColorBrush(Color.FromRgb((byte)(it.Color.R * 0.6), (byte)(it.Color.G * 0.6), (byte)(it.Color.B * 0.6))), sel ? 2 : 1);
                    ctx.DrawRectangle(fill, pen, br, 3, 3);
                    string txt = it.Text;
                    if (txt.Length > 0)
                    {
                        // dark text on yellow/light fills, white on strong fills
                        bool light = (it.Color.R * 299 + it.Color.G * 587 + it.Color.B * 114) / 1000 > 140;
                        var ft = Text(txt, 10, light ? Brushes.Black : Brushes.White);
                        using (ctx.PushClip(br)) ctx.DrawText(ft, new Point(br.X + 3, br.Y + (ItemH - ft.Height) / 2));
                    }
                    _hits.Add((br, it, hasStart, hasEnd));
                }
            }
        }

        // ---------------------------------------------------------------- input
        (CalendarViewItem item, bool hasStart, bool hasEnd, Rect rect)? ItemAt(Point p)
        {
            for (int i = _hits.Count - 1; i >= 0; i--)
                if (_hits[i].rect.Contains(p)) return (_hits[i].item, _hits[i].hasStart, _hits[i].hasEnd, _hits[i].rect);
            return null;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var p = e.GetPosition(this);
            var props = e.GetCurrentPoint(this).Properties;
            if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed) return;
            var hit = ItemAt(p);
            int di = DayIndexAt(p);
            if (hit != null)
            {
                var (it, hasStart, hasEnd, rect) = hit.Value;
                if (!ReferenceEquals(SelectedItem, it) || e.ClickCount < 2) SelectItem(it);
                if (e.ClickCount == 2 && props.IsLeftButtonPressed) { ItemDoubleClick?.Invoke(this, new CalendarViewItemEventArgs(it)); _drag = Drag.None; e.Handled = true; return; }
                if (!props.IsLeftButtonPressed) { e.Handled = true; return; }
                _dragOrigStart = it.StartDate; _dragOrigEnd = it.EndDate;
                _dragAnchor = di >= 0 ? Days[di].Date : it.StartDate.Date;
                if (hasStart && p.X - rect.X < 6) _drag = Drag.ResizeStart;
                else if (hasEnd && rect.Right - p.X < 6) _drag = Drag.ResizeEnd;
                else _drag = Drag.Move;
                e.Pointer.Capture(this);
                e.Handled = true; return;
            }
            if (di < 0) return;
            if (SelectedItem != null) { SelectedItem = null; }
            var day = Days[di].Date;
            if (e.ClickCount == 2 && props.IsLeftButtonPressed && SelectionStart != null && day >= Min(SelectionStart.Value, SelectionEnd ?? SelectionStart.Value) && day <= Max(SelectionStart.Value, SelectionEnd ?? SelectionStart.Value))
            {
                CreateItemOnSelection("", true); e.Handled = true; return;
            }
            // keep a right-click inside the current selection, otherwise start a new one
            if (props.IsRightButtonPressed && SelectionStart != null && day >= Min(SelectionStart.Value, SelectionEnd.Value) && day <= Max(SelectionStart.Value, SelectionEnd.Value)) return;
            _drag = Drag.Select; _dragAnchor = day;
            SetSelection(day, day);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
        static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_drag == Drag.None) return;
            int di = DayIndexAt(e.GetPosition(this));
            if (di < 0) return;
            var day = Days[di].Date;
            switch (_drag)
            {
                case Drag.Select: SetSelection(_dragAnchor, day); break;
                case Drag.Move:
                    {
                        var delta = day - _dragAnchor;
                        SelectedItem.StartDate = _dragOrigStart + delta; SelectedItem.EndDate = _dragOrigEnd + delta;
                        InvalidateVisual(); break;
                    }
                case Drag.ResizeStart:
                    if (day <= _dragOrigEnd.Date) { SelectedItem.StartDate = day; InvalidateVisual(); }
                    break;
                case Drag.ResizeEnd:
                    if (day >= _dragOrigStart.Date) { SelectedItem.EndDate = day.AddHours(23).AddMinutes(59).AddSeconds(59); InvalidateVisual(); }
                    break;
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var d = _drag; _drag = Drag.None;
            e.Pointer.Capture(null);
            if ((d == Drag.Move || d == Drag.ResizeStart || d == Drag.ResizeEnd) && SelectedItem != null &&
                (SelectedItem.StartDate != _dragOrigStart || SelectedItem.EndDate != _dragOrigEnd))
                ItemDatesChanged?.Invoke(this, new CalendarViewItemEventArgs(SelectedItem));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if ((e.Key == Key.Delete || e.Key == Key.Back) && SelectedItem != null) { DeleteSelectedItems(); e.Handled = true; }
            else if (e.Key == Key.Enter && SelectedItem != null) { ItemDoubleClick?.Invoke(this, new CalendarViewItemEventArgs(SelectedItem)); e.Handled = true; }
        }
    }
}
