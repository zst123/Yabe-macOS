using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace YabeMac
{
    /// <summary>Item of the three lists (original ListViewItemBetterString: Name / Text / Tag).</summary>
    public class GcItem : INotifyPropertyChanged
    {
        string _text;
        public string Name { get; set; }
        public object Tag { get; set; }
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
        public event PropertyChangedEventHandler PropertyChanged;
        public override string ToString() => !string.IsNullOrEmpty(Name) ? Name : (!string.IsNullOrEmpty(Text) ? Text : Tag?.ToString() ?? base.ToString());
    }

    /// <summary>Small helpers to build the code-only plugin windows with WinForms-like group boxes.</summary>
    static class Ui
    {
        public static Border Group(string title, Control content, double width = double.NaN)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12 });
            sp.Children.Add(content);
            return new Border
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 8), Child = sp, Width = width
            };
        }

        public static Button Btn(string text, double w = double.NaN, double h = double.NaN, string tip = null)
        {
            var b = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            if (!double.IsNaN(w)) b.MinWidth = w;
            if (!double.IsNaN(h)) b.MinHeight = h;
            if (!double.IsNaN(h) && h < 30) { b.Padding = new Thickness(6, 1); b.FontSize = 12; }
            if (tip != null) ToolTip.SetTip(b, tip);
            return b;
        }

        public static ListBox List(ObservableCollection<GcItem> items, bool multi)
        {
            var lb = new ListBox
            {
                ItemsSource = items,
                SelectionMode = multi ? SelectionMode.Multiple : SelectionMode.Single,
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GcItem>((i, _) =>
                    new TextBlock { [!TextBlock.TextProperty] = new Binding("Text"), TextWrapping = TextWrapping.NoWrap }, true),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1)
            };
            return lb;
        }

        /// <summary>Selected items in list order (WinForms ListBox.SelectedItems order).</summary>
        public static List<GcItem> Selected(ListBox lb, ObservableCollection<GcItem> items)
        {
            var set = new HashSet<object>(lb.SelectedItems?.Cast<object>() ?? Enumerable.Empty<object>());
            return items.Where(i => set.Contains(i)).ToList();
        }

        public static Control Row(params Control[] c)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var x in c) sp.Children.Add(x);
            return sp;
        }

        public static TextBox Text(double w = double.NaN, string watermark = null) =>
            new TextBox { Width = w, Watermark = watermark, MinHeight = 24, Padding = new Thickness(4, 2), VerticalContentAlignment = VerticalAlignment.Center };

        public static TextBlock Wrapped(string s) => new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap };
    }

    /// <summary>Port of GlobalCommander/AdvancedFilter (OK / Cancel / Reset(=Abort)).</summary>
    public class GcAdvancedFilterWindow : Window
    {
        public enum Result { Cancel, Ok, Reset }
        public Result Outcome = Result.Cancel;
        public TextBox PropIdText = Ui.Text(102);
        public TextBox ValueText = Ui.Text();
        public ComboBox OperationCombo = new ComboBox { Width = 174 };

        public GcAdvancedFilterWindow(IEnumerable<object> operations)
        {
            Title = "AdvancedFilter";
            Width = 444; Height = 160; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            OperationCombo.ItemsSource = operations.ToList();
            OperationCombo.SelectedIndex = 0;
            var ok = Ui.Btn("OK", 75); var cancel = Ui.Btn("Cancel", 75); var reset = Ui.Btn("Reset", 75);
            ok.Click += (_, __) => { Outcome = Result.Ok; Close(); };
            cancel.Click += (_, __) => { Outcome = Result.Cancel; Close(); };
            reset.Click += (_, __) => { Outcome = Result.Reset; Close(); };
            var g = new Grid { Margin = new Thickness(13), ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,*") };
            void Put(Control c, int col, int row, int span = 1) { Grid.SetColumn(c, col); Grid.SetRow(c, row); Grid.SetColumnSpan(c, span); g.Children.Add(c); }
            Put(new TextBlock { Text = "Property ID:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, 0, 0);
            Put(PropIdText, 1, 0);
            Put(new TextBlock { Text = "Value:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) }, 0, 1);
            Grid.SetRowSpan(OperationCombo, 1);
            ValueText.MinWidth = 150; var opRow = Ui.Row(OperationCombo, ValueText);
            ((StackPanel)opRow).Margin = new Thickness(0, 8, 0, 0);
            Put(opRow, 1, 1, 2);
            var buttons = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom };
            DockPanel.SetDock(reset, Dock.Left);
            buttons.Children.Add(reset);
            var right = Ui.Row(ok, cancel); right.HorizontalAlignment = HorizontalAlignment.Right;
            buttons.Children.Add(right);
            Put(buttons, 0, 2, 3);
            Content = g;
            KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        }
    }
}
