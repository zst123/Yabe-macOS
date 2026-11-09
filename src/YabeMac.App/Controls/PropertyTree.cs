using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.BACnet;
using System.Linq;
using Yabe;

namespace YabeMac.PropertyPanel
{
    /// <summary>
    /// Replacement of the WinForms PropertyGrid used by YABE: a two column tree (property / value) with a
    /// "BacnetProperty" category header, expandable structures and arrays, inline typed editors and a description pane.
    /// It knows nothing about BACnet I/O: edits are reported through <see cref="CommitRequested"/> and friends.
    /// </summary>
    public class PropertyTree : UserControl
    {
        const string CategoryMark = "\u0001category";

        // ---- events towards the host (MainWindow.Properties.cs) -----------------------------------------------
        /// <summary>A value was edited: node + the parsed new value (null = NULL).</summary>
        public event Action<PropertyNode, object> CommitRequested;
        public event Action<PropertyNode> RelinquishRequested;
        public event Action<PropertyNode> AddElementRequested;
        public event Action<PropertyNode> RemoveElementRequested;
        public event Action OpenScheduleRequested;
        public event Action OpenCalendarRequested;
        public event Action<bool> SimplifiedViewToggled;
        public event Action<PropertyNode> NodeDoubleClicked;

        // ---- state ----------------------------------------------------------------------------------------------
        readonly ListBox _list = new ListBox();
        readonly ObservableCollection<PropertyNode> _rows = new ObservableCollection<PropertyNode>();
        readonly PropertyNode _category = new PropertyNode { Name = "BacnetProperty", Hint = CategoryMark, Expanded = true, Kind = EditKind.ReadOnly };
        List<PropertyNode> _roots = new List<PropertyNode>();
        readonly TextBlock _descName = new TextBlock { FontWeight = FontWeight.SemiBold };
        readonly TextBlock _descText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        readonly TextBlock _message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        readonly Thumb _splitter = new Thumb { Width = 5, HorizontalAlignment = HorizontalAlignment.Left, Cursor = new Cursor(StandardCursorType.SizeWestEast), Background = Brushes.Transparent };
        readonly MenuItem _miEdit = new MenuItem { Header = "Edit value", InputGesture = new KeyGesture(Key.F2) };
        readonly MenuItem _miNull = new MenuItem { Header = "Write NULL (relinquish)" };
        readonly MenuItem _miAdd = new MenuItem { Header = "Add list element..." };
        readonly MenuItem _miRemove = new MenuItem { Header = "Remove list element" };
        readonly MenuItem _miSimplified = new MenuItem { Header = "Simplified view", ToggleType = MenuItemToggleType.CheckBox };
        readonly MenuItem _miSchedule = new MenuItem { Header = "Open schedule editor" };
        readonly MenuItem _miCalendar = new MenuItem { Header = "Open calendar editor" };
        double _nameWidth = 110;
        EditSession _edit;
        bool _suppressSimplified;

        public event Action<double> NameWidthChanged;

        public double NameWidth
        {
            get => _nameWidth;
            set
            {
                value = Math.Max(50, value);
                _nameWidth = value;
                _splitter.Margin = new Thickness(value - 2, 0, 0, 0);
                NameWidthChanged?.Invoke(value);
            }
        }

        public BacnetObjectTypes? CurrentObjectType { get; set; }
        public PropertyNode SelectedNode => _list.SelectedItem as PropertyNode is PropertyNode n && n.Hint != CategoryMark ? n : null;
        public IReadOnlyList<PropertyNode> Roots => _roots;

        public bool SimplifiedView
        {
            get => _miSimplified.IsChecked;
            set { _suppressSimplified = true; _miSimplified.IsChecked = value; _suppressSimplified = false; }
        }

        public PropertyTree()
        {
            FontSize = 12;
            _list.ItemsSource = _rows;
            _list.ItemTemplate = new FuncDataTemplate<PropertyNode>((n, _) => BuildRow(n), true);
            _list.SelectionMode = SelectionMode.Single;
            _list.Padding = new Thickness(0);
            _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters = { new Setter(Layoutable.MinHeightProperty, 20.0), new Setter(TemplatedControl.PaddingProperty, new Thickness(0)) }
            });
            _list.SelectionChanged += (_, __) => { CancelEdit(); UpdateDescription(); };
            _list.AddHandler(InputElement.KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);

            _splitter.DragDelta += (_, e) => NameWidth = Math.Min(Math.Max(60, NameWidth + e.Vector.X), Math.Max(80, Bounds.Width - 60));
            _splitter.Margin = new Thickness(_nameWidth - 2, 0, 0, 0);

            var listHost = new Grid();
            listHost.Children.Add(_list);
            listHost.Children.Add(_splitter);

            var desc = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
                Padding = new Thickness(6, 3), MinHeight = 56, MaxHeight = 120,
                Child = new StackPanel { Children = { _descName, _descText, _message } },
            };
            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            grid.Children.Add(listHost);
            Grid.SetRow(desc, 1);
            grid.Children.Add(desc);
            Content = grid;

            BuildContextMenu();
            Rebuild();
        }

        // ================================================================================================
        // Public API
        // ================================================================================================

        public void Clear()
        {
            CancelEdit();
            _roots = new List<PropertyNode>();
            _rows.Clear();
            _message.Text = "";
            UpdateDescription();
        }

        /// <summary>
        /// Shows a fresh set of properties. keepState: same object as before (refresh) - restores expansion, selection
        /// and scroll position like the original UpdateGrid; otherwise selects Present Value.
        /// </summary>
        public void SetNodes(List<PropertyNode> roots, bool keepState)
        {
            CancelEdit();
            var expState = new Dictionary<string, bool>();
            string selKey = null;
            Vector offset = default;
            if (keepState)
            {
                foreach (var r in _roots) CollectExpansion(r, expState);
                selKey = SelectedNode?.PathKey;
                offset = Scroller()?.Offset ?? default;
                foreach (var r in roots) RestoreExpansion(r, expState);
            }
            _roots = roots;
            _message.Text = "";
            Rebuild();

            PropertyNode sel = null;
            if (keepState && selKey != null) sel = _rows.FirstOrDefault(n => n.PathKey == selKey && n.Hint != CategoryMark);
            if (sel == null && !keepState)
                sel = _roots.FirstOrDefault(n => n.PropertyId == (uint)BacnetPropertyIds.PROP_PRESENT_VALUE);
            if (sel != null) _list.SelectedItem = sel;
            if (keepState)
                Dispatcher.UIThread.Post(() => { var sv = Scroller(); if (sv != null) sv.Offset = offset; }, DispatcherPriority.Loaded);
            else if (sel != null)
                Dispatcher.UIThread.Post(() => _list.ScrollIntoView(sel), DispatcherPriority.Loaded);
            UpdateDescription();
        }

        public void ShowMessage(string text, bool error = true)
        {
            _message.Text = text ?? "";
            _message.Foreground = error ? Brushes.IndianRed : _descText.Foreground;
        }

        public void ExpandAll(bool expand)
        {
            void Walk(PropertyNode n, int depth)
            {
                if (!n.HasChildren) return;
                n.Expanded = expand;
                if (expand && depth < 3 && n.Children.Count <= 200) foreach (var c in n.Children) Walk(c, depth + 1);
            }
            foreach (var r in _roots) Walk(r, 0);
            Rebuild(true);
        }

        // ================================================================================================
        // Rows
        // ================================================================================================

        ScrollViewer Scroller() => _list.Scroll as ScrollViewer;

        static void CollectExpansion(PropertyNode n, Dictionary<string, bool> d)
        {
            if (!n.HasChildren) return;
            d[n.PathKey] = n.Expanded;
            if (n._children != null) foreach (var c in n._children) CollectExpansion(c, d);
        }

        static void RestoreExpansion(PropertyNode n, Dictionary<string, bool> d)
        {
            if (!n.HasChildren) return;
            if (d.TryGetValue(n.PathKey, out bool e)) n.Expanded = e;
            // nested children are restored when they get created: hook through the child builder
            var builder = n._childBuilder;
            if (builder != null)
                n._childBuilder = node => { var kids = builder(node); foreach (var k in kids) RestoreExpansion(k, d); return kids; };
            else if (n._children != null) foreach (var c in n._children) RestoreExpansion(c, d);
        }

        void Rebuild(bool keepScroll = false)
        {
            var sel = _list.SelectedItem as PropertyNode;
            var off = keepScroll ? Scroller()?.Offset ?? default : default;
            var flat = new List<PropertyNode> { _category };
            if (_category.Expanded) foreach (var r in _roots) Append(r, flat);
            _rows.Clear();
            foreach (var n in flat) _rows.Add(n);
            if (sel != null && flat.Contains(sel)) _list.SelectedItem = sel;
            if (keepScroll) Dispatcher.UIThread.Post(() => { var sv = Scroller(); if (sv != null) sv.Offset = off; }, DispatcherPriority.Loaded);
        }

        static void Append(PropertyNode n, List<PropertyNode> flat)
        {
            flat.Add(n);
            if (n.Expanded && n.HasChildren) foreach (var c in n.Children) Append(c, flat);
        }

        void Toggle(PropertyNode n)
        {
            if (n.Hint == CategoryMark) { n.Expanded = !n.Expanded; Rebuild(true); return; }
            if (!n.HasChildren) return;
            n.Expanded = !n.Expanded;
            Rebuild(true);
        }

        Control BuildRow(PropertyNode n)
        {
            bool isCat = n.Hint == CategoryMark;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var nameCol = grid.ColumnDefinitions[0];
            nameCol.Width = new GridLength(_nameWidth);
            Action<double> onWidth = w => nameCol.Width = new GridLength(w);
            NameWidthChanged += onWidth;
            grid.DetachedFromVisualTree += (_, __) => NameWidthChanged -= onWidth;
            grid.AttachedToVisualTree += (_, __) => { nameCol.Width = new GridLength(_nameWidth); NameWidthChanged -= onWidth; NameWidthChanged += onWidth; };

            // ---- name cell
            var glyph = new TextBlock
            {
                Text = n.HasChildren || isCat ? (n.Expanded ? "▾" : "▸") : "", Width = 14, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
            };
            glyph.PointerPressed += (_, e) => { if (e.GetCurrentPoint(glyph).Properties.IsLeftButtonPressed) { _list.SelectedItem = n; Toggle(n); e.Handled = true; } };
            var nameText = new TextBlock
            {
                Text = n.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isCat ? FontWeight.SemiBold : FontWeight.Normal,
            };
            ToolTip.SetTip(nameText, n.Name);
            var namePanel = new DockPanel { Margin = new Thickness(n.Depth * 14 + 2, 0, 2, 0), LastChildFill = true };
            DockPanel.SetDock(glyph, Dock.Left);
            namePanel.Children.Add(glyph);
            namePanel.Children.Add(nameText);
            var nameCell = new Border
            {
                Child = namePanel, BorderThickness = new Thickness(0, 0, 1, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                Background = isCat ? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) : null, ClipToBounds = true,
            };
            grid.Children.Add(nameCell);

            if (isCat)
            {
                Grid.SetColumn(nameCell, 0);
                Grid.SetColumnSpan(nameCell, 2);
                nameCell.BorderThickness = new Thickness(0, 0, 0, 1);
                nameCell.PointerPressed += (_, e) => { _list.SelectedItem = n; Toggle(n); e.Handled = true; };
                return grid;
            }

            // ---- value cell
            var valueText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0),
            };
            SetValueLook(valueText, n);
            var valueCell = new Border
            {
                Child = valueText, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                Background = Brushes.Transparent, Tag = n, ClipToBounds = true,
            };
            Grid.SetColumn(valueCell, 1);
            ToolTip.SetTip(valueCell, string.IsNullOrEmpty(n.Display) ? null : n.Display);
            valueCell.Tapped += (_, e) => { if (_edit == null && PropertyModel.CanEdit(n)) { _list.SelectedItem = n; BeginEdit(n, valueCell); } };
            valueCell.DoubleTapped += (_, e) => NodeDoubleClicked?.Invoke(n);
            nameCell.DoubleTapped += (_, e) => { if (n.HasChildren) Toggle(n); else NodeDoubleClicked?.Invoke(n); };
            grid.Children.Add(valueCell);
            return grid;
        }

        void SetValueLook(TextBlock tb, PropertyNode n)
        {
            if (n.IsErrorValue) { tb.Text = n.Display; tb.Foreground = Brushes.IndianRed; tb.FontStyle = FontStyle.Italic; return; }
            if (n.IsNullValue && !n.IsArray) { tb.Text = "null"; tb.Opacity = 0.45; tb.FontStyle = FontStyle.Italic; return; }
            tb.Text = n.Display;
            if (n.Changed) { tb.Foreground = Brushes.DarkOrange; tb.FontWeight = FontWeight.SemiBold; }
            if (n.ReadOnly || n.Kind == EditKind.ReadOnly) tb.Opacity = 0.65;
        }

        void UpdateDescription()
        {
            var n = SelectedNode;
            if (n == null) { _descName.Text = _category.Hint == CategoryMark && _list.SelectedItem == _category ? _category.Name : ""; _descText.Text = ""; return; }
            _descName.Text = n.Name;
            _descText.Text = n.Description;
        }

        // ================================================================================================
        // Keyboard
        // ================================================================================================

        void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (_edit != null) return;
            var n = _list.SelectedItem as PropertyNode;
            if (n == null) return;
            switch (e.Key)
            {
                case Key.Right: if (n.HasChildren && !n.Expanded) { Toggle(n); e.Handled = true; } break;
                case Key.Left:
                    if (n.HasChildren && n.Expanded) { Toggle(n); e.Handled = true; }
                    else if (n.Parent != null) { _list.SelectedItem = n.Parent; e.Handled = true; }
                    break;
                case Key.F2:
                case Key.Enter:
                    if (n.Hint == CategoryMark) Toggle(n);
                    else if (PropertyModel.CanEdit(n)) BeginEditSelected();
                    else if (n.HasChildren) Toggle(n);
                    e.Handled = true; break;
                case Key.C:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)) { CopyValue(); e.Handled = true; }
                    break;
            }
        }

        public void EditSelected() => BeginEditSelected();

        void BeginEditSelected()
        {
            var n = SelectedNode;
            if (n == null) return;
            var cell = FindValueCell(n);
            if (cell != null) BeginEdit(n, cell);
        }

        Border FindValueCell(PropertyNode n)
        {
            foreach (var c in _list.GetVisualDescendants().OfType<Border>())
                if (ReferenceEquals(c.Tag, n)) return c;
            return null;
        }

        // ================================================================================================
        // Editing
        // ================================================================================================

        sealed class EditSession
        {
            public PropertyNode Node;
            public Border Cell;
            public Control OriginalChild;
            public bool Done;
            public Flyout Flyout;
        }

        void CancelEdit()
        {
            var s = _edit;
            if (s == null) return;
            _edit = null;
            s.Done = true;
            try { s.Flyout?.Hide(); } catch { }
            if (s.Cell != null) s.Cell.Child = s.OriginalChild;
        }

        void EndEditWithValue(EditSession s, object value)
        {
            if (s.Done) return;
            _edit = null; s.Done = true;
            s.Cell.Child = s.OriginalChild;
            try { s.Flyout?.Hide(); } catch { }
            CommitRequested?.Invoke(s.Node, value);
        }

        void CommitText(EditSession s, string text, string initial)
        {
            if (s.Done) return;
            if (text == initial) { CancelEdit(); return; }
            if (!PropertyModel.TryParse(s.Node, text, out var value, out var err))
            {
                _edit = null; s.Done = true;
                s.Cell.Child = s.OriginalChild;
                ShowMessage(s.Node.Name + ": " + err);
                return;
            }
            EndEditWithValue(s, value);
        }

        static string EditText(PropertyNode n)
        {
            if (n.Value == null) return "";
            switch (n.Kind)
            {
                case EditKind.Text: return n.Value as string ?? n.Value.ToString();
                case EditKind.BitString: return n.Value is BacnetBitString b ? b.ToString() : n.Display;
                case EditKind.Date: return n.Value is DateTime d ? d.ToString("d") : n.Display;
                case EditKind.Time: return n.Value is DateTime t ? t.ToString("T") : n.Display;
                case EditKind.DateTime: return n.Value is DateTime dt ? dt.ToString() : n.Display;
                default: return n.Display;
            }
        }

        void BeginEdit(PropertyNode n, Border cell)
        {
            if (!PropertyModel.CanEdit(n)) return;
            CancelEdit();
            var s = new EditSession { Node = n, Cell = cell, OriginalChild = cell.Child as Control };
            _edit = s;

            switch (n.Kind)
            {
                case EditKind.Choice:
                case EditKind.Boolean:
                    {
                        var items = new List<object>();
                        object current = null;
                        if (n.Kind == EditKind.Boolean) { items.Add("False"); items.Add("True"); current = n.Value is bool b ? (b ? "True" : "False") : null; }
                        else
                        {
                            items.AddRange(n.Choices);
                            if (n.Value != null && TryChoiceValue(n.Value, out uint cv))
                            {
                                current = n.Choices.FirstOrDefault(c => c.Value == cv);
                                if (current == null) { var extra = new Choice { Value = cv, Label = n.Display }; items.Add(extra); current = extra; }
                            }
                        }
                        var combo = new ComboBox { ItemsSource = items, SelectedItem = current, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 20, Padding = new Thickness(4, 0), FontSize = FontSize };
                        bool initial = true;
                        combo.SelectionChanged += (_, __) =>
                        {
                            if (initial || s.Done || combo.SelectedItem == null) return;
                            string label = combo.SelectedItem is Choice ch ? ch.Label : combo.SelectedItem.ToString();
                            if (!PropertyModel.TryParse(n, label, out var v, out var err)) { CancelEdit(); ShowMessage(err); return; }
                            EndEditWithValue(s, v);
                        };
                        combo.DropDownClosed += (_, __) => { if (!s.Done) Dispatcher.UIThread.Post(() => { if (_edit == s) CancelEdit(); }); };
                        cell.Child = combo;
                        Dispatcher.UIThread.Post(() => { initial = false; combo.Focus(); combo.IsDropDownOpen = true; }, DispatcherPriority.Input);
                        break;
                    }
                case EditKind.BitString when n.EnumType != null:
                    BeginBitEdit(s);
                    break;
                case EditKind.Date:
                case EditKind.Time:
                case EditKind.DateTime:
                    {
                        var fly = YabeMac.Forms.DateTimeEditor.CreateFlyout(n, v => EndEditWithValue(s, v), () => CancelEdit());
                        s.Flyout = fly;
                        fly.Closed += (_, __) => { if (_edit == s) CancelEdit(); };
                        fly.ShowAt(s.Cell);
                        break;
                    }
                default:
                    {
                        string initial = EditText(n);
                        var tb = new TextBox { Text = initial, MinHeight = 20, Padding = new Thickness(3, 0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = FontSize };
                        ToolTip.SetTip(tb, EditHint(n));
                        tb.KeyDown += (_, e) =>
                        {
                            if (e.Key == Key.Enter) { CommitText(s, tb.Text, initial); e.Handled = true; }
                            else if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
                        };
                        tb.LostFocus += (_, __) => { if (!s.Done) CommitText(s, tb.Text, initial); };
                        cell.Child = tb;
                        Dispatcher.UIThread.Post(() => { tb.Focus(); tb.SelectAll(); }, DispatcherPriority.Input);
                        break;
                    }
            }
        }

        static bool TryChoiceValue(object v, out uint u)
        {
            u = 0;
            try
            {
                if (v is Enum) { u = Convert.ToUInt32(v); return true; }
                if (v is ulong ul) { u = (uint)ul; return true; }
                if (v is uint x) { u = x; return true; }
                if (v is long l) { u = (uint)l; return true; }
                if (v is int i) { u = (uint)i; return true; }
            }
            catch { }
            return false;
        }

        static string EditHint(PropertyNode n)
        {
            switch (n.Kind)
            {
                case EditKind.Octets: return "Bytes: [1,2,0x0A] or hex digits (0A FF 01)";
                case EditKind.ObjectId: return "TYPE:INSTANCE, e.g. OBJECT_ANALOG_INPUT:1 or AI:1";
                case EditKind.Date: return "Date, e.g. " + DateTime.Today.ToString("d");
                case EditKind.Time: return "Time, e.g. " + DateTime.Now.ToString("T");
                case EditKind.DateTime: return "Date and time, e.g. " + DateTime.Now.ToString();
                case EditKind.BitString: return "Bits as 0 and 1, e.g. 0101";
                case EditKind.IpAddress: return "IP address";
                default: return n.Nullable ? "Leave empty to write NULL (relinquish)" : null;
            }
        }

        void BeginBitEdit(EditSession s)
        {
            var n = s.Node;
            var info = new PropertyModel.BitInfo { EnumType = n.EnumType, Linear = n.LinearBits };
            var cur = n.Value is BacnetBitString bs ? bs : default;
            string curBits = n.Value is BacnetBitString ? cur.ToString() : "";
            var names = PropertyModel.BitNames(info, Math.Max(curBits.Length, 0));
            var boxes = new List<CheckBox>();
            var panel = new StackPanel { Spacing = 2 };
            for (int i = 0; i < names.Length; i++)
            {
                var cb = new CheckBox { Content = names[i] ?? ("Bit " + i), IsChecked = i < curBits.Length && curBits[i] == '1' };
                boxes.Add(cb); panel.Children.Add(cb);
            }
            var ok = new Button { Content = "OK", MinWidth = 60 };
            var cancel = new Button { Content = "Cancel", MinWidth = 60 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { ok, cancel } };
            var content = new DockPanel { Margin = new Thickness(6), MaxHeight = 340 };
            DockPanel.SetDock(buttons, Dock.Bottom);
            content.Children.Add(buttons);
            content.Children.Add(new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            var fly = new Flyout { Content = content, Placement = PlacementMode.Bottom };
            s.Flyout = fly;
            ok.Click += (_, __) =>
            {
                var sb = new System.Text.StringBuilder();
                foreach (var b in boxes) sb.Append(b.IsChecked == true ? '1' : '0');
                // keep the original length (bits_used) of the value
                string bits = sb.ToString().PadRight(curBits.Length, '0');
                EndEditWithValue(s, BacnetBitString.Parse(bits));
            };
            cancel.Click += (_, __) => CancelEdit();
            fly.Closed += (_, __) => { if (_edit == s) CancelEdit(); };
            fly.ShowAt(s.Cell);
        }

        // ================================================================================================
        // Context menu
        // ================================================================================================

        void BuildContextMenu()
        {
            _miEdit.Click += (_, __) => BeginEditSelected();
            _miNull.Click += (_, __) => { var n = SelectedNode; if (n != null) RelinquishRequested?.Invoke(n); };
            _miAdd.Click += (_, __) => { var n = SelectedNode; if (n != null) AddElementRequested?.Invoke(n); };
            _miRemove.Click += (_, __) => { var n = SelectedNode; if (n != null) RemoveElementRequested?.Invoke(n); };
            _miSchedule.Click += (_, __) => OpenScheduleRequested?.Invoke();
            _miCalendar.Click += (_, __) => OpenCalendarRequested?.Invoke();
            _miSimplified.Click += (_, __) => { if (!_suppressSimplified) SimplifiedViewToggled?.Invoke(_miSimplified.IsChecked); };
            var expand = new MenuItem { Header = "Expand all" }; expand.Click += (_, __) => ExpandAll(true);
            var collapse = new MenuItem { Header = "Collapse all" }; collapse.Click += (_, __) => ExpandAll(false);
            var copyName = new MenuItem { Header = "Copy name" }; copyName.Click += (_, __) => CopyText(SelectedNode?.Name);
            var copyValue = new MenuItem { Header = "Copy value" }; copyValue.Click += (_, __) => CopyValue();
            var menu = new ContextMenu();
            menu.Items.Add(_miEdit);
            menu.Items.Add(_miNull);
            menu.Items.Add(_miAdd);
            menu.Items.Add(_miRemove);
            menu.Items.Add(new Separator());
            menu.Items.Add(expand);
            menu.Items.Add(collapse);
            menu.Items.Add(new Separator());
            menu.Items.Add(copyName);
            menu.Items.Add(copyValue);
            menu.Items.Add(new Separator());
            menu.Items.Add(_miSchedule);
            menu.Items.Add(_miCalendar);
            menu.Items.Add(_miSimplified);
            menu.Opening += (_, __) =>
            {
                var n = SelectedNode;
                _miEdit.IsEnabled = n != null && PropertyModel.CanEdit(n);
                _miNull.IsEnabled = n != null && PropertyModel.CanRelinquish(n);
                _miAdd.IsEnabled = n != null && PropertyModel.CanAddElement(n);
                _miRemove.IsEnabled = n != null && PropertyModel.CanRemoveElement(n);
                _miSchedule.IsVisible = CurrentObjectType == BacnetObjectTypes.OBJECT_SCHEDULE;
                _miCalendar.IsVisible = CurrentObjectType == BacnetObjectTypes.OBJECT_CALENDAR;
            };
            _list.ContextMenu = menu;
        }

        void CopyValue() => CopyText(SelectedNode?.Display);

        void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text); } catch { }
        }
    }
}
