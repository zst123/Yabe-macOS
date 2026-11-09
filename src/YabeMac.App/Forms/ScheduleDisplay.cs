using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>
    /// Port of Yabe/Forms/ScheduleDisplay.cs ("Simple Schedule Editor").
    /// Entry point for other areas (property grid on Weekly_Schedule / Exception_Schedule / Effective_Period ...):
    ///   await ScheduleDisplay.Open(device, schedule_object_id [, owner]);
    /// </summary>
    public class ScheduleDisplay : Window
    {
        // ------------------------------------------------------------------ state
        readonly BacnetObjectId schedule_id;
        readonly BACnetDevice device;
        string objectName;
        // Default value type here if no values are already present
        BacnetApplicationTags ScheduleType = BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE;

        bool isDirty, suppressDirtyTracking;
        const string baseTitle = "Simple Schedule Editor";

        TextBox TxtStartDate, TxtEndDate, TxtScheduleDefault;
        ComboBox ScheduleDataType;
        TreeView Schedule;
        ListBox listReferences;
        TextBlock PendingChangesLabel;
        Button UpdateButton, StartDatePicker, EndDatePicker, btSave, btLoad, ScheduleHelpButton, CancelBtn;
        readonly ObservableCollection<SchedNode> _roots = new ObservableCollection<SchedNode>();
        readonly ObservableCollection<RefItem> _refs = new ObservableCollection<RefItem>();
        static List<IEditableNode> NodesToCopy;       // static to permit copy paste between different schedules
        RefItem PropertyReferenceCopy;

        class RefItem
        {
            public BacnetDeviceObjectPropertyReference Ref;
            public string Text;
            public override string ToString() => Text;
        }

        // ------------------------------------------------------------------ public entry point
        /// <summary>Opens the schedule editor for a Schedule object (modal). Reads the device off the UI thread first.</summary>
        public static async Task Open(BACnetDevice device, BacnetObjectId objectId, Window owner = null)
        {
            owner = owner ?? Dialogs.Owner;
            try
            {
                var win = new ScheduleDisplay(device, objectId);
                var snap = await Task.Run(() => win.ReadAll(true));
                win.Apply(snap);
                await win.ShowDialog(owner);
            }
            catch (Exception ex) { MainWindow.Instance?.Log("Error loading Schedule : " + ex.Message); }
        }

        // ------------------------------------------------------------------ dirty tracking
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

        // ------------------------------------------------------------------ construction / layout
        ScheduleDisplay(BACnetDevice device, BacnetObjectId object_id)
        {
            this.device = device; this.schedule_id = object_id;
            Title = baseTitle;
            Width = 526; Height = 775; MinWidth = 392; MinHeight = 469;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            BuildUi();
        }

        static TextBlock Label(string t) => new TextBlock { Text = t, Margin = new Thickness(0, 0, 0, 3) };

        void BuildUi()
        {
            TxtStartDate = new TextBox { Width = 100, Text = "Always", HorizontalAlignment = HorizontalAlignment.Left };
            TxtEndDate = new TextBox { Width = 100, Text = "Always", HorizontalAlignment = HorizontalAlignment.Left };
            TxtScheduleDefault = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            ScheduleDataType = new ComboBox { Width = 132, HorizontalAlignment = HorizontalAlignment.Left };
            for (int i = 1; i < 12; i++) ScheduleDataType.Items.Add(ScheduleValueText.GetNiceName((BacnetApplicationTags)i));
            StartDatePicker = new Button { Content = "\U0001F4C5", Padding = new Thickness(6, 3), Margin = new Thickness(6, 0, 0, 0) };
            EndDatePicker = new Button { Content = "\U0001F4C5", Padding = new Thickness(6, 3), Margin = new Thickness(6, 0, 0, 0) };
            ToolTip.SetTip(TxtStartDate, "The date from which the schedule will take effect," + Environment.NewLine + "or \"Always\" if no start date is specified.");
            ToolTip.SetTip(TxtEndDate, "The date up until which the schedule will take effect," + Environment.NewLine + "or \"Always\" if the schedule should run indefinitely.");

            var dateStart = new StackPanel { Children = { Label("Validity Start Date"), new StackPanel { Orientation = Orientation.Horizontal, Children = { TxtStartDate, StartDatePicker } } } };
            var dateEnd = new StackPanel { Children = { Label("Validity End Date"), new StackPanel { Orientation = Orientation.Horizontal, Children = { TxtEndDate, EndDatePicker } } } };
            var defVal = new StackPanel { Children = { Label("Schedule Default value"), TxtScheduleDefault } };
            var dtype = new StackPanel { Children = { Label("Data Type"), ScheduleDataType } };
            var hdr = new Grid { ColumnDefinitions = new ColumnDefinitions("187,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 0, 0, 0) };
            Grid.SetColumn(dateEnd, 1); Grid.SetRow(defVal, 1); Grid.SetRow(dtype, 1); Grid.SetColumn(dtype, 1);
            defVal.Margin = new Thickness(0, 12, 0, 0); dtype.Margin = new Thickness(0, 12, 0, 0);
            hdr.Children.Add(dateStart); hdr.Children.Add(dateEnd); hdr.Children.Add(defVal); hdr.Children.Add(dtype);

            PendingChangesLabel = new TextBlock { Text = "● Unsaved changes", Foreground = Brushes.DarkOrange, FontWeight = FontWeight.Bold, IsVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ScheduleHelpButton = new Button { Content = "?", FontWeight = FontWeight.Bold, Width = 24, Height = 24, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            var weeklyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 14, 0, 4) };
            var wl = new TextBlock { Text = "Weekly Schedules", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(PendingChangesLabel, 1); Grid.SetColumn(ScheduleHelpButton, 2);
            weeklyRow.Children.Add(wl); weeklyRow.Children.Add(PendingChangesLabel); weeklyRow.Children.Add(ScheduleHelpButton);

            Schedule = new TreeView { SelectionMode = SelectionMode.Multiple, ItemsSource = _roots, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            Schedule.Styles.Add(new Style(x => x.OfType<TreeViewItem>()) { Setters = { new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded", BindingMode.TwoWay)) } });
            Schedule.ItemTemplate = new FuncTreeDataTemplate<SchedNode>((n, _) => BuildNodeView(n), n => n.Children);
            Schedule.ContextMenu = BuildContextMenu(true);

            listReferences = new ListBox { ItemsSource = _refs, Height = 80, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), SelectionMode = SelectionMode.Single };
            listReferences.ContextMenu = BuildContextMenu(false);
            listReferences.DoubleTapped += (_, __) => { if (listReferences.SelectedItem != null) ModifyReference(); };
            var refLabel = new TextBlock { Text = "Object Property References", Margin = new Thickness(0, 10, 0, 3) };

            btSave = new Button { Content = "Save...", Margin = new Thickness(0, 0, 4, 0) };
            btLoad = new Button { Content = "Load..." };
            UpdateButton = new Button { Content = "Update & Read back", Margin = new Thickness(0, 0, 8, 0) };
            CancelBtn = new Button { Content = "Cancel", MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { btSave, btLoad } };
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { UpdateButton, CancelBtn } };
            var btnRow = new Grid { Margin = new Thickness(0, 6, 0, 6), Children = { left, right } };
            var tip = new TextBlock { Text = "Tip: Click the (?) button above the schedule list for help on the syntax used to modify schedule entries and schedule exceptions.", TextWrapping = TextWrapping.Wrap };

            var root = new DockPanel { Margin = new Thickness(21, 15, 21, 12) };
            var top = new StackPanel { Children = { hdr, weeklyRow } };
            var bottom = new StackPanel { Children = { refLabel, listReferences, btnRow, tip } };
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(Schedule);
            Content = root;

            // ---- events
            // property observables fire synchronously (TextChanged may be raised late, after the dirty suppression ended)
            foreach (var tb in new[] { TxtStartDate, TxtEndDate, TxtScheduleDefault })
                tb.GetObservable(TextBox.TextProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<string>(_ => MarkDirty()));
            foreach (var tb in new[] { TxtStartDate, TxtEndDate })
            {
                var t = tb;
                t.LostFocus += (_, __) => TxtDate_Validated(t);
                t.KeyDown += (_, e) => { if (e.Key == Key.Enter) TxtDate_Validated(t); };
            }
            StartDatePicker.Click += async (_, __) => TxtStartDate.Text = await DateTimePicker(TxtStartDate.Text);
            EndDatePicker.Click += async (_, __) => TxtEndDate.Text = await DateTimePicker(TxtEndDate.Text);
            ScheduleDataType.SelectionChanged += (_, __) =>
            {
                if (ScheduleDataType.SelectedIndex < 0) return;
                ScheduleType = (BacnetApplicationTags)(ScheduleDataType.SelectedIndex + 1);
                MarkDirty();
            };
            ScheduleHelpButton.Click += async (_, __) => await ShowHelp();
            UpdateButton.Click += (_, __) => Update_Click();
            CancelBtn.Click += (_, __) => Close();
            btSave.Click += async (_, __) => await SaveClick();
            btLoad.Click += async (_, __) => await LoadClick();
            Schedule.DoubleTapped += (_, e) => { if (!(e.Source is TextBox)) ModifyTree(); };
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);
            KeyBindingEscape();
            isDirty = false;   // the initial text assignments above are not user edits
        }

        void KeyBindingEscape() { KeyDown += (_, e) => { if (e.Key == Key.Escape && !(e.Source is TextBox)) Close(); }; }

        Control BuildNodeView(SchedNode n)
        {
            var icon = new TextBlock { Text = n.Icon, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.Bind(TextBlock.TextProperty, new Binding("Text"));
            var edit = new TextBox { MinWidth = 380, IsVisible = false, Padding = new Thickness(4, 1), MinHeight = 0 };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, DataContext = n, Children = { icon, text, edit } };
            void Sync()
            {
                text.IsVisible = !n.IsEditing; edit.IsVisible = n.IsEditing;
                if (n.IsEditing)
                {
                    edit.Text = n.EditText;
                    Dispatcher.UIThread.Post(() => { edit.Focus(); edit.SelectAll(); }, DispatcherPriority.Background);
                }
            }
            System.ComponentModel.PropertyChangedEventHandler h = (_, e) => { if (e.PropertyName == nameof(SchedNode.IsEditing)) Sync(); };
            n.PropertyChanged += h;
            panel.DetachedFromVisualTree += (_, __) => n.PropertyChanged -= h;
            edit.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; await CommitEdit(n, edit.Text); }
                else if (e.Key == Key.Escape) { e.Handled = true; n.IsEditing = false; Schedule.Focus(); }
            };
            edit.LostFocus += async (_, __) => { if (n.IsEditing) await CommitEdit(n, edit.Text); };
            return panel;
        }

        // ------------------------------------------------------------------ context menu / shortcuts
        ContextMenu BuildContextMenu(bool tree)
        {
            MenuItem Mi(string header, string gesture, Action a)
            {
                var m = new MenuItem { Header = header, InputGesture = gesture == null ? null : KeyGesture.Parse(gesture) };
                m.Click += (_, __) => a();
                return m;
            }
            return new ContextMenu
            {
                ItemsSource = new object[]
                {
                    Mi("Modify", null, () => { if (tree) ModifyTree(); else ModifyReference(); }),
                    Mi("Delete", "Delete", () => Delete(tree)),
                    Mi("Add", "Insert", () => Add(tree)),
                    Mi("Copy", "Ctrl+C", () => Copy(tree)),
                    Mi("Paste", "Ctrl+V", () => Paste(tree)),
                }
            };
        }

        void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || e.Source is TextBox) return;
            bool tree = Schedule.IsKeyboardFocusWithin;
            if (!tree && !listReferences.IsKeyboardFocusWithin) return;
            bool cmd = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (e.Key == Key.Delete || e.Key == Key.Back) { Delete(tree); e.Handled = true; }
            else if (e.Key == Key.Insert) { Add(tree); e.Handled = true; }
            else if (cmd && e.Key == Key.C) { Copy(tree); e.Handled = true; }
            else if (cmd && e.Key == Key.V) { Paste(tree); e.Handled = true; }
            else if (e.Key == Key.F2 || e.Key == Key.Enter) { if (tree) ModifyTree(); else ModifyReference(); e.Handled = true; }
        }

        // ------------------------------------------------------------------ Reading from the device (runs off the UI thread)
        class Snapshot
        {
            public string ObjectName;
            public string Start, End;                 // null: not read
            public BacnetApplicationTags Type;
            public string DefaultText;                // null: unchanged
            public bool HasWeekly;
            public List<(DateTime, BacnetValue)>[] Days;
            public bool HasException;
            public List<(object spec, uint prio, List<(DateTime, BacnetValue)> tvs)> Exceptions = new List<(object, uint, List<(DateTime, BacnetValue)>)>();
            public List<BacnetDeviceObjectPropertyReference> Refs = new List<BacnetDeviceObjectPropertyReference>();
            public List<string> Errors = new List<string>();
        }

        Snapshot ReadAll(bool initial)
        {
            var s = new Snapshot { Type = ScheduleType };
            if (initial)
            {
                // Get the Present_Value data Type, used for new value in the schedule
                try
                {
                    if (device.ReadPropertyRequest(schedule_id, BacnetPropertyIds.PROP_PRESENT_VALUE, out IList<BacnetValue> value) &&
                        value != null && value.Count != 0 && value[0].Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL)
                        s.Type = value[0].Tag;
                }
                catch { }
            }
            ReadName(s); ReadEffectivePeriod(s); ReadWeekly(s); ReadReferences(s); ReadExceptionSchedule(s);
            return s;
        }

        void ReadName(Snapshot s)
        {
            try
            {
                if (device.ReadPropertyRequest(schedule_id, BacnetPropertyIds.PROP_OBJECT_NAME, out IList<BacnetValue> values) &&
                    values != null && values.Count > 0 && values[0].Value is string n) s.ObjectName = n;
            }
            catch { }
        }

        void ReadEffectivePeriod(Snapshot s)
        {
            try
            {
                if (device.ReadPropertyRequest(schedule_id, BacnetPropertyIds.PROP_EFFECTIVE_PERIOD, out IList<BacnetValue> value))
                {
                    DateTime dt = (DateTime)value[0].Value;
                    s.Start = dt.Ticks != 0 ? dt.ToString("d") : "Always";   // FF-FF-FF-FF is put into a DateTime as 0 ticks
                    dt = (DateTime)value[1].Value;
                    s.End = dt.Ticks != 0 ? dt.ToString("d") : "Always";
                }
            }
            catch (Exception ex) { s.Errors.Add("Failed to read schedule effective period from the device. " + ex.Message); }
        }

        void ReadReferences(Snapshot s)
        {
            try
            {
                if (device.ReadPropertyRequest(schedule_id, BacnetPropertyIds.PROP_LIST_OF_OBJECT_PROPERTY_REFERENCES, out IList<BacnetValue> value))
                    foreach (BacnetValue bv in value) s.Refs.Add((BacnetDeviceObjectPropertyReference)bv.Value);
            }
            catch { }
        }

        void ReadWeekly(Snapshot s)
        {
            byte[] buf = null;
            try
            {
                device.ReadPropertyRequest(schedule_id, BacnetPropertyIds.PROP_SCHEDULE_DEFAULT, out IList<BacnetValue> def);
                if (def != null && def.Count != 0 && def[0].Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL)
                {
                    s.Type = def[0].Tag;
                    s.DefaultText = def[0].Value.ToString();
                }
                if (device.RawEncodedDecodedPropertyConfirmedRequest(schedule_id, BacnetPropertyIds.PROP_WEEKLY_SCHEDULE, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, ref buf))
                {
                    int offset = 0; byte tag_number; uint len_value_type;
                    offset += ASN1.decode_tag_number(buf, offset, out tag_number);
                    if (tag_number != 3) return;
                    var days = new List<(DateTime, BacnetValue)>[7];
                    for (int i = 0; i < 7; i++)
                    {
                        days[i] = new List<(DateTime, BacnetValue)>();
                        offset += ASN1.decode_tag_number(buf, offset, out tag_number);   // tag 0
                        while (!ASN1.IS_CLOSING_TAG(buf[offset]))
                        {
                            BacnetValue value;
                            offset += ASN1.decode_tag_number_and_value(buf, offset, out tag_number, out len_value_type);
                            offset += ASN1.bacapp_decode_data(buf, offset, buf.Length, (BacnetApplicationTags)tag_number, len_value_type, out value);
                            DateTime dt = (DateTime)value.Value;
                            offset += ASN1.decode_tag_number_and_value(buf, offset, out tag_number, out len_value_type);
                            offset += ASN1.bacapp_decode_data(buf, offset, buf.Length, (BacnetApplicationTags)tag_number, len_value_type, out value);
                            if (value.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL) s.Type = value.Tag;   // all types must be the same for a valid schedule
                            days[i].Add((dt, value));
                        }
                        offset++;
                    }
                    offset += ASN1.decode_tag_number(buf, offset, out tag_number);
                    if (tag_number == 3) { s.Days = days; s.HasWeekly = true; }
                }
            }
            catch (Exception ex) { s.Errors.Add("Failed to read weekly schedule from the device. " + ex.Message); s.HasWeekly = false; }
        }

        // Exception_Schedule: period (calendarReference[1] / calendarEntry[0]) + listOfTimeValues[2] + eventPriority[3]
        void ReadExceptionSchedule(Snapshot s)
        {
            byte[] buf = null;
            try
            {
                if (!device.RawEncodedDecodedPropertyConfirmedRequest(schedule_id, BacnetPropertyIds.PROP_EXCEPTION_SCHEDULE, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, ref buf)) return;
                if (buf == null || buf.Length == 0) return;
                int offset = 0; byte tag_number; uint len_value_type;
                offset += ASN1.decode_tag_number(buf, offset, out tag_number);
                if (tag_number != 3) return;
                s.HasException = true;
                while (!ASN1.IS_CLOSING_TAG(buf[offset]))
                {
                    object spec; byte periodTag;
                    offset += ASN1.decode_tag_number(buf, offset, out periodTag);
                    if (periodTag == 1)
                    {
                        ushort otype; uint oinst;
                        offset += ASN1.decode_object_id(buf, offset, out otype, out oinst);
                        spec = new BacnetObjectId((BacnetObjectTypes)otype, oinst);
                    }
                    else
                    {
                        byte innerTag; uint innerLen;
                        bool inside = ASN1.IS_OPENING_TAG(buf[offset]);
                        offset += ASN1.decode_tag_number_and_value(buf, offset, out innerTag, out innerLen);
                        if (innerTag == 0) { var d = new BacnetDate(); offset += d.ASN1decode(buf, offset, innerLen); spec = d; }
                        else if (innerTag == 2) { var w = new BacnetweekNDay(); offset += w.ASN1decode(buf, offset, innerLen); spec = w; }
                        else if (innerTag == 1)
                        {
                            var r = new BacnetDateRange(); offset += r.ASN1decode(buf, offset, innerLen); spec = r;
                            if (inside) offset += ASN1.decode_tag_number(buf, offset, out tag_number);   // dateRange closing tag [1]
                        }
                        else
                        {
                            spec = "(unknown date format)";
                            offset += DumpNotImplementedDateEntryTypes(buf, offset);
                            if (inside) offset += ASN1.decode_tag_number(buf, offset, out tag_number);
                        }
                        offset += ASN1.decode_tag_number(buf, offset, out tag_number);   // calendarEntry closing tag [0]
                    }

                    offset += ASN1.decode_tag_number(buf, offset, out tag_number);       // opening tag 2
                    var tvs = new List<(DateTime, BacnetValue)>();
                    while (!ASN1.IS_CLOSING_TAG(buf[offset]))
                    {
                        BacnetValue val;
                        offset += ASN1.decode_tag_number_and_value(buf, offset, out tag_number, out len_value_type);
                        offset += ASN1.bacapp_decode_data(buf, offset, buf.Length, (BacnetApplicationTags)tag_number, len_value_type, out val);
                        DateTime dt = (DateTime)val.Value;
                        offset += ASN1.decode_tag_number_and_value(buf, offset, out tag_number, out len_value_type);
                        offset += ASN1.bacapp_decode_data(buf, offset, buf.Length, (BacnetApplicationTags)tag_number, len_value_type, out val);
                        tvs.Add((dt, val));
                    }
                    offset++;   // closing tag 2

                    uint priority;
                    offset += ASN1.decode_tag_number_and_value(buf, offset, out tag_number, out len_value_type);
                    offset += ASN1.decode_unsigned(buf, offset, len_value_type, out priority);
                    s.Exceptions.Add((spec, priority, tvs));
                }
            }
            catch (Exception ex) { s.Errors.Add("Failed to read exception schedule from the device. " + ex.Message); }
        }

        static int DumpNotImplementedDateEntryTypes(byte[] buffer, int offset)
        {
            int len = 0;
            while (!ASN1.IS_CLOSING_TAG(buffer[offset + len]))
            {
                len += ASN1.decode_tag_number_and_value(buffer, offset + len, out byte _, out uint innerLen);
                len += (int)innerLen;
            }
            return len;
        }

        /// <summary>Applies a snapshot to the controls (UI thread). Does not touch the dirty state.</summary>
        void Apply(Snapshot s)
        {
            RunWithDirtyTrackingSuppressed(() =>
            {
                if (s.ObjectName != null) objectName = s.ObjectName;
                ScheduleType = s.Type;
                if (s.Start != null) { TxtStartDate.Text = s.Start; TxtEndDate.Text = s.End; }
                if (s.DefaultText != null) TxtScheduleDefault.Text = s.DefaultText;

                _roots.Clear();
                Func<BacnetApplicationTags> typeOf = () => ScheduleType;
                if (s.HasWeekly)
                    for (int i = 0; i < 7; i++)
                    {
                        var daily = new DailySchedule(typeOf, i);
                        _roots.Add(daily.Node);
                        foreach (var (t, v) in s.Days[i]) daily.AddEntry(t, v);
                        daily.SortEntries();
                    }
                if (s.HasException)
                {
                    var ex = new ScheduleExceptionCollection(typeOf);
                    _roots.Add(ex.Node);
                    foreach (var (spec, prio, tvs) in s.Exceptions) ex.AddException(spec, prio, tvs);
                }
                ExpandAll();

                _refs.Clear();
                foreach (var r in s.Refs) AddPropertyRefentry(r, -1, false);

                int idx = (int)ScheduleType - 1;
                ScheduleDataType.SelectedIndex = idx >= 0 && idx < ScheduleDataType.ItemCount ? idx : -1;
                UpdateTitle();
            });
            if (s.Errors.Count > 0) _ = Dialogs.Message(string.Join("\n", s.Errors), "Error reading from device", this);
        }

        void ExpandAll() { foreach (var r in _roots) { r.IsExpanded = true; foreach (var c in r.Children) c.IsExpanded = true; } }

        // ------------------------------------------------------------------ Writing to the device (UI-thread snapshot, then off-thread)
        class WriteJob
        {
            public string Start, End, Default; public BacnetApplicationTags Type;
            public List<ScheduleEntry>[] Days; public List<ScheduleException> Exceptions;
            public List<BacnetDeviceObjectPropertyReference> Refs;
        }

        List<string> WriteAll(WriteJob j)
        {
            var errors = new List<string>();
            WriteEffectivePeriod(j, errors);
            WriteWeekly(j, errors);
            WriteReferences(j, errors);
            WriteExceptionSchedule(j, errors);
            return errors;
        }

        void WriteEffectivePeriod(WriteJob j, List<string> errors)
        {
            try
            {
                EncodeBuffer b = device.channel.GetEncodeBuffer(0);
                ASN1.encode_opening_tag(b, 3);
                DateTime dt = !j.Start.Equals("Always", StringComparison.OrdinalIgnoreCase) ? Convert.ToDateTime(j.Start) : new DateTime(0);
                ASN1.encode_application_date(b, dt);
                dt = !j.End.Equals("Always", StringComparison.OrdinalIgnoreCase) ? Convert.ToDateTime(j.End) : new DateTime(0);
                ASN1.encode_application_date(b, dt);
                ASN1.encode_closing_tag(b, 3);
                Array.Resize(ref b.buffer, b.offset);
                byte[] io = b.buffer;
                device.RawEncodedDecodedPropertyConfirmedRequest(schedule_id, BacnetPropertyIds.PROP_EFFECTIVE_PERIOD, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, ref io);
            }
            catch (Exception ex) { errors.Add("Failed to write schedule effective period to the device. " + ex.Message); }
        }

        void WriteWeekly(WriteJob j, List<string> errors)
        {
            try
            {
                var bv = new BacnetValue[] { ScheduleValueText.Deserialize(j.Default, j.Type) };
                if (!device.WritePropertyRequest(schedule_id, BacnetPropertyIds.PROP_SCHEDULE_DEFAULT, bv))
                {
                    errors.Add("Failed to write default schedule value to the device.");
                    return;
                }
            }
            catch (Exception ex) { errors.Add("Failed to write default schedule value to the device. " + ex.Message); return; }

            try
            {
                EncodeBuffer b = device.channel.GetEncodeBuffer(0);
                ASN1.encode_opening_tag(b, 3);
                for (int i = 0; i < 7; i++)
                {
                    ASN1.encode_opening_tag(b, 0);
                    if (j.Days[i] != null) foreach (var e in j.Days[i]) e.Encode(b);
                    ASN1.encode_closing_tag(b, 0);
                }
                ASN1.encode_closing_tag(b, 3);
                Array.Resize(ref b.buffer, b.offset);
                byte[] io = b.buffer;
                device.RawEncodedDecodedPropertyConfirmedRequest(schedule_id, BacnetPropertyIds.PROP_WEEKLY_SCHEDULE, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, ref io);
            }
            catch (Exception ex) { errors.Add("Failed to write weekly schedule to the device. " + ex.Message); }
        }

        void WriteReferences(WriteJob j, List<string> errors)
        {
            var values = new List<BacnetValue>();
            foreach (var r in j.Refs) values.Add(new BacnetValue(r));
            try { device.WritePropertyRequest(schedule_id, BacnetPropertyIds.PROP_LIST_OF_OBJECT_PROPERTY_REFERENCES, values); }
            catch (Exception ex) { errors.Add("Failed to write schedule object property references to the device. " + ex.Message); }
        }

        void WriteExceptionSchedule(WriteJob j, List<string> errors)
        {
            if (j.Exceptions == null) return;   // device didn't expose an Exception_Schedule - nothing to write
            EncodeBuffer b = device.channel.GetEncodeBuffer(0);
            ASN1.encode_opening_tag(b, 3);
            try { foreach (var e in j.Exceptions) e.Encode(b); }
            catch (Exception ex) { errors.Add("Failed to encode schedule exceptions. " + ex.Message); return; }
            ASN1.encode_closing_tag(b, 3);
            try
            {
                Array.Resize(ref b.buffer, b.offset);
                byte[] io = b.buffer;
                device.RawEncodedDecodedPropertyConfirmedRequest(schedule_id, BacnetPropertyIds.PROP_EXCEPTION_SCHEDULE, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, ref io);
            }
            catch (Exception ex) { errors.Add("Failed to write schedule exceptions to the device. " + ex.Message); }
        }

        WriteJob CaptureWriteJob()
        {
            var j = new WriteJob
            {
                Start = TxtStartDate.Text ?? "Always", End = TxtEndDate.Text ?? "Always", Default = TxtScheduleDefault.Text ?? "", Type = ScheduleType,
                Days = new List<ScheduleEntry>[7], Refs = _refs.Select(r => r.Ref).ToList()
            };
            for (int i = 0; i < 7; i++)
                if (i < _roots.Count && _roots[i].Tag is DailySchedule d) j.Days[i] = d.Entries.ToList();
            if (_roots.Count > 7 && _roots[7].Tag is ScheduleExceptionCollection ec) j.Exceptions = ec.Exceptions.ToList();
            return j;
        }

        async void Update_Click()
        {
            var controls = new Control[] { Schedule, UpdateButton, listReferences, StartDatePicker, EndDatePicker, TxtStartDate, TxtEndDate, TxtScheduleDefault, ScheduleDataType, btSave, btLoad };
            Cursor = new Cursor(StandardCursorType.Wait);
            foreach (var c in controls) c.IsEnabled = false;
            try
            {
                var job = CaptureWriteJob();
                var (errors, snap) = await Task.Run(() =>
                {
                    var err = WriteAll(job);
                    var sn = ReadAll(false);
                    sn.Errors.InsertRange(0, err);
                    return (err, sn);
                });
                Apply(snap);
                ClearDirty();   // whatever's showing now came straight back off the device
            }
            finally
            {
                foreach (var c in controls) c.IsEnabled = true;
                Cursor = Cursor.Default;
            }
        }

        // ------------------------------------------------------------------ date fields
        void TxtDate_Validated(TextBox t)
        {
            if (string.Equals(t.Text, "Always", StringComparison.OrdinalIgnoreCase)) return;
            try { Convert.ToDateTime(t.Text); } catch { t.Text = "Always"; }
        }

        // Date picker: calendar window "Select and close"; ESC aborts
        async Task<string> DateTimePicker(string datestr)
        {
            var cal = new Avalonia.Controls.Calendar { Margin = new Thickness(10) };
            try { var dt = Convert.ToDateTime(datestr); cal.SelectedDate = dt; cal.DisplayDate = dt; } catch { cal.SelectedDate = DateTime.Today; }
            bool abort = false;
            var win = new Window { Title = "Select and close", Content = cal, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) { abort = true; win.Close(); } else if (e.Key == Key.Enter) win.Close(); };
            await win.ShowDialog(this);
            if (abort || cal.SelectedDate == null) return datestr;
            return cal.SelectedDate.Value.ToString("d");
        }

        // ------------------------------------------------------------------ help
        async Task ShowHelp()
        {
            string helpText = @"General

Both the Weekly Schedules (Monday..Sunday) and the Schedule Exceptions can be added, modified, copied/pasted and deleted directly in the tree above:
    - Add:     Insert key, or right-click > Add (adds to whichever day/list is selected)
    - Modify:  double-click a line, or Enter, or right-click > Modify
    - Copy:    Ctrl+C          Paste:   Ctrl+V
    - Delete:  Delete key, or right-click > Delete

Typing tips that apply everywhere below: times may be 24-hour (""19:00:00"") or 12-hour with am/pm (""7:00:00 PM""); case doesn't matter (""AM""/""am"" are the same) and spaces around ""="" or before am/pm are optional.


Weekly Schedule entries (one line per day)

    hh:mm:ss = <value>
    hh:mm:ss = null                (clears the value at that time)

Examples:
    07:00:00 am = 1
    19:00:00 = 0
    12:00:00 pm=75.5


Schedule Exception entries

    <date specifier>, priority=<1-16>, hh:mm:ss=<value> [, hh:mm:ss=<value> ...]

The <date specifier> can be any one of:

    A single date - dd/mm/yyyy (use ** for ""any"", e.g. 25/12/**** for every year):
        25/12/2026

    A date range - ""From"" is optional:
        From 24/12/**** to 02/01/****

    A calendar object reference - <ObjectType>:<instance>:
        CALENDAR:0

    A day, week-of-month and month clause. The day may be a day name or ""Every day""
    (or left out entirely, which means every day). The week-of-month part is optional and
    may be ""Days 1-7""/""Days 8-14""/""Days 15-21""/""Days 22-28"", or the equivalent
    ""Week 1""/""Week 2""/""Week 3""/""Week 4"", or ""last week""/""the last week"" for the
    last 7 days of the month - with or without brackets. The month may be a month name,
    or ""every month""/""odd months""/""even months"":
        Monday (Days 1-7) in September
        Friday Week 3 in September
        the last week of September
        Every day during odd months

Examples:
    25/12/2026, priority=16, 12:00:00 am=0
    From 24/12/**** to 02/01/****, priority=16, 12:00:00 am=0
    01/08/2026, priority=16, 07:00:00 am=1, 1:00:00 pm=0
    Monday (Days 1-7) in September, priority=16, 12:00:00 am=0
    CALENDAR:0, priority=16, 12:00:00 am=1
";
            var box = new TextBox { Text = helpText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), BorderThickness = new Thickness(0) };
            var ok = new Button { Content = "OK", MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            var dock = new DockPanel();
            DockPanel.SetDock(ok, Dock.Bottom);
            dock.Children.Add(ok); dock.Children.Add(new ScrollViewer { Content = box, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
            var win = new Window { Title = "Schedule entry syntax help", Width = 695, Height = 450, Content = dock, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            ok.Click += (_, __) => win.Close();
            await win.ShowDialog(this);
        }

        // ------------------------------------------------------------------ tree editing
        IEnumerable<SchedNode> SelectedNodes() => Schedule.SelectedItems.OfType<SchedNode>().ToList();
        static bool IsEditableNode(SchedNode n, out IEditableNode e) { e = n?.Tag as IEditableNode; return e != null; }
        static bool IsEditableNodeParent(SchedNode n, out IEditableNodeParent e) { e = n?.Tag as IEditableNodeParent; return e != null; }

        HashSet<IEditableNodeParent> GetSelectedParentNodes()
        {
            var set = new HashSet<IEditableNodeParent>();
            foreach (var t in SelectedNodes())
            {
                if (IsEditableNode(t, out var en)) set.Add(en.Parent);
                else if (IsEditableNodeParent(t, out var ep)) set.Add(ep);
            }
            return set;
        }

        HashSet<IEditableNode> GetSelectedEditableNodes()
        {
            var set = new HashSet<IEditableNode>();
            foreach (var t in SelectedNodes())
            {
                if (IsEditableNode(t, out var en)) set.Add(en);
                else if (IsEditableNodeParent(t, out var ep)) foreach (var c in ep.EditableEntries) set.Add(c);
            }
            return set;
        }

        void ModifyTree(SchedNode node = null)
        {
            node = node ?? SelectedNodes().LastOrDefault();
            // do not modify a Day Node - only ScheduleEntry and Schedule Exceptions leaves are editable
            if (node != null && node.ParentNode != null && IsEditableNode(node, out _) && !node.IsEditing) node.BeginEdit();
        }

        async Task CommitEdit(SchedNode node, string label)
        {
            if (!node.IsEditing) return;
            node.IsEditing = false;
            Schedule.Focus();
            if (!IsEditableNode(node, out var en)) return;
            if (string.IsNullOrEmpty(label)) { await Dialogs.Message("Value cannot be blank.", "Error", this); return; }
            if (!en.TryUpdateFromLabel(label, out string error)) { await Dialogs.Message(error, "Error", this); return; }
            MarkDirty();
        }

        async void Add(bool tree)
        {
            if (tree)
            {
                var parents = GetSelectedParentNodes();
                IEditableNode lastAdded = null;
                foreach (var p in parents)
                {
                    var added = p.AddEntryFromLabel(p.GetDefaultLabelForNewNode(), out _);
                    if (added == null) { await Dialogs.Message("Error adding new schedule entry to \"" + p.Name + "\"", "Error", this); continue; }
                    lastAdded = added;
                }
                if (lastAdded != null) MarkDirty();
                if (parents.Count == 1 && lastAdded != null)
                {
                    ExpandAll();
                    Schedule.SelectedItem = lastAdded.Node;
                    ModifyTree(lastAdded.Node);
                }
            }
            else
            {
                var nr = new BacnetDeviceObjectPropertyReference(new BacnetObjectId(), BacnetPropertyIds.PROP_PRESENT_VALUE);
                var form = new EditPropertyObjectReference(nr);
                await form.ShowDialog(this);
                if (form.OutOK) AddPropertyRefentry(form.ObjRef, -1);
            }
        }

        async void Delete(bool tree)
        {
            if (tree)
            {
                bool removed = false;
                foreach (var t in SelectedNodes())
                    if (t.ParentNode != null && IsEditableNode(t, out var en) && en.Parent.RemoveEntry(en)) removed = true;
                if (removed) MarkDirty();
            }
            else if (listReferences.SelectedItem != null)
            {
                foreach (var it in listReferences.SelectedItems.OfType<RefItem>().ToList()) _refs.Remove(it);
                MarkDirty();
            }
        }

        void Copy(bool tree)
        {
            if (tree) NodesToCopy = GetSelectedEditableNodes().ToList();
            else if (listReferences.SelectedItem is RefItem r) PropertyReferenceCopy = r;
        }

        void Paste(bool tree)
        {
            if (tree)
            {
                if (NodesToCopy == null || NodesToCopy.Count == 0) return;
                var parents = GetSelectedParentNodes();
                SchedNode lastAdded = null;
                foreach (var p in parents)
                    foreach (var n in NodesToCopy)
                    {
                        var cloned = p.CloneEntry(n);   // null if e.g. an Exception is pasted into a day
                        if (cloned != null) lastAdded = cloned.Node;
                    }
                if (lastAdded != null) MarkDirty();
                ExpandAll();
                if (parents.Count == 1 && lastAdded != null) { Schedule.SelectedItem = lastAdded; ModifyTree(lastAdded); }
            }
            else if (PropertyReferenceCopy != null) AddPropertyRefentry(PropertyReferenceCopy.Ref, -1);
        }

        // ------------------------------------------------------------------ property reference list
        void AddPropertyRefentry(BacnetDeviceObjectPropertyReference bopr, int idxRemove, bool markDirty = true)
        {
            string newText;
            if (bopr.deviceIndentifier.type != BacnetObjectTypes.OBJECT_DEVICE)
                newText = bopr.objectIdentifier.ToString().Substring(7) + " - " + ((BacnetPropertyIds)bopr.propertyIdentifier).ToString().Substring(5) + " on localDevice";
            else
                newText = bopr.objectIdentifier.ToString().Substring(7) + " - " + ((BacnetPropertyIds)bopr.propertyIdentifier).ToString().Substring(5) + " on DEVICE:" + bopr.deviceIndentifier.instance;
            if (idxRemove != -1 && idxRemove < _refs.Count) _refs.RemoveAt(idxRemove);
            _refs.Add(new RefItem { Ref = bopr, Text = newText });
            if (markDirty) MarkDirty();
        }

        async void ModifyReference()
        {
            if (!(listReferences.SelectedItem is RefItem sel)) return;
            try
            {
                var form = new EditPropertyObjectReference(sel.Ref);
                await form.ShowDialog(this);
                sel.Ref = form.ObjRef;
                int idx = _refs.IndexOf(sel);
                if (form.RefModified) AddPropertyRefentry(form.ObjRef, idx);
            }
            catch { }
        }

        // ------------------------------------------------------------------ Save / Load (line based .dat file, same format as the original)
        const string ScheduleFileHeader = "YABE-SCHEDULE-V1";

        async Task SaveClick()
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save schedule state", DefaultExtension = "dat",
                SuggestedFileName = schedule_id.type.ToString().Replace("OBJECT_", "") + "_" + schedule_id.instance + ".dat",
                FileTypeChoices = new[] { new FilePickerFileType("Schedule state (*.dat)") { Patterns = new[] { "*.dat" } }, FilePickerFileTypes.All }
            });
            if (f == null) return;
            try { SaveScheduleToFile(f.Path.LocalPath); }
            catch (Exception ex) { await Dialogs.Message("Failed to save the schedule state to file. " + ex.Message, "Error saving file", this); }
        }

        async Task LoadClick()
        {
            var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load schedule state", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Schedule state (*.dat)") { Patterns = new[] { "*.dat" } }, FilePickerFileTypes.All }
            });
            if (r.Count == 0) return;
            try { await LoadScheduleFromFile(r[0].Path.LocalPath); }
            catch (Exception ex) { await Dialogs.Message("Failed to load the schedule state from file. " + ex.Message, "Error loading file", this); }
        }

        void SaveScheduleToFile(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(ScheduleFileHeader);
            sb.AppendLine("DataType=" + ((int)ScheduleType).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("StartDate=" + TxtStartDate.Text);
            sb.AppendLine("EndDate=" + TxtEndDate.Text);
            sb.AppendLine("Default=" + TxtScheduleDefault.Text);
            for (int i = 0; i < 7; i++)
            {
                sb.AppendLine("[Day" + i + "]");
                if (i < _roots.Count && _roots[i].Tag is DailySchedule daily)
                    foreach (var entry in daily.Entries) sb.AppendLine(entry.ToDisplayString());
            }
            if (_roots.Count > 7 && _roots[7].Tag is ScheduleExceptionCollection exceptions)
            {
                sb.AppendLine("[Exceptions]");
                foreach (var ex in exceptions.Exceptions) sb.AppendLine(ex.ToDisplayString());
            }
            sb.AppendLine("[References]");
            foreach (var ri in _refs)
            {
                var b = ri.Ref;
                sb.AppendLine(string.Join(" ", new[]
                {
                    ((uint)b.objectIdentifier.type).ToString(CultureInfo.InvariantCulture), b.objectIdentifier.instance.ToString(CultureInfo.InvariantCulture),
                    ((uint)b.propertyIdentifier).ToString(CultureInfo.InvariantCulture), b.arrayIndex.ToString(CultureInfo.InvariantCulture),
                    ((uint)b.deviceIndentifier.type).ToString(CultureInfo.InvariantCulture), b.deviceIndentifier.instance.ToString(CultureInfo.InvariantCulture)
                }));
            }
            File.WriteAllText(path, sb.ToString());
        }

        async Task LoadScheduleFromFile(string path)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != ScheduleFileHeader)
                throw new Exception("This file is not a recognized Yabe schedule state file.");

            BacnetApplicationTags dataType = ScheduleType;
            string startDate = null, endDate = null, defaultValue = null;
            var dayLines = new List<string>[7];
            for (int i = 0; i < 7; i++) dayLines[i] = new List<string>();
            List<string> exceptionLines = null; var referenceLines = new List<string>();
            List<string> current = null;
            for (int li = 1; li < lines.Length; li++)
            {
                string line = lines[li].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string section = line.Substring(1, line.Length - 2);
                    if (section.StartsWith("Day") && int.TryParse(section.Substring(3), out int di) && di >= 0 && di < 7) current = dayLines[di];
                    else if (section == "Exceptions") current = exceptionLines = new List<string>();
                    else if (section == "References") current = referenceLines;
                    else current = null;
                    continue;
                }
                if (current != null) { current.Add(line); continue; }
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "DataType": if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dt)) dataType = (BacnetApplicationTags)dt; break;
                    case "StartDate": startDate = val; break;
                    case "EndDate": endDate = val; break;
                    case "Default": defaultValue = val; break;
                }
            }

            var errors = new List<string>();
            RunWithDirtyTrackingSuppressed(() =>
            {
                ScheduleType = dataType;
                int idx = (int)ScheduleType - 1;
                ScheduleDataType.SelectedIndex = idx >= 0 && idx < ScheduleDataType.ItemCount ? idx : -1;
                if (startDate != null) TxtStartDate.Text = startDate;
                if (endDate != null) TxtEndDate.Text = endDate;
                if (defaultValue != null) TxtScheduleDefault.Text = defaultValue;

                _roots.Clear();
                Func<BacnetApplicationTags> typeOf = () => ScheduleType;
                for (int i = 0; i < 7; i++)
                {
                    var daily = new DailySchedule(typeOf, i);
                    _roots.Add(daily.Node);
                    foreach (string l in dayLines[i])
                        if (daily.AddEntryFromLabel(l, out string err) == null) errors.Add(daily.Name + ": \"" + l + "\" - " + err);
                    daily.SortEntries();
                }
                if (exceptionLines != null)
                {
                    var ex = new ScheduleExceptionCollection(typeOf);
                    _roots.Add(ex.Node);
                    foreach (string l in exceptionLines)
                        if (ex.AddEntryFromLabel(l, out string err) == null) errors.Add("Exception: \"" + l + "\" - " + err);
                }
                ExpandAll();
                _refs.Clear();
                foreach (string l in referenceLines)
                {
                    if (TryParseReferenceLine(l, out var bopr)) AddPropertyRefentry(bopr, -1, false);
                    else errors.Add("Reference: \"" + l + "\"");
                }
            });
            MarkDirty();   // a local change loaded from file, not yet on the device
            if (errors.Count > 0)
                await Dialogs.Message("The file loaded, but some entries could not be parsed and were skipped:\n\n" + string.Join("\n", errors), "Some entries skipped", this);
        }

        static bool TryParseReferenceLine(string line, out BacnetDeviceObjectPropertyReference bopr)
        {
            bopr = default;
            string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 6) return false;
            var ci = CultureInfo.InvariantCulture;
            if (!uint.TryParse(p[0], NumberStyles.Integer, ci, out uint objType)) return false;
            if (!uint.TryParse(p[1], NumberStyles.Integer, ci, out uint objInstance)) return false;
            if (!uint.TryParse(p[2], NumberStyles.Integer, ci, out uint propertyId)) return false;
            if (!uint.TryParse(p[3], NumberStyles.Integer, ci, out uint arrayIndex)) return false;
            if (!uint.TryParse(p[4], NumberStyles.Integer, ci, out uint deviceType)) return false;
            if (!uint.TryParse(p[5], NumberStyles.Integer, ci, out uint deviceInstance)) return false;
            var objectId = new BacnetObjectId((BacnetObjectTypes)objType, objInstance);
            BacnetObjectId? deviceId = null;
            if ((BacnetObjectTypes)deviceType == BacnetObjectTypes.OBJECT_DEVICE) deviceId = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, deviceInstance);
            bopr = new BacnetDeviceObjectPropertyReference(objectId, (BacnetPropertyIds)propertyId, deviceId, arrayIndex);
            return true;
        }

        // =============================================================================================
        public class EnumItem
        {
            public string Name; public uint enumValue;
            public EnumItem(string name, uint v) { Name = name; enumValue = v; }
            public override string ToString() => Name;
        }

        /// <summary>Port of EditPropertyObjectReference ("Object Property Reference" dialog). Show with ShowDialog(owner), then read ObjRef / OutOK / RefModified.</summary>
        public class EditPropertyObjectReference : Window
        {
            public BacnetDeviceObjectPropertyReference ObjRef;
            public bool OutOK = false;
            public bool RefModified;
            readonly ComboBox Reference_ObjType, Reference_Prop;
            readonly TextBox Reference_ObjId, Reference_Array, Reference_Device;

            public EditPropertyObjectReference(BacnetDeviceObjectPropertyReference objRef)
            {
                ObjRef = objRef;
                Title = "Object Property Reference";
                Width = 346; Height = 310; CanResize = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;

                Reference_ObjType = new ComboBox { Width = 190 };
                var types = new List<EnumItem>();
                foreach (BacnetObjectTypes bot in Enum.GetValues(typeof(BacnetObjectTypes))) types.Add(new EnumItem(bot.ToString().Substring(7), (uint)bot));
                types.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));      // Sorted = true in the original
                Reference_ObjType.ItemsSource = types;
                Reference_Prop = new ComboBox { Width = 270 };
                var props = new List<EnumItem>();
                foreach (BacnetPropertyIds bpi in Enum.GetValues(typeof(BacnetPropertyIds))) props.Add(new EnumItem(bpi.ToString().Substring(5), (uint)bpi));
                Reference_Prop.ItemsSource = props;
                Reference_ObjType.SelectedItem = types.FirstOrDefault(t => t.enumValue == (uint)ObjRef.objectIdentifier.type);
                Reference_Prop.SelectedItem = props.FirstOrDefault(t => t.enumValue == (uint)ObjRef.propertyIdentifier);

                Reference_ObjId = new TextBox { Width = 70, Text = ObjRef.objectIdentifier.instance.ToString() };
                Reference_Device = new TextBox { Width = 70 };
                Reference_Array = new TextBox { Width = 70 };
                if (ObjRef.deviceIndentifier.type == BacnetObjectTypes.OBJECT_DEVICE) Reference_Device.Text = ObjRef.deviceIndentifier.instance.ToString();
                if (ObjRef.arrayIndex != ASN1.BACNET_ARRAY_ALL) Reference_Array.Text = ObjRef.arrayIndex.ToString();

                var objRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Reference_ObjType, new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center }, Reference_ObjId } };
                var optional = new Border
                {
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 8, 12, 12), Margin = new Thickness(0, 16, 0, 0),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = "Optional", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children =
                            {
                                new StackPanel { Children = { new TextBlock { Text = "Array Index (1..*)" }, Reference_Array } },
                                new StackPanel { Children = { new TextBlock { Text = "Device Id" }, Reference_Device } },
                            } }
                        }
                    }
                };
                var ok = new Button { Content = "OK", Width = 121, Height = 31, HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
                ok.Click += (_, __) => OK_Click();
                Content = new StackPanel
                {
                    Margin = new Thickness(29, 13, 29, 12), Spacing = 3,
                    Children = { new TextBlock { Text = "Object reference" }, objRow, new TextBlock { Text = "Property", Margin = new Thickness(0, 14, 0, 0) }, Reference_Prop, optional, ok }
                };
            }

            void OK_Click()
            {
                try
                {
                    BacnetObjectId? device = null; uint arrayIdx = ASN1.BACNET_ARRAY_ALL;
                    if (!string.IsNullOrEmpty(Reference_Device.Text)) device = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, Convert.ToUInt32(Reference_Device.Text));
                    if (!string.IsNullOrEmpty(Reference_Array.Text)) arrayIdx = Convert.ToUInt16(Reference_Array.Text);
                    var nr = new BacnetDeviceObjectPropertyReference(
                        new BacnetObjectId((BacnetObjectTypes)((EnumItem)Reference_ObjType.SelectedItem).enumValue, Convert.ToUInt32(Reference_ObjId.Text) & 0x3FFFFF),
                        (BacnetPropertyIds)((EnumItem)Reference_Prop.SelectedItem).enumValue, device, arrayIdx);
                    if (!ObjRef.Equals(nr)) { ObjRef = nr; RefModified = true; }
                    OutOK = true;
                    Close();
                }
                catch { Close(); }
            }
        }
    }
}
