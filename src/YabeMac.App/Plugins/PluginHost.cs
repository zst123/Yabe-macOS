using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    /// <summary>
    /// Avalonia counterpart of the original IYabePlugin. External plugin DLLs (Plugins/&lt;name&gt;.dll, class
    /// &lt;name&gt;.Plugin) must implement this interface and reference YabeMac.App; the original WinForms plugin
    /// DLLs cannot be loaded (they need System.Windows.Forms / YabeMainDialog).
    /// </summary>
    public interface IYabeMacPlugin
    {
        void Init(MainWindow yabe);
    }

    public partial class MainWindow
    {
        /// <summary>Default of the original "Plugins" setting (used when the setting is empty).</summary>
        const string DefaultPlugins = "CheckReliability, CheckStatusFlags, ListOutOfService, GlobalCommander, YabeDeviceReport, FindPriorities, FindPrioritiesGlobal";

        /// <summary>Adds an item to the Plugins menu (the original exposed pluginsToolStripMenuItem).</summary>
        public void AddPluginMenuItem(MenuItem item) { PluginsMenu.Items.Add(item); PluginsMenu.IsVisible = true; }

        /// <summary>
        /// Call once from OnStartup (after Startup.LoadOriginalData()): builds the user command menu and loads plugins.
        /// </summary>
        public void InitExportArea()
        {
            try { InitUserCmd(); } catch (Exception ex) { Log("User commands: " + ex.Message); }
            try { InitPlugins(); } catch (Exception ex) { Log("Plugins: " + ex.Message); }
        }

        static readonly Dictionary<string, Func<IYabeMacPlugin>> BuiltinPlugins = new Dictionary<string, Func<IYabeMacPlugin>>(StringComparer.OrdinalIgnoreCase)
        {
            ["CheckReliability"] = () => new BuiltinPlugins.CheckReliability(),
            ["CheckStatusFlags"] = () => new BuiltinPlugins.CheckStatusFlags(),
            ["ListOutOfService"] = () => new BuiltinPlugins.ListOutOfService(),
            ["FindPriorities"] = () => new BuiltinPlugins.FindPriorities(),
            ["ListCOV_Increment"] = () => new BuiltinPlugins.ListCovIncrement(),
            ["ListAnalogValues"] = () => new BuiltinPlugins.ListAnalogValues(),
            ["GetDevicesNames"] = () => new BuiltinPlugins.GetDevicesNames(),
            ["YabeDeviceReport"] = () => new BuiltinPlugins.DeviceReport(),
            ["GlobalCommander"] = () => new BuiltinPlugins.GlobalCommanderPlugin(),
            ["FindPrioritiesGlobal"] = () => new BuiltinPlugins.FindPrioritiesGlobalPlugin(),
        };

        void InitPlugins()
        {
            string setting = AppSettings.Default.Plugins;
            if (string.IsNullOrWhiteSpace(setting)) setting = DefaultPlugins;
            PluginsMenu.IsVisible = PluginsMenu.Items.Count > 0;

            foreach (string pluginname in setting.Split(new[] { ',', ';' }))
            {
                string name = pluginname.Replace(" ", string.Empty);
                if (name == "") continue;
                try
                {
                    IYabeMacPlugin plugin;
                    if (BuiltinPlugins.TryGetValue(name, out var factory)) plugin = factory();
                    else
                    {
                        string dll = Path.Combine(Startup.AppDir, "Plugins", name + ".dll");
                        if (!File.Exists(dll)) { Log($"Plugin \"{pluginname.Trim()}\" is not available in this port."); continue; }
                        var asm = Assembly.LoadFrom(dll);
                        plugin = asm.CreateInstance(name + ".Plugin", true) as IYabeMacPlugin;
                        if (plugin == null) { Log($"Plugin \"{name}\": no IYabeMacPlugin class {name}.Plugin"); continue; }
                    }
                    plugin.Init(this);
                    Log($"Loaded plugin \"{pluginname.Trim()}\".");
                }
                catch (Exception ex) { Log($"Error loading plugin \"{pluginname.Trim()}\". {ex.Message}"); }
            }
        }
    }
}
