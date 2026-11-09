using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;

namespace YabeMac
{
    // macOS "Liquid Glass"-style window: blurred translucent backdrop (NSVisualEffectView under the
    // hood), title bar merged into the content, and the translucent theme in Theme/Glass.axaml.
    // Toggle with AppSettings.UseGlassTheme (settings.json); the classic look is the fallback.
    public partial class MainWindow
    {
        void ApplyGlass()
        {
            if (!AppSettings.Default.UseGlassTheme || !OperatingSystem.IsMacOS()) return;

            Classes.Add("glass");
            // Dark appearance: keeps the system backdrop dark (the light material turns white when the
            // window is inactive). Set GlassDark=false in settings.json to follow the system instead.
            if (AppSettings.Default.GlassDark) RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            // Dark grey tint painted as the window background (over the blur). Not a child overlay:
            // a child inserted into the root DockPanel collapses to zero size and never tinted anything.
            var tint = new SolidColorBrush(Color.Parse("#C826282D"));
            Background = tint;
            // First choice that the platform supports wins; macOS maps the blur levels to the system backdrop.
            var blur = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent,
            };
            TransparencyLevelHint = blur;

            // macOS swaps the blur material for a light one when the window is inactive, which shows up
            // as a white window. While inactive, drop the blur and paint a solid dark background instead.
            var solid = new SolidColorBrush(Color.Parse("#FF26282D"));
            Deactivated += (_, __) =>
            {
                TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
                Background = solid;
            };
            Activated += (_, __) =>
            {
                Background = tint;
                TransparencyLevelHint = blur;
            };

            // Cards that are custom controls rather than styled Fluent controls.
            PropertyTreeView?.Classes.Add("glasscard");
            if (CovGraph != null) { CovGraph.OuterBrush = Brushes.Transparent; CovGraph.AreaBrush = Brushes.Transparent; CovGraph.InkBrush = new SolidColorBrush(Color.Parse("#E6E6E6")); }
            if (TrendReviewGraph != null) { TrendReviewGraph.OuterBrush = Brushes.Transparent; TrendReviewGraph.InkBrush = new SolidColorBrush(Color.Parse("#E6E6E6")); }

            // Optional: let content run under the title bar. Off by default because Avalonia then
            // repositions/rescales the traffic-light buttons so they look smaller than in native apps.
            // The standard system title bar keeps them exactly native. Set GlassMergedTitleBar=true to opt in.
            bool merged = AppSettings.Default.GlassMergedTitleBar;
            if (merged)
            {
                ExtendClientAreaToDecorationsHint = true;
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
                ExtendClientAreaTitleBarHeightHint = -1;
            }

            if (Content is Control root)
            {
                // The system reports a 0 decoration margin with PreferSystemChrome, so never go below the
                // standard macOS title bar height (28pt) or the toolbar sits under the traffic lights.
                if (merged)
                {
                    const double MinTitleBar = 30;
                    root.Margin = new Thickness(0, MinTitleBar, 0, 0);
                    PropertyChanged += (_, e) =>
                    {
                        if (e.Property == WindowDecorationMarginProperty)
                            root.Margin = new Thickness(0, Math.Max(WindowDecorationMargin.Top, MinTitleBar), 0, 0);
                    };
                }

                foreach (var child in (root as Panel)?.Children ?? new Avalonia.Controls.Controls())
                    if (child is Border b && b.Child is StackPanel) b.Classes.Add("toolbar");
            }
        }
    }
}
