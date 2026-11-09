using Avalonia.Controls;
using System;

namespace YabeMac.BuiltinPlugins
{
    /// <summary>Plugins menu entry "Global Commander" (port of GlobalCommander.Plugin).</summary>
    class GlobalCommanderPlugin : IYabeMacPlugin
    {
        public void Init(MainWindow yabe)
        {
            var mi = new MenuItem { Header = "Global Commander" };
            mi.Click += (_, __) =>
            {
                try { new GlobalCommanderWindow(yabe).Show(yabe); }
                catch (Exception ex) { yabe.Log("Failed to load the Global Commander window. " + ex.Message); }
            };
            yabe.AddPluginMenuItem(mi);
        }
    }

    /// <summary>Plugins menu entry "FindPrioritiesGlobal" (port of FindPrioritiesGlobal.Plugin).</summary>
    class FindPrioritiesGlobalPlugin : IYabeMacPlugin
    {
        public void Init(MainWindow yabe)
        {
            var mi = new MenuItem { Header = "FindPrioritiesGlobal" };
            mi.Click += (_, __) =>
            {
                try { new FindPrioritiesGlobalWindow(yabe).Show(yabe); }
                catch (Exception ex) { yabe.Log("Failed to load the FindPrioritiesGlobal window. " + ex.Message); }
            };
            yabe.AddPluginMenuItem(mi);
        }
    }
}
