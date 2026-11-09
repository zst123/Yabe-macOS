using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace YabeMac
{
    /// <summary>Small UI helpers shared by the channel dialogs.</summary>
    public static class ChannelsUi
    {
        /// <summary>Selectable read-only text window (SelectableMessageBox replacement).</summary>
        public static Task ShowText(Window owner, string title, string text, double width = 700, double height = 450)
        {
            var win = new Window { Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var ok = new Button { Content = "OK", MinWidth = 72, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(8) };
            ok.Click += (_, __) => win.Close();
            var tb = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 8, 8, 0) };
            var dock = new DockPanel();
            DockPanel.SetDock(ok, Dock.Bottom);
            dock.Children.Add(ok);
            dock.Children.Add(tb);
            win.Content = dock;
            return win.ShowDialog(owner ?? MainWindow.Instance);
        }

        public static string DescribeCertificate(X509Certificate cert)
        {
            var c = cert as X509Certificate2 ?? new X509Certificate2(cert);
            return "Subject: " + c.Subject + "\nIssuer: " + c.Issuer + "\nValid from: " + c.NotBefore + "\nValid to: " + c.NotAfter +
                   "\nSerial number: " + c.SerialNumber + "\nThumbprint: " + c.Thumbprint + "\nSignature algorithm: " + c.SignatureAlgorithm?.FriendlyName;
        }

        public static async Task<string> PickFile(Window owner, string title, string filterName, string[] patterns, string initialDir = null)
        {
            var opts = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
            if (patterns != null)
                opts.FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All };
            try
            {
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                    opts.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialDir);
            }
            catch { }
            var files = await owner.StorageProvider.OpenFilePickerAsync(opts);
            return files.Count == 0 ? null : files[0].Path.LocalPath;
        }
    }
}

namespace YabeMac
{
    public static class ChannelsUiPick
    {
        /// <summary>GenericInputBox&lt;ComboBox&gt; replacement. Returns the selected index, or -1 when cancelled.</summary>
        public static async Task<int> PickFromList(Window owner, string title, string prompt, IList<string> items)
        {
            int result = -1;
            var combo = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = 0, MinWidth = 320, Margin = new Thickness(0, 8, 0, 8) };
            var win = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            var ok = new Button { Content = "OK", MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Click += (_, __) => { result = combo.SelectedIndex; win.Close(); };
            cancel.Click += (_, __) => win.Close();
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } };
            win.Content = new StackPanel { Margin = new Thickness(16), Spacing = 4, Children = { new TextBlock { Text = prompt }, combo, row } };
            await win.ShowDialog(owner ?? MainWindow.Instance);
            return result;
        }
    }
}
