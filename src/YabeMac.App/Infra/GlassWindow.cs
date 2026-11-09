using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using System;
using System.Linq;
using YabeMac.Plot;

namespace YabeMac
{
    /// <summary>Gives every secondary window (settings, plots, editors...) the main window's glass look.</summary>
    static class GlassWindow
    {
        static readonly IBrush Tint = new SolidColorBrush(Color.Parse("#C826282D"));
        static readonly IBrush Solid = new SolidColorBrush(Color.Parse("#FF26282D"));
        static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E6E6E6"));

        public static void Register()
        {
            if (!AppSettings.Default.UseGlassTheme || !OperatingSystem.IsMacOS()) return;
            Window.LoadedEvent.AddClassHandler<Window>((w, _) => Apply(w), RoutingStrategies.Direct);
        }

        static void Apply(Window w)
        {
            if (w is MainWindow || w.Classes.Contains("glass")) return;
            w.Classes.Add("glass");
            if (AppSettings.Default.GlassDark) w.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            var blur = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent };
            w.Background = Tint;
            w.TransparencyLevelHint = blur;
            w.Deactivated += (_, __) => { w.TransparencyLevelHint = new[] { WindowTransparencyLevel.None }; w.Background = Solid; };
            w.Activated += (_, __) => { w.Background = Tint; w.TransparencyLevelHint = blur; };
            foreach (var p in w.GetVisualDescendants().OfType<PlotControl>())
            {
                p.GradientBackground = false;
                p.OuterBrush = Brushes.Transparent; p.AreaBrush = Brushes.Transparent; p.InkBrush = Ink;
            }
        }
    }
}
