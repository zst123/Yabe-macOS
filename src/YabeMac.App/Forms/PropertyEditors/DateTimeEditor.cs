using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.BACnet;
using System.Linq;
using YabeMac.PropertyPanel;

namespace YabeMac.Forms
{
    /// <summary>
    /// Editor of BACnet Date / Time / DateTime property values (replaces the PropertyGrid DateTimePicker editors of the original).
    /// Every field can be left 'unspecified' (wildcard); the month and day also offer the BACnet specials (odd / even / last day).
    /// </summary>
    public sealed class DateTimeEditor : UserControl
    {
        static readonly string[] Months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
        static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        readonly bool _hasDate, _hasTime;
        NumericUpDown _year, _hour, _min, _sec, _hund;
        CheckBox _yearAny, _hourAny, _minAny, _secAny, _hundAny;
        ComboBox _month, _day, _weekday;
        CalendarDatePicker _picker;
        TextBlock _preview, _error;
        Button _ok;
        bool _busy;

        public event Action<DateTimeParts> Accepted;
        public event Action Cancelled;

        /// <summary>The value currently shown by the editor (null when the date is impossible, e.g. February 30).</summary>
        public DateTimeParts Current { get; private set; }

        public DateTimeEditor(DateTimeParts initial)
        {
            _hasDate = initial.HasDate; _hasTime = initial.HasTime;
            Content = Build(initial);
            Refresh();
        }

        // ------------------------------------------------------------------------------------------------------
        public static Flyout CreateFlyout(PropertyNode node, Action<DateTimeParts> accept, Action cancel)
        {
            var init = node.Parts?.Clone() ?? DateTimeParts.FromDateTime(node.Value is DateTime d ? d : DateTime.Now, node.Tag ?? KindTag(node.Kind));
            // the node decides what is edited (a stale Parts of another shape must not leak in)
            init.HasDate = node.Kind != EditKind.Time; init.HasTime = node.Kind != EditKind.Date;
            var ed = new DateTimeEditor(init);
            ed.Accepted += accept; ed.Cancelled += cancel;
            return new Flyout { Content = ed, Placement = PlacementMode.Bottom };
        }

        static BacnetApplicationTags? KindTag(EditKind k) =>
            k == EditKind.Time ? BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME :
            k == EditKind.Date ? BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE :
            BacnetApplicationTags.BACNET_APPLICATION_TAG_DATETIME;

        // ------------------------------------------------------------------------------------------------------
        static NumericUpDown Nud(int min, int max, int val) => new NumericUpDown
        {
            Minimum = min, Maximum = max, Value = val, Increment = 1, FormatString = "0", MinWidth = 70, Width = 80,
            HorizontalContentAlignment = HorizontalAlignment.Left, ShowButtonSpinner = false, ParsingNumberStyle = NumberStyles.Integer,
        };
        static CheckBox Any(string t = "any") => new CheckBox { Content = t, Margin = new Thickness(0, 2, 0, 0) };
        static TextBlock Head(string t) => new TextBlock { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 2) };
        static TextBlock Lbl(string t) => new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };

        Control Build(DateTimeParts p)
        {
            var root = new StackPanel { Spacing = 4, Margin = new Thickness(10), MinWidth = 330 };

            if (_hasDate)
            {
                root.Children.Add(Head("Date"));
                _picker = new CalendarDatePicker { Watermark = "pick a date", HorizontalAlignment = HorizontalAlignment.Stretch };
                if (p.DateIsSpecific) { try { _picker.SelectedDate = new DateTime(p.Year, p.Month, p.Day); } catch { } }
                _picker.SelectedDateChanged += (_, __) =>
                {
                    if (_busy || _picker.SelectedDate is not DateTime d) return;
                    _busy = true;
                    _year.Value = d.Year; _yearAny.IsChecked = false; _month.SelectedIndex = 2 + d.Month; _day.SelectedIndex = 3 + d.Day;
                    _busy = false; Refresh();
                };
                root.Children.Add(_picker);

                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowSpacing = 4, ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
                for (int i = 0; i < 4; i++) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                _year = Nud(1900, 2154, p.Year == DateTimeParts.Any ? DateTime.Today.Year : p.Year);
                _yearAny = Any(); _yearAny.IsChecked = p.Year == DateTimeParts.Any;
                _month = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                _month.Items.Add("(any month)"); _month.Items.Add("odd months"); _month.Items.Add("even months");
                foreach (var m in Months) _month.Items.Add(m);
                _month.SelectedIndex = p.Month == DateTimeParts.Any ? 0 : p.Month == 13 ? 1 : p.Month == 14 ? 2 : 2 + p.Month;
                _day = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                _day.Items.Add("(any day)"); _day.Items.Add("last day of month"); _day.Items.Add("odd days"); _day.Items.Add("even days");
                for (int d = 1; d <= 31; d++) _day.Items.Add(d.ToString());
                _day.SelectedIndex = p.Day == DateTimeParts.Any ? 0 : p.Day == 32 ? 1 : p.Day == 33 ? 2 : p.Day == 34 ? 3 : 3 + p.Day;
                _weekday = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                _weekday.Items.Add("(any weekday)");
                foreach (var d in Days) _weekday.Items.Add(d);
                _weekday.SelectedIndex = p.Weekday == DateTimeParts.Any ? 0 : p.Weekday;

                void Row(int r, string l, Control c, Control extra = null)
                {
                    var t = Lbl(l); Grid.SetRow(t, r); Grid.SetColumn(t, 0); g.Children.Add(t);
                    Grid.SetRow(c, r); Grid.SetColumn(c, 1); g.Children.Add(c);
                    if (extra != null) { Grid.SetRow(extra, r); Grid.SetColumn(extra, 2); g.Children.Add(extra); }
                }
                _year.HorizontalAlignment = HorizontalAlignment.Left;
                Row(0, "Year", _year, _yearAny); Row(1, "Month", _month); Row(2, "Day", _day); Row(3, "Weekday", _weekday);
                root.Children.Add(g);
                _yearAny.IsCheckedChanged += (_, __) => Refresh();
                _year.ValueChanged += (_, __) => Refresh();
                _month.SelectionChanged += (_, __) => Refresh();
                _day.SelectionChanged += (_, __) => Refresh();
                _weekday.SelectionChanged += (_, __) => Refresh();
            }

            if (_hasTime)
            {
                root.Children.Add(Head("Time"));
                var g = new Grid { ColumnSpacing = 8, RowSpacing = 2, ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
                for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                _hour = Nud(0, 23, p.Hour == DateTimeParts.Any ? DateTime.Now.Hour : p.Hour); _hourAny = Any(); _hourAny.IsChecked = p.Hour == DateTimeParts.Any;
                _min = Nud(0, 59, p.Minute == DateTimeParts.Any ? 0 : p.Minute); _minAny = Any(); _minAny.IsChecked = p.Minute == DateTimeParts.Any;
                _sec = Nud(0, 59, p.Second == DateTimeParts.Any ? 0 : p.Second); _secAny = Any(); _secAny.IsChecked = p.Second == DateTimeParts.Any;
                _hund = Nud(0, 99, p.Hundredths == DateTimeParts.Any ? 0 : p.Hundredths); _hundAny = Any(); _hundAny.IsChecked = p.Hundredths == DateTimeParts.Any;
                var items = new (string, NumericUpDown, CheckBox)[] { ("Hour", _hour, _hourAny), ("Minute", _min, _minAny), ("Second", _sec, _secAny), ("1/100 s", _hund, _hundAny) };
                for (int i = 0; i < 4; i++)
                {
                    var (l, n, c) = items[i];
                    n.Width = double.NaN; n.MinWidth = 60; n.HorizontalAlignment = HorizontalAlignment.Stretch;
                    var lb = Lbl(l); Grid.SetColumn(lb, i); g.Children.Add(lb);
                    Grid.SetColumn(n, i); Grid.SetRow(n, 1); g.Children.Add(n);
                    Grid.SetColumn(c, i); Grid.SetRow(c, 2); g.Children.Add(c);
                    n.ValueChanged += (_, __) => Refresh();
                    c.IsCheckedChanged += (_, __) => Refresh();
                }
                root.Children.Add(g);
            }

            var now = new Button { Content = "Now", HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            now.Click += (_, __) => SetNow();
            var any = new Button { Content = "All unspecified", HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            any.Click += (_, __) => SetAllAny();
            _preview = new TextBlock { FontFamily = new FontFamily("Menlo,Consolas,monospace"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            _error = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };
            _ok = new Button { Content = "OK", MinWidth = 70, IsDefault = true, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            var cancel = new Button { Content = "Cancel", MinWidth = 70, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            _ok.Click += (_, __) => { if (Current != null) Accepted?.Invoke(Current); };
            cancel.Click += (_, __) => Cancelled?.Invoke();
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { now, any } };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _ok, cancel } };
            var bar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right); bar.Children.Add(left);
            root.Children.Add(_preview); root.Children.Add(_error); root.Children.Add(bar);
            return root;
        }

        void SetNow()
        {
            var n = DateTime.Now; _busy = true;
            if (_hasDate)
            {
                _year.Value = n.Year; _yearAny.IsChecked = false; _month.SelectedIndex = 2 + n.Month; _day.SelectedIndex = 3 + n.Day;
                try { _picker.SelectedDate = n.Date; } catch { }
            }
            if (_hasTime)
            {
                _hour.Value = n.Hour; _min.Value = n.Minute; _sec.Value = n.Second; _hund.Value = n.Millisecond / 10;
                _hourAny.IsChecked = _minAny.IsChecked = _secAny.IsChecked = _hundAny.IsChecked = false;
            }
            _busy = false; Refresh();
        }

        void SetAllAny()
        {
            _busy = true;
            if (_hasDate) { _yearAny.IsChecked = true; _month.SelectedIndex = 0; _day.SelectedIndex = 0; _weekday.SelectedIndex = 0; _picker.SelectedDate = null; }
            if (_hasTime) _hourAny.IsChecked = _minAny.IsChecked = _secAny.IsChecked = _hundAny.IsChecked = true;
            _busy = false; Refresh();
        }

        static int V(NumericUpDown n, CheckBox any) => any.IsChecked == true ? DateTimeParts.Any : (int)(n.Value ?? 0);

        /// <summary>Reads the controls into a value.</summary>
        DateTimeParts Collect()
        {
            var p = new DateTimeParts { HasDate = _hasDate, HasTime = _hasTime };
            if (_hasDate)
            {
                p.Year = V(_year, _yearAny);
                int mi = _month.SelectedIndex; p.Month = mi <= 0 ? DateTimeParts.Any : mi == 1 ? 13 : mi == 2 ? 14 : mi - 2;
                int di = _day.SelectedIndex; p.Day = di <= 0 ? DateTimeParts.Any : di == 1 ? 32 : di == 2 ? 33 : di == 3 ? 34 : di - 3;
                p.Weekday = _weekday.SelectedIndex <= 0 ? DateTimeParts.Any : _weekday.SelectedIndex;
            }
            if (_hasTime)
            {
                p.Hour = V(_hour, _hourAny); p.Minute = V(_min, _minAny); p.Second = V(_sec, _secAny); p.Hundredths = V(_hund, _hundAny);
            }
            return p;
        }

        void Refresh()
        {
            if (_busy || _preview == null) return;
            var p = Collect();
            string err = null;
            if (_hasDate)
            {
                if (p.DateIsSpecific)
                {
                    int wd = p.ComputedWeekday();
                    if (wd == DateTimeParts.Any) err = "Not a valid date.";
                    else { _busy = true; _weekday.SelectedIndex = wd; p.Weekday = wd; _busy = false; }
                }
                _weekday.IsEnabled = !p.DateIsSpecific;
                _year.IsEnabled = _yearAny.IsChecked != true;
                if (p.Day >= 1 && p.Day <= 31 && p.Month >= 1 && p.Month <= 12 && p.Day > (p.Year == DateTimeParts.Any ? (p.Month == 2 ? 29 : DateTime.DaysInMonth(2001, p.Month)) : DateTime.DaysInMonth(p.Year, p.Month)))
                    err = "Not a valid date.";
            }
            if (_hasTime)
            {
                _hour.IsEnabled = _hourAny.IsChecked != true; _min.IsEnabled = _minAny.IsChecked != true;
                _sec.IsEnabled = _secAny.IsChecked != true; _hund.IsEnabled = _hundAny.IsChecked != true;
            }
            Current = err == null ? p : null;
            _preview.Text = p.Format() + "\n" + BitConverter.ToString(p.Encode()).Replace("-", " ");
            _error.Text = err; _error.IsVisible = err != null;
            _ok.IsEnabled = err == null;
        }
    }
}
