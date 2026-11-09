using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Collections.Specialized;
using System.Linq;

namespace YabeMac
{
    // macOS system menu bar. The structure is generated from the in-window MainMenu (single source
    // of truth, so workers adding Plugins / User Commands items at runtime show up here too); a
    // native item click just raises Click on the matching Avalonia MenuItem, so the same handlers run.
    // The in-window menu is hidden while the native bar is active. Toggle: AppSettings.UseNativeMenu.
    public partial class MainWindow
    {
        bool _nativeMenuActive;

        void InitNativeMenu()
        {
            if (!OperatingSystem.IsMacOS() || !AppSettings.Default.UseNativeMenu) return;
            _nativeMenuActive = true;
            RebuildNativeMenu();
            MainMenu.IsVisible = false;

            // Items added later (plugins, user commands) -> rebuild.
            foreach (var top in MainMenu.Items.OfType<MenuItem>())
                Watch(top);
        }

        void Watch(MenuItem item)
        {
            if (item.Items is INotifyCollectionChanged c)
                c.CollectionChanged += (_, __) => Avalonia.Threading.Dispatcher.UIThread.Post(RebuildNativeMenu);
            foreach (var child in item.Items.OfType<MenuItem>()) Watch(child);
        }

        void RebuildNativeMenu()
        {
            if (!_nativeMenuActive) return;
            var bar = new NativeMenu();
            foreach (var top in MainMenu.Items.OfType<MenuItem>())
                bar.Add(ToNative(top));
            NativeMenu.SetMenu(this, bar);
        }

        static string Clean(object header) => (header?.ToString() ?? "").Replace("_", "");

        static NativeMenuItemBase ToNative(object source)
        {
            if (source is Separator) return new NativeMenuItemSeparator();
            var mi = (MenuItem)source;
            var item = new NativeMenuItem(Clean(mi.Header)) { IsEnabled = mi.IsEnabled };
            var children = mi.Items.Cast<object>().Where(o => o is MenuItem || o is Separator).ToList();
            if (children.Count > 0)
            {
                item.Menu = new NativeMenu();
                foreach (var c in children) item.Menu.Add(ToNative(c));
            }
            else
            {
                item.Click += (_, __) => mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, mi));
            }
            return item;
        }
    }
}
