using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace YabeMac
{
    /// <summary>Port of GlobalCommander/PropertyValueListForm: device / point / value of the selected property, value editable (writes).</summary>
    public class PropertyValueListWindow : Window
    {
        public class Row : INotifyPropertyChanged
        {
            internal PropertyValueListWindow Owner;
            public GcProperty Prop;
            public string ParentDeviceName => Prop.ParentDeviceName;
            public string ParentPointName => Prop.ParentPointName;
            public string Value
            {
                get => Prop.ValueObjectString;
                set { Owner?.OnEdit(this, value); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
            public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }

        readonly DataGrid grid = new DataGrid
        {
            AutoGenerateColumns = false, GridLinesVisibility = DataGridGridLinesVisibility.All, CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Extended, IsReadOnly = false
        };
        bool busy;

        public PropertyValueListWindow(List<GcProperty> props, string propertyNiceName = null)
        {
            Title = "Property Value List";
            Width = 1045; Height = 649;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var rows = props.Select(p => new Row { Prop = p, Owner = this }).ToList();
            grid.Columns.Add(new DataGridTextColumn { Header = "Device", Binding = new Binding("ParentDeviceName") { Mode = BindingMode.OneWay }, IsReadOnly = true, Width = new DataGridLength(25, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Point", Binding = new Binding("ParentPointName") { Mode = BindingMode.OneWay }, IsReadOnly = true, Width = new DataGridLength(35, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value of Selected Property" + (propertyNiceName == null ? "" : " - " + propertyNiceName),
                Binding = new Binding("Value") { Mode = BindingMode.TwoWay },
                Width = new DataGridLength(40, DataGridLengthUnitType.Star)
            });
            grid.ItemsSource = rows;
            grid.KeyDown += OnGridKeyDown;

            var close = Ui.Btn("Close", 144, 40);
            close.Click += (_, __) => Close();
            close.HorizontalAlignment = HorizontalAlignment.Right;
            var bottom = new Border { Child = close, Padding = new Thickness(0, 4, 3, 5), Height = 49 };
            var dock = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(bottom);
            dock.Children.Add(grid);
            Content = dock;
            KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) Close(); };
        }

        async void OnEdit(Row row, string text)
        {
            if (busy) return;
            busy = true;
            try { await Task.Run(() => row.Prop.ValueObjectString = text); }
            catch (Exception ex) { await Dialogs.Message(ex.Message, "Data Error", this); }
            finally { busy = false; Dispatcher.UIThread.Post(row.Refresh); }
        }

        // Backspace: erase one character and continue editing; Delete: empty the field (not committed yet).
        void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Back && e.Key != Key.Delete) return;
            if (grid.CurrentColumn == null || grid.CurrentColumn.IsReadOnly || grid.SelectedItem == null) return;
            var tb = e.Source as TextBox;
            if (tb != null) return;   // already editing: normal text handling
            e.Handled = true;
            bool del = e.Key == Key.Delete;
            if (!grid.BeginEdit()) return;
            Dispatcher.UIThread.Post(() =>
            {
                var editor = grid.CurrentColumn.GetCellContent(grid.SelectedItem) as TextBox;
                if (editor == null) return;
                if (del || string.IsNullOrEmpty(editor.Text) || editor.Text.Length <= 1) editor.Text = string.Empty;
                else editor.Text = editor.Text.Substring(0, editor.Text.Length - 1);
                editor.CaretIndex = editor.Text.Length;
            }, DispatcherPriority.Background);
        }
    }
}
