using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>
    /// Port of Yabe/Forms/CalendarEditor.cs. Entry point for other areas (e.g. property grid on Date_List):
    ///   await CalendarEditor.Open(device, calendar_object_id [, owner]);
    /// </summary>
    public class CalendarEditor : Window
    {
        readonly BACnetDevice device; readonly BacnetObjectId object_id;
        BACnetCalendarEntry calendarEntries;       // dates in the bacnet object
        string objectName;
        DateTime CalendarStartRequested;
        bool InternalListeEntriesSelect;

        bool isDirty, suppressDirtyTracking;
        const string baseTitle = "Calendar Editor";

        CalendarView calendarView;
        Avalonia.Controls.Calendar dateSelect;
        ListBox listEntries;
        readonly ObservableCollection<object> _entries = new ObservableCollection<object>();
        TextBlock PendingChangesLabel;
        Button btReadWrite, btAdd, btDelete, btCancel, btSave, btLoad;
        bool _syncingDateSelect;

        public static async Task Open(BACnetDevice device, BacnetObjectId objectId, Window owner = null)
        {
            owner = owner ?? Dialogs.Owner;
            try
            {
                var win = new CalendarEditor(device, objectId);
                string err = null; BACnetCalendarEntry entries = default; string name = null;
                await Task.Run(() => { try { (entries, name) = win.ReadCalendar(); } catch (Exception ex) { err = ex.Message; } });
                if (err != null) { await Dialogs.Message("Failed to read the calendar from the device. " + err, "Error reading calendar", owner); return; }
                win.RunWithDirtyTrackingSuppressed(() => win.ApplyCalendar(entries, name));
                await win.ShowDialog(owner);
            }
            catch (Exception ex) { MainWindow.Instance?.Log("Error loading Calendar : " + ex.Message); }
        }

        void MarkDirty() { if (suppressDirtyTracking || isDirty) return; isDirty = true; UpdateTitle(); }
        void ClearDirty() { if (!isDirty) return; isDirty = false; UpdateTitle(); }
        void UpdateTitle()
        {
            PendingChangesLabel.IsVisible = isDirty;
            Title = isDirty ? "* " + baseTitle : baseTitle;
            if (!string.IsNullOrWhiteSpace(objectName)) Title += " - " + objectName;
        }
        void RunWithDirtyTrackingSuppressed(Action a)
        {
            bool prev = suppressDirtyTracking; suppressDirtyTracking = true;
            try { a(); } finally { suppressDirtyTracking = prev; }
        }

        CalendarEditor(BACnetDevice device, BacnetObjectId object_id)
        {
            this.device = device; this.object_id = object_id;
            Title = baseTitle;
            Width = 800; Height = 460; MinWidth = 600; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            calendarEntries.Entries = new List<object>();
            BuildUi();
        }

        void BuildUi()
        {
            dateSelect = new Avalonia.Controls.Calendar { SelectedDate = DateTime.Today, HorizontalAlignment = HorizontalAlignment.Left };
            calendarView = new CalendarView();
            listEntries = new ListBox { ItemsSource = _entries, SelectionMode = SelectionMode.Single, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            ScrollViewer.SetHorizontalScrollBarVisibility(listEntries, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);   // long entries must not be cut off
            var ctx = new ContextMenu();
            var miAdd = new MenuItem { Header = "Add Week of Day" }; miAdd.Click += (_, __) => AddWeekNDay();
            var miDel = new MenuItem { Header = "Delete" }; miDel.Click += (_, __) => DeleteSelected();
            var miMod = new MenuItem { Header = "Modify" }; miMod.Click += (_, __) => Modify();
            ctx.ItemsSource = new[] { miAdd, miDel, miMod };
            listEntries.ContextMenu = ctx;

            btReadWrite = new Button { Content = "Write & Read back", Height = 35, Padding = new Thickness(14, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            btCancel = new Button { Content = "Cancel", Height = 35, MinWidth = 73, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            btAdd = new Button { Content = "Add", MinWidth = 51 };
            btDelete = new Button { Content = "Delete", MinWidth = 51 };
            btSave = new Button { Content = "Save...", MinWidth = 51 };
            btLoad = new Button { Content = "Load...", MinWidth = 51 };
            PendingChangesLabel = new TextBlock { Text = "● Unsaved changes", Foreground = Brushes.DarkOrange, FontWeight = FontWeight.Bold, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

            // left column: month picker, "Date entries :" list, Save/Load
            var leftGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Width = 240, Margin = new Thickness(16, 1, 6, 8) };
            var dateHost = new LayoutTransformControl { LayoutTransform = new ScaleTransform(0.68, 0.68), HorizontalAlignment = HorizontalAlignment.Left, Child = dateSelect };
            var lbl = new TextBlock { Text = "Date entries :", Margin = new Thickness(0, 8, 0, 3) };
            var sl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 6, 0, 0), Children = { btSave, btLoad } };
            Grid.SetRow(lbl, 1); Grid.SetRow(listEntries, 2); Grid.SetRow(sl, 3);
            leftGrid.Children.Add(dateHost); leftGrid.Children.Add(lbl); leftGrid.Children.Add(listEntries); leftGrid.Children.Add(sl);

            // right column: calendar view + button row
            var addDel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { btAdd, btDelete } };
            var rw = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { PendingChangesLabel, btReadWrite, btCancel } };
            var buttons = new Grid { Margin = new Thickness(0, 5, 8, 8), Children = { addDel, rw } };
            var rightDock = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            rightDock.Children.Add(buttons);
            rightDock.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), Child = calendarView });

            var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(rightDock, 1);
            root.Children.Add(leftGrid); root.Children.Add(rightDock);
            Content = root;

            // ---- events
            dateSelect.SelectedDatesChanged += (_, __) =>
            {
                if (_syncingDateSelect || dateSelect.SelectedDate == null) return;
                var d = dateSelect.SelectedDate.Value;
                calendarView.SetSelection(d, d);
                SetCalendarDisplayDate(d);
            };
            dateSelect.DisplayDateChanged += (_, e) =>
            {
                if (_syncingDateSelect) return;
                SetCalendarDisplayDate(dateSelect.DisplayDate);
            };
            listEntries.SelectionChanged += (_, __) => listEntries_SelectedIndexChanged();
            listEntries.DoubleTapped += (_, __) => Modify();
            calendarView.LoadItems += (_, __) => PlaceItemsInCalendarView();
            calendarView.ItemCreated += calendarView_ItemCreated;
            calendarView.ItemDeleted += calendarView_ItemDeleted;
            calendarView.ItemDatesChanged += calendarView_ItemDatesChanged;
            calendarView.ItemDoubleClick += (_, __) => Modify();
            calendarView.ItemSelected += calendarView_ItemSelected;
            btAdd.Click += (_, __) => calendarView.CreateItemOnSelection("", true);
            btDelete.Click += (_, __) => { if (listEntries.SelectedIndex == -1) return; calendarView.DeleteSelectedItems(); };
            btReadWrite.Click += (_, __) => btReadWrite_Click();
            btCancel.Click += (_, __) => Close();
            btSave.Click += async (_, __) => await SaveClick();
            btLoad.Click += async (_, __) => await LoadClick();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }

        // ------------------------------------------------------------------ device access
        (BACnetCalendarEntry, string) ReadCalendar()
        {
            string name = null;
            try
            {
                if (device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_OBJECT_NAME, out IList<BacnetValue> nv) &&
                    nv != null && nv.Count > 0 && nv[0].Value is string s) name = s;
            }
            catch { }

            device.ReadPropertyRequest(object_id, BacnetPropertyIds.PROP_DATE_LIST, out IList<BacnetValue> values);
            var entries = new BACnetCalendarEntry { Entries = new List<object>() };
            if (values != null && values.Count == 1 && values[0].Value != null)
            {
                if (values[0].Value is object[] arr)
                {
                    // An array of date entries is received rather than a BACnetCalendarEntry
                    foreach (var o in arr) entries.Entries.Add(o);
                }
                else if (values[0].Value is BACnetCalendarEntry bce) entries = bce;
            }
            return (entries, name);
        }

        void ApplyCalendar(BACnetCalendarEntry entries, string name)
        {
            calendarEntries = entries;
            if (calendarEntries.Entries == null) calendarEntries.Entries = new List<object>();
            if (name != null) objectName = name;
            UpdateTitle();
            _syncingDateSelect = true;
            dateSelect.SelectedDate = DateTime.Today; dateSelect.DisplayDate = DateTime.Today;
            _syncingDateSelect = false;
            calendarView.SetSelection(DateTime.Today);
            _entries.Clear();
            foreach (object e in calendarEntries.Entries) _entries.Add(e);
            // calendarView is updated by the LoadItems event
            SetCalendarDisplayDate(DateTime.Now);
        }

        async void btReadWrite_Click()
        {
            var list = new List<BacnetValue> { new BacnetValue(calendarEntries) };
            try { await Task.Run(() => device.WritePropertyRequest(object_id, BacnetPropertyIds.PROP_DATE_LIST, list)); }
            catch (Exception ex) { await Dialogs.Message("Failed to write the calendar to the device. " + ex.Message, "Error writing calendar", this); return; }

            try
            {
                var (entries, name) = await Task.Run(() => ReadCalendar());
                RunWithDirtyTrackingSuppressed(() => ApplyCalendar(entries, name));
            }
            catch (Exception ex) { await Dialogs.Message("The write succeeded, but we failed to read the calendar back from the device. " + ex.Message, "Error reading calendar", this); return; }
            ClearDirty();
        }

        // ------------------------------------------------------------------ view
        void SetCalendarDisplayDate(DateTime d)
        {
            DateTime start = new DateTime(d.Year, d.Month, 1);
            DateTime stop = start.AddMonths(1).AddHours(-1);
            CalendarStartRequested = start;
            calendarView.SetViewRange(start, stop);
        }

        void listEntries_SelectedIndexChanged()
        {
            if (InternalListeEntriesSelect || listEntries.SelectedIndex == -1) { InternalListeEntriesSelect = false; return; }
            object o = _entries[listEntries.SelectedIndex];
            if (o is BacnetDateRange bdr)
            {
                if (bdr.startDate.year != 255) SetCalendarDisplayDate(bdr.startDate.toDateTime());
                else if (bdr.endDate.year != 255) SetCalendarDisplayDate(bdr.endDate.toDateTime().AddDays(-10));
            }
            else if (o is BacnetDate bd)
            {
                if (!bd.IsPeriodic) SetCalendarDisplayDate(bd.toDateTime());
            }
            SelectInView(o);
        }

        void SelectInView(object tag)
        {
            var it = calendarView.Items.FirstOrDefault(i => Equals(i.Tag, tag));
            if (it != null) calendarView.SelectItem(it, false);
        }

        void AddCalendarEntry(DateTime _start, DateTime _end, Color color, string text, object tag)
        {
            var start = new DateTime(_start.Year, _start.Month, _start.Day, 0, 0, 0);
            var end = new DateTime(_end.Year, _end.Month, _end.Day, 23, 59, 59);
            var ci = new CalendarViewItem(start, end, text);
            ci.ApplyColor(color); ci.Tag = tag;
            var days = calendarView.Days;
            if (start <= days[days.Length - 1].Date && days[0].Date <= end) calendarView.Items.Add(ci);
        }

        void PlaceItemsInCalendarView()
        {
            var days = calendarView.Days;
            if (days.Length == 0 || calendarEntries.Entries == null) return;
            foreach (object e in calendarEntries.Entries)
            {
                if (e is BacnetDate bd)
                {
                    if (bd.IsPeriodic)
                    { foreach (var dt in days) if (bd.IsAFittingDate(dt)) AddCalendarEntry(dt, dt, Colors.Blue, "Periodic", bd); }
                    else AddCalendarEntry(bd.toDateTime(), bd.toDateTime(), Colors.Green, "", bd);
                }
                else if (e is BacnetDateRange bdr)
                {
                    DateTime start, end;
                    start = bdr.startDate.year != 255 ? new DateTime(bdr.startDate.year + 1900, bdr.startDate.month, bdr.startDate.day, 0, 0, 0) : DateTime.MinValue;
                    end = bdr.endDate.year != 255 ? new DateTime(bdr.endDate.year + 1900, bdr.endDate.month, bdr.endDate.day, 23, 59, 59) : DateTime.MaxValue;
                    var ci = new CalendarViewItem(start, end, "");
                    ci.ApplyColor(Colors.Yellow); ci.Tag = bdr;
                    if (start <= days[days.Length - 1].Date && days[0].Date <= end) calendarView.Items.Add(ci);
                }
                else if (e is BacnetweekNDay bwnd)
                {
                    foreach (var dt in days) if (bwnd.IsAFittingDate(dt)) AddCalendarEntry(dt, dt, Colors.Red, "Periodic", bwnd);
                }
            }
        }

        int IndexOfEntry(object o) { for (int i = 0; i < _entries.Count; i++) if (Equals(_entries[i], o)) return i; return -1; }

        void calendarView_ItemDeleted(object sender, CalendarViewItemEventArgs e)
        {
            calendarEntries.Entries.Remove(e.Item.Tag);
            int i = IndexOfEntry(e.Item.Tag); if (i >= 0) _entries.RemoveAt(i);
            SetCalendarDisplayDate(CalendarStartRequested);
            MarkDirty();
        }

        void calendarView_ItemSelected(object sender, CalendarViewItemEventArgs e)
        {
            int idx = IndexOfEntry(e.Item.Tag);
            InternalListeEntriesSelect = true;
            listEntries.SelectedIndex = idx;
            InternalListeEntriesSelect = false;
        }

        static object EntryFor(DateTime s, DateTime e)
        {
            if (s.Year == e.Year && s.Month == e.Month && s.Day == e.Day)
                return new BacnetDate((byte)(s.Year - 1900), (byte)s.Month, (byte)s.Day);
            return new BacnetDateRange(new BacnetDate((byte)(s.Year - 1900), (byte)s.Month, (byte)s.Day), new BacnetDate((byte)(e.Year - 1900), (byte)e.Month, (byte)e.Day));
        }

        void calendarView_ItemCreated(object sender, CalendarViewItemEventArgs e)
        {
            object n = EntryFor(e.Item.StartDate, e.Item.EndDate);
            _entries.Add(n); calendarEntries.Entries.Add(n);
            SetCalendarDisplayDate(CalendarStartRequested);
            MarkDirty();
        }

        async void calendarView_ItemDatesChanged(object sender, CalendarViewItemEventArgs e)
        {
            object o = e.Item.Tag;
            if ((o is BacnetDate d && d.IsPeriodic) || o is BacnetweekNDay)
            {
                // Cannot do that with periodic element: what has to be changed: day n, wday, month ????
                SetCalendarDisplayDate(CalendarStartRequested);
                await ModifyEntry(o);
                return;
            }
            calendarEntries.Entries.Remove(o);
            int idx = IndexOfEntry(o);
            if (idx >= 0) _entries.RemoveAt(idx);
            object n = EntryFor(e.Item.StartDate, e.Item.EndDate);
            _entries.Insert(Math.Max(idx, 0), n); calendarEntries.Entries.Add(n);
            MarkDirty();
            SetCalendarDisplayDate(CalendarStartRequested);
        }

        // ------------------------------------------------------------------ list editing
        void DeleteSelected()
        {
            try
            {
                if (listEntries.SelectedIndex < 0) return;
                calendarEntries.Entries.Remove(listEntries.SelectedItem);
                _entries.RemoveAt(listEntries.SelectedIndex);
                MarkDirty();
            }
            catch { }
            SetCalendarDisplayDate(CalendarStartRequested);
        }

        async void AddWeekNDay()
        {
            var bwd = new BacnetweekNDay(1, 1);
            var edit = new CalendarEntryEdit(bwd);
            await edit.ShowDialog(this);
            if (edit.OutOK)
            {
                object o = edit.GetBackEntry();
                _entries.Add(o); calendarEntries.Entries.Add(o);
                SetCalendarDisplayDate(CalendarStartRequested);
                MarkDirty();
            }
        }

        async void Modify() { if (listEntries.SelectedIndex == -1) return; await ModifyEntry(listEntries.SelectedItem); }

        async Task ModifyEntry(object selected)
        {
            int idx = IndexOfEntry(selected);
            if (idx < 0) return;
            try
            {
                var edit = new CalendarEntryEdit(selected);
                await edit.ShowDialog(this);
                if (edit.OutOK)
                {
                    object o = edit.GetBackEntry();
                    calendarEntries.Entries.Remove(selected);
                    idx = IndexOfEntry(selected);
                    if (idx >= 0) _entries.RemoveAt(idx); else idx = _entries.Count;
                    _entries.Insert(Math.Min(idx, _entries.Count), o);
                    calendarEntries.Entries.Add(o);
                    MarkDirty();
                }
            }
            catch { }
            SetCalendarDisplayDate(CalendarStartRequested);
        }

        // ------------------------------------------------------------------ Save / Load (.dat, line based, same format as the original)
        const string CalendarFileHeader = "YABE-CALENDAR-V1";

        async Task SaveClick()
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save calendar state", DefaultExtension = "dat",
                SuggestedFileName = object_id.type.ToString().Replace("OBJECT_", "") + "_" + object_id.instance + ".dat",
                FileTypeChoices = new[] { new FilePickerFileType("Calendar state (*.dat)") { Patterns = new[] { "*.dat" } }, FilePickerFileTypes.All }
            });
            if (f == null) return;
            try { SaveCalendarToFile(f.Path.LocalPath); }
            catch (Exception ex) { await Dialogs.Message("Failed to save the calendar state to file. " + ex.Message, "Error saving file", this); }
        }

        async Task LoadClick()
        {
            var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load calendar state", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Calendar state (*.dat)") { Patterns = new[] { "*.dat" } }, FilePickerFileTypes.All }
            });
            if (r.Count == 0) return;
            try { await LoadCalendarFromFile(r[0].Path.LocalPath); }
            catch (Exception ex) { await Dialogs.Message("Failed to load the calendar state from file. " + ex.Message, "Error loading file", this); }
        }

        void SaveCalendarToFile(string path)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(CalendarFileHeader);
            if (calendarEntries.Entries != null)
                foreach (object entry in calendarEntries.Entries)
                {
                    if (entry is BacnetDate bd)
                        sb.AppendLine("DATE " + bd.year.ToString(ci) + " " + bd.month.ToString(ci) + " " + bd.day.ToString(ci) + " " + bd.wday.ToString(ci));
                    else if (entry is BacnetDateRange bdr)
                        sb.AppendLine("RANGE " +
                            bdr.startDate.year.ToString(ci) + " " + bdr.startDate.month.ToString(ci) + " " + bdr.startDate.day.ToString(ci) + " " + bdr.startDate.wday.ToString(ci) + " " +
                            bdr.endDate.year.ToString(ci) + " " + bdr.endDate.month.ToString(ci) + " " + bdr.endDate.day.ToString(ci) + " " + bdr.endDate.wday.ToString(ci));
                    else if (entry is BacnetweekNDay bwnd)
                        sb.AppendLine("WEEKNDAY " + bwnd.month.ToString(ci) + " " + bwnd.week.ToString(ci) + " " + bwnd.wday.ToString(ci));
                }
            File.WriteAllText(path, sb.ToString());
        }

        async Task LoadCalendarFromFile(string path)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != CalendarFileHeader)
                throw new Exception("This file is not a recognized Yabe calendar state file.");
            var ci = CultureInfo.InvariantCulture;
            var parsed = new List<object>(); var errors = new List<string>();
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    if (p[0].Equals("DATE", StringComparison.OrdinalIgnoreCase) && p.Length >= 5)
                        parsed.Add(new BacnetDate(byte.Parse(p[1], ci), byte.Parse(p[2], ci), byte.Parse(p[3], ci), byte.Parse(p[4], ci)));
                    else if (p[0].Equals("RANGE", StringComparison.OrdinalIgnoreCase) && p.Length >= 9)
                        parsed.Add(new BacnetDateRange(
                            new BacnetDate(byte.Parse(p[1], ci), byte.Parse(p[2], ci), byte.Parse(p[3], ci), byte.Parse(p[4], ci)),
                            new BacnetDate(byte.Parse(p[5], ci), byte.Parse(p[6], ci), byte.Parse(p[7], ci), byte.Parse(p[8], ci))));
                    else if (p[0].Equals("WEEKNDAY", StringComparison.OrdinalIgnoreCase) && p.Length >= 4)
                        // BacnetweekNDay(byte wday, byte month, byte week); line order is "month week wday".
                        parsed.Add(new BacnetweekNDay(byte.Parse(p[3], ci), byte.Parse(p[1], ci), byte.Parse(p[2], ci)));
                    else errors.Add(line);
                }
                catch { errors.Add(line); }
            }
            RunWithDirtyTrackingSuppressed(() =>
            {
                calendarEntries = new BACnetCalendarEntry { Entries = parsed };
                _entries.Clear();
                foreach (object entry in parsed) _entries.Add(entry);
                SetCalendarDisplayDate(DateTime.Now);
            });
            MarkDirty();   // a local change loaded from file, not yet on the device
            if (errors.Count > 0)
                await Dialogs.Message("The file loaded, but some lines could not be parsed and were skipped:\n\n" + string.Join("\n", errors), "Some entries skipped", this);
        }

        // =====================================================================================
        /// <summary>Port of CalendarEntryEdit ("Calendar Entry" dialog): edits a BacnetDate, BacnetDateRange or BacnetweekNDay.</summary>
        public class CalendarEntryEdit : Window
        {
            public bool OutOK = false;
            readonly object Entry;
            readonly CalendarDatePicker startDate, endDate;       // null date = unspecified (year 255)
            readonly TextBox year;
            readonly ComboBox month, day, week, wday;

            public CalendarEntryEdit(object entry)
            {
                Entry = entry;
                Title = "Calendar Entry";
                SizeToContent = SizeToContent.WidthAndHeight; CanResize = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;

                var fi = CultureInfo.CurrentCulture.DateTimeFormat;
                wday = new ComboBox { Width = 110 };
                for (int i = 1; i < 7; i++) wday.Items.Add(fi.DayNames[i]);
                wday.Items.Add(fi.DayNames[0]); wday.Items.Add("****");
                day = new ComboBox { Width = 76 };
                for (int i = 1; i < 32; i++) day.Items.Add(i);
                day.Items.Add("Last"); day.Items.Add("**");
                month = new ComboBox { Width = 120 };
                for (int i = 0; i < 12; i++) month.Items.Add(fi.MonthNames[i]);
                month.Items.Add("Odd"); month.Items.Add("Even"); month.Items.Add("****");
                week = new ComboBox { Width = 190 };
                foreach (var w in new[] { "days 1 to 7", "days 8 to 14", "days 15 to 21", "days 22 to 28", "days 29 to 31", "last 7 days of the month", "all days numbers" }) week.Items.Add(w);
                year = new TextBox { Width = 64 };
                startDate = new CalendarDatePicker { Width = 150, Watermark = "(any)" };
                endDate = new CalendarDatePicker { Width = 150, Watermark = "(any)" };

                Control body;
                if (entry is BacnetDateRange bdr)
                {
                    startDate.SelectedDate = bdr.startDate.year != 255 ? bdr.startDate.toDateTime() : (DateTime?)null;
                    endDate.SelectedDate = bdr.endDate.year != 255 ? bdr.endDate.toDateTime() : (DateTime?)null;
                    body = Group(new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 24, Children =
                        {
                            new StackPanel { Children = { new TextBlock { Text = "Start" }, startDate } },
                            new StackPanel { Children = { new TextBlock { Text = "End" }, endDate } },
                        }
                    });
                }
                else
                {
                    SetCombo(wday, entry is BacnetDate d0 ? d0.wday : ((BacnetweekNDay)entry).wday, 7, 7);
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(wday);
                    if (entry is BacnetDate bd)
                    {
                        SetCombo(day, bd.day, 32, 32);
                        SetCombo(month, bd.month, 14, 14);
                        year.Text = bd.year != 255 ? (bd.year + 1900).ToString() : "****";
                        row.Children.Add(day); row.Children.Add(month); row.Children.Add(year);
                    }
                    else
                    {
                        var bw = (BacnetweekNDay)entry;
                        SetCombo(month, bw.month, 14, 14);
                        week.SelectedIndex = (bw.week < 7 && bw.week != 0) ? bw.week - 1 : 6;
                        row.Children.Add(week); row.Children.Add(month);
                    }
                    body = Group(row);
                }

                var ok = new Button { Content = "OK", Width = 76, Height = 29, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                var cancel = new Button { Content = "Cancel", Width = 76, Height = 29, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                ok.Click += (_, __) => { OutOK = true; Close(); };
                cancel.Click += (_, __) => Close();
                KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
                Content = new StackPanel
                {
                    Margin = new Thickness(12), Spacing = 14,
                    Children = { body, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 40, HorizontalAlignment = HorizontalAlignment.Center, Children = { ok, cancel } } }
                };
            }

            static Control Group(Control c) => new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(14, 12), Child = c };

            // value 255 (or out of range) -> index of the "any" item
            static void SetCombo(ComboBox cb, byte v, int anyIndex, int dummy)
            {
                int idx = (v != 255 && v >= 1 && v - 1 < cb.ItemCount - 1) ? v - 1 : anyIndex;
                // Odd/Even for month use 13/14 -> index 12/13 ; "any" is the last item
                if (cb.ItemCount > 0 && idx >= cb.ItemCount) idx = cb.ItemCount - 1;
                cb.SelectedIndex = idx;
            }

            public object GetBackEntry()
            {
                if (Entry is BacnetDate)
                {
                    var bd = new BacnetDate();
                    bd.wday = wday.SelectedIndex == 7 ? (byte)255 : (byte)(wday.SelectedIndex + 1);
                    bd.day = day.SelectedIndex == 32 ? (byte)255 : (byte)(day.SelectedIndex + 1);
                    bd.month = month.SelectedIndex == 14 ? (byte)255 : (byte)(month.SelectedIndex + 1);
                    int valyear = 255;
                    try { valyear = Convert.ToInt32(year.Text) - 1900; } catch { }
                    bd.year = (valyear >= 0 && valyear < 255) ? (byte)valyear : (byte)255;
                    return bd;
                }
                if (Entry is BacnetDateRange)
                {
                    var bdr = new BacnetDateRange();
                    var s = startDate.SelectedDate; var e = endDate.SelectedDate;
                    bdr.startDate = s == null ? new BacnetDate(255, 255, 255) : new BacnetDate((byte)(s.Value.Year - 1900), (byte)s.Value.Month, (byte)s.Value.Day);
                    bdr.endDate = e == null ? new BacnetDate(255, 255, 255) : new BacnetDate((byte)(e.Value.Year - 1900), (byte)e.Value.Month, (byte)e.Value.Day);
                    return bdr;
                }
                if (Entry is BacnetweekNDay)
                {
                    var bwd = (BacnetweekNDay)Entry;
                    bwd.wday = wday.SelectedIndex == 7 ? (byte)255 : (byte)(wday.SelectedIndex + 1);
                    bwd.month = month.SelectedIndex == 14 ? (byte)255 : (byte)(month.SelectedIndex + 1);
                    bwd.week = (byte)(week.SelectedIndex + 1);
                    if (bwd.week == 7) bwd.week = 255;
                    return bwd;
                }
                return null;
            }
        }
    }
}
