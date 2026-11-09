using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    // Developer aid: YABE_SHOT=<png> runs the real window against a BACnet device at
    // YABE_SHOT_TARGET (default 127.0.0.1:47809), selects the first analog value, subscribes a couple of
    // objects and writes the rendered window to a PNG, then exits. Used to check layout without a display capture.
    public partial class MainWindow
    {
        async void RunShotMode()
        {
            string png = Environment.GetEnvironmentVariable("YABE_SHOT");
            if (string.IsNullOrEmpty(png)) return;
            string target = Environment.GetEnvironmentVariable("YABE_SHOT_TARGET") ?? "127.0.0.1:47809";
            try
            {
                var comm = new BacnetClient(new BacnetIpUdpProtocolTransport(47818, false, false, 1472, "127.0.0.1"), 1000, 3);
                AddChannel(comm, "UDP 127.0.0.1:47818", false);
                comm.WhoIs(-1, -1, new BacnetAddress(BacnetAddressTypes.IP, target));
                await Task.Delay(1500);

                DeviceNode dev = null;
                foreach (var n in Flatten(_deviceRoots)) if (n.Device != null) { dev = n; break; }
                if (dev == null) { Log("SHOT: no device found"); }
                else
                {
                    DeviceTree.SelectedItem = dev;
                    await Task.Delay(2500);
                    var objs = Flatten2(_objectRoots).Where(o => o.ObjectId != null).ToList();
                    foreach (var o in objs.Where(o => o.ObjectId.Value.type == BacnetObjectTypes.OBJECT_ANALOG_INPUT))
                        CreateSubscription(o.Device, o.ObjectId.Value, true, 500);
                    var av = objs.FirstOrDefault(o => o.ObjectId.Value.type == BacnetObjectTypes.OBJECT_ANALOG_VALUE);
                    if (av != null) ObjectTree.SelectedItem = av;
                    await Task.Delay(6000);
                }
                Dispatcher.UIThread.Post(() =>
                {
                    Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2A2C31"));   // ~ window tint over a dark system backdrop (offscreen has no backdrop)
                    var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                    var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
                    bmp.Render(this);
                    bmp.Save(png);
                    Log("SHOT saved " + png);
                    Close();
                });
            }
            catch (Exception ex) { File.WriteAllText(png + ".err", ex.ToString()); Close(); }
        }

        static System.Collections.Generic.IEnumerable<ObjectNode> Flatten2(System.Collections.Generic.IEnumerable<ObjectNode> nodes)
        {
            foreach (var n in nodes.ToList()) { yield return n; foreach (var c in Flatten2(n.Children)) yield return c; }
        }
    }
}
