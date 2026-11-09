using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace YabeMac
{
    /// <summary>Avalonia has no MessageBox/InputBox; these cover the WinForms MessageBox + GenericInputBox uses.</summary>
    public static class Dialogs
    {
        public static Window Owner => MainWindow.Instance;

        public static Task Message(string text, string title = "Yabe", Window owner = null) =>
            Show(owner, title, text, new[] { "OK" }, null).ContinueWith(_ => { });

        public static async Task<bool> Confirm(string text, string title = "Yabe", Window owner = null) =>
            await Show(owner, title, text, new[] { "Yes", "No" }, null) == "Yes";

        public static async Task<bool> OkCancel(string text, string title = "Yabe", Window owner = null) =>
            await Show(owner, title, text, new[] { "OK", "Cancel" }, null) == "OK";

        /// <summary>Returns null when cancelled.</summary>
        public static async Task<string> Input(string prompt, string title = "Yabe", string initial = "", Window owner = null)
        {
            string result = null;
            var box = new TextBox { Text = initial, MinWidth = 320, Margin = new Thickness(0, 8, 0, 8) };
            var win = Build(title, prompt, new[] { "OK", "Cancel" }, box, b => { if (b == "OK") result = box.Text; });
            await win.ShowDialog(owner ?? Owner);
            return result;
        }

        static async Task<string> Show(Window owner, string title, string text, string[] buttons, Control extra)
        {
            string pressed = buttons[buttons.Length - 1];
            var win = Build(title, text, buttons, extra, b => pressed = b);
            await win.ShowDialog(owner ?? Owner);
            return pressed;
        }

        static Window Build(string title, string text, string[] buttons, Control extra, System.Action<string> onButton)
        {
            var win = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false, MaxWidth = 700 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var b in buttons)
            {
                var btn = new Button { Content = b, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
                string name = b;
                btn.Click += (_, __) => { onButton(name); win.Close(); };
                row.Children.Add(btn);
            }
            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            if (extra != null) panel.Children.Add(extra);
            panel.Children.Add(row);
            win.Content = panel;
            return win;
        }
    }
}
