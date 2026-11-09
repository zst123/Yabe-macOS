using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using System;
using System.IO.BACnet;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Yabe;
using YabeMac.PropertyPanel;

// Visual smoke test of the PropertyTree control: reads an object of the simulator, shows it in a window,
// renders the control to a PNG (RenderTargetBitmap) and exits.   usage: PropView host:port OBJECT_TYPE:instance out.png [edit]
class PvApp : Application
{
    public override void Initialize() { Styles.Add(new FluentTheme()); }
    public override void OnFrameworkInitializationCompleted()
    {
        var a = Program.Args;
        string target = a.Length > 0 ? a[0] : "127.0.0.1:47859";
        string objSpec = a.Length > 1 ? a[1] : "OBJECT_ANALOG_VALUE:1";
        string png = a.Length > 2 ? a[2] : "propview.png";
        bool edit = a.Contains("edit");
        BACnetDevice dev = null;
        var client = new BacnetClient(new BacnetIpUdpProtocolTransport(47848, false, false, 1472, "127.0.0.1"), 1000, 3);
        client.OnIam += (s, adr, id, apdu, seg, vendor) => dev = new BACnetDevice(s, adr, id, apdu, seg, vendor);
        client.Start();
        client.WhoIs(-1, -1, new BacnetAddress(BacnetAddressTypes.IP, target));
        for (int i = 0; i < 50 && dev == null; i++) Thread.Sleep(100);
        PropertyModel.TryParseObjectId(objSpec, out var oid);
        var res = PropertyModel.Read(dev, oid, ReadOptions.FromSettings(false));
        var tree = new PropertyTree { Width = 300, Height = 520 };
        tree.SetNodes(res.Nodes, false);
        var win = new Window { Width = 320, Height = 540, Content = tree, Title = "propview" };
        win.Show();
        // "dtedit": also render the date/time editor (YabeMac.Forms.DateTimeEditor) for the first Date/Time/DateTime node
        var dtNode = a.Contains("dtedit") ? res.Nodes.FirstOrDefault(n => n.Kind == EditKind.Date || n.Kind == EditKind.Time || n.Kind == EditKind.DateTime) : null;
        if (dtNode != null)
        {
            if (a.Contains("mix")) dtNode.Parts = new DateTimeParts { HasDate = true, HasTime = true, Year = 255, Month = 13, Day = 32, Hour = 7, Minute = 30, Second = 255, Hundredths = 0 };
            if (a.Contains("specific")) dtNode.Parts = new DateTimeParts { HasDate = true, HasTime = true, Year = 2026, Month = 11, Day = 8, Hour = 14, Minute = 5, Second = 9, Hundredths = 25 };
            var ed = YabeMac.Forms.DateTimeEditor.CreateFlyout(dtNode, _ => { }, () => { }).Content as Control;
            var ew = new Window { Width = 380, Height = 560, Content = new Border { Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2B2B2B")), Child = ed }, Title = "dtedit", SizeToContent = SizeToContent.Manual };
            ew.Show();
            Dispatcher.UIThread.Post(() =>
            {
                var rt = new RenderTargetBitmap(new PixelSize(380 * 2, 560 * 2), new Vector(192, 192));
                rt.Render(ew.Content as Visual);
                rt.Save(png.Replace(".png", ".editor.png"));
                Console.WriteLine("saved editor " + png.Replace(".png", ".editor.png"));
            }, DispatcherPriority.Background);
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (edit) tree.EditSelected();
            Dispatcher.UIThread.Post(() =>
            {
                var rtb = new RenderTargetBitmap(new PixelSize(300 * 2, 520 * 2), new Vector(192, 192));
                rtb.Render(tree);
                rtb.Save(png);
                Console.WriteLine("saved " + png);
                client.Dispose();
                Environment.Exit(0);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
        base.OnFrameworkInitializationCompleted();
    }
}

class Program
{
    public static string[] Args;
    [STAThread]
    static void Main(string[] args)
    {
        Args = args;
        AppBuilder.Configure<PvApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
