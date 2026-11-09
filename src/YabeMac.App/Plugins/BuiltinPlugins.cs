using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using Yabe;

namespace YabeMac.BuiltinPlugins
{
    // Ports of the plugins in YabePlugins/* that only needed WinForms for their result dialogs.
    // BACnet logic is identical; the dialogs are Avalonia windows (PluginResultWindow & co).
    // NOT ported: GlobalCommander, FindPrioritiesGlobal (3000+ line WinForms tools).

    abstract class SimplePlugin : IYabeMacPlugin
    {
        protected MainWindow Yabe;
        protected abstract string MenuText { get; }
        protected abstract void Run();
        public virtual void Init(MainWindow yabe)
        {
            Yabe = yabe;
            var mi = new MenuItem { Header = MenuText };
            mi.Click += (_, __) => { try { Run(); } catch { } };
            yabe.AddPluginMenuItem(mi);
        }
        protected static string Pretty(string s) => PluginResultWindow.TitleCase(s.Replace('_', ' '));
    }

    class CheckReliability : SimplePlugin
    {
        protected override string MenuText => "Check Reliability";
        protected override void Run() => _ = PluginResultWindow.Run(Yabe, "Reliability", "All Reliabilities are OK", 320, 340, (device, id) =>
        {
            if (!device.channel.ReadPropertyRequest(device.BacAdr, id, BacnetPropertyIds.PROP_RELIABILITY, out IList<BacnetValue> value)) return null;
            if (Convert.ToUInt32(value[0].Value) == 0) return null;   // RELIABILITY_NO_FAULT_DETECTED
            var n = new ResultNode(PluginResultWindow.ObjectLabel(device, id));
            n.Add(Pretty(((BacnetReliability)Convert.ToUInt32(value[0].Value)).ToString()));
            return n;
        });
    }

    class CheckStatusFlags : SimplePlugin
    {
        protected override string MenuText => "Check Objects Status";
        protected override void Run() => _ = PluginResultWindow.Run(Yabe, "StatusFlags", "All Flags are clean", 660, 600, (device, id) =>
        {
            if (!device.channel.ReadPropertyRequest(device.BacAdr, id, BacnetPropertyIds.PROP_STATUS_FLAGS, out IList<BacnetValue> value)) return null;
            string bits = value[0].Value.ToString();
            if (bits == "0000") return null;   // no flag set
            var n = new ResultNode(PluginResultWindow.ObjectLabel(device, id));
            if (device.channel.ReadPropertyRequest(device.BacAdr, id, BacnetPropertyIds.PROP_DESCRIPTION, out IList<BacnetValue> d))
                n.Add(d[0].Value.ToString());
            for (int i = 0; i < 4 && i < bits.Length; i++)
                if (bits[i] == '1')
                {
                    string alm = PluginResultWindow.TitleCase(Enum.GetName(typeof(BacnetStatusFlags), 1 << i).Replace("_", " "));
                    n.Add(alm.Length > 12 ? alm.Substring(12) : alm);
                }
            return n;
        });
    }

    class ListOutOfService : SimplePlugin
    {
        protected override string MenuText => "List OutOfService";
        protected override void Run() => _ = PluginResultWindow.Run(Yabe, "Out of Service", "No Out of Service found.", 320, 340, (device, id) =>
        {
            if (!device.channel.ReadPropertyRequest(device.BacAdr, id, BacnetPropertyIds.PROP_OUT_OF_SERVICE, out IList<BacnetValue> value)) return null;
            if (!(bool)value[0].Value) return null;
            var n = new ResultNode(PluginResultWindow.ObjectLabel(device, id));
            string name = id.ToString();
            n.Add(name.StartsWith("OBJECT_") ? name.Substring(7) : name);
            return n;
        });
    }

    class FindPriorities : SimplePlugin
    {
        protected override string MenuText => "FindPriorities";
        protected override void Run() => _ = PluginResultWindow.Run(Yabe, "Priorities", "No Objects with priorities found.", 520, 340, (device, id) =>
        {
            if (!device.channel.ReadPropertyRequest(device.BacAdr, id, BacnetPropertyIds.PROP_PRIORITY_ARRAY, out IList<BacnetValue> value)) return null;
            string name = id.ToString();
            if (name.StartsWith("OBJECT_")) name = name.Substring(7);
            string output = null;
            for (int i = 0; i < 16 && i < value.Count; i++)
                if (value[i].Value != null)
                    output = (output ?? name + ":     ") + " " + (i + 1);
            return output == null ? null : new ResultNode(output);
        });
    }

    class ListCovIncrement : IYabeMacPlugin
    {
        public void Init(MainWindow yabe)
        {
            var menu = new MenuItem { Header = "List COV_Increment" };
            void Sub(string text, BacnetObjectTypes[] filter)
            {
                var mi = new MenuItem { Header = text };
                mi.Click += (_, __) => { try { Run(yabe, filter); } catch { } };
                menu.Items.Add(mi);
            }
            Sub("on all Objects", new[] { BacnetObjectTypes.OBJECT_ANALOG_INPUT, BacnetObjectTypes.OBJECT_ANALOG_VALUE,
                BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, BacnetObjectTypes.OBJECT_LOOP, BacnetObjectTypes.OBJECT_PULSE_CONVERTER });
            Sub("on Analog Input Objects", new[] { BacnetObjectTypes.OBJECT_ANALOG_INPUT });
            Sub("on Analog Output Objects", new[] { BacnetObjectTypes.OBJECT_ANALOG_OUTPUT });
            Sub("on Analog Value Objects", new[] { BacnetObjectTypes.OBJECT_ANALOG_VALUE });
            yabe.AddPluginMenuItem(menu);
        }

        static void Run(MainWindow yabe, BacnetObjectTypes[] filter) =>
            _ = PluginResultWindow.Run(yabe, "List of COV_Increment", "No COV_Increments were found.", 815, 600, (device, id) =>
            {
                if (!device.channel.ReadPropertyRequest(device.BacAdr, id, BacnetPropertyIds.PROP_COV_INCREMENT, out IList<BacnetValue> value)) return null;
                string Increment = value[0].Value.ToString();
                string Units = "";
                try
                {
                    device.channel.ReadPropertyRequest(device.BacAdr, id, BacnetPropertyIds.PROP_UNITS, out value);
                    Units = ((BacnetUnitsId)Convert.ToUInt32(value[0].Value)).ToString();
                    if (Units.StartsWith("UNITS_")) Units = Units.Substring(6);
                    Units = PluginResultWindow.TitleCase(Units.Replace("_", " "));
                }
                catch { }
                var n = new ResultNode(PluginResultWindow.ObjectLabel(device, id));
                if (device.channel.ReadPropertyRequest(device.BacAdr, id, BacnetPropertyIds.PROP_DESCRIPTION, out value))
                    n.Add(value[0].Value.ToString());
                n.Add("Value = " + Increment + " " + Units);
                return n;
            }, filter);
    }

    class ListAnalogValues : SimplePlugin
    {
        protected override string MenuText => "List Analog Values";
        protected override void Run() => _ = AnalogValuesWindow.Run(Yabe, new[] { BacnetObjectTypes.OBJECT_ANALOG_VALUE });
    }

    class DeviceReport : SimplePlugin
    {
        protected override string MenuText => "Report of All Devices";
        protected override void Run() => _ = DeviceReportWindow.Run(Yabe);
    }

    class GetDevicesNames : SimplePlugin
    {
        protected override string MenuText => "Get All Devices Names";
        protected override void Run()
        {
            var roots = Yabe.DeviceTree.ItemsSource as IEnumerable<DeviceNode>;
            if (roots == null) return;
            var nodes = new List<DeviceNode>();
            void Collect(IEnumerable<DeviceNode> l) { foreach (var n in l) { if (n.Device != null) nodes.Add(n); Collect(n.Children); } }
            Collect(roots);
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var tn in nodes)
                {
                    try
                    {
                        string Name = tn.Device.ReadObjectName(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, tn.Device.deviceId));
                        if (!string.IsNullOrEmpty(Name))
                        {
                            string title = Name + " [" + tn.Device.deviceId + "] ";
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => tn.Title = title);
                        }
                    }
                    catch { }
                }
            });
        }
    }
}
