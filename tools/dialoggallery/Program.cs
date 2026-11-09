using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Yabe;
using YabeMac;

// Throwaway dialog gallery: boots the real YabeMac.App application, connects to the simulators and renders each
// dialog to a PNG.   usage: DialogGallery outdir [name ...]   (no names = all)
static class Gallery
{
    public static string Out; public static string[] Only;
    static MainWindow main; static BacnetClient comm;
    static BACnetDevice d599, d600, d601, d604;
    static IClassicDesktopStyleApplicationLifetime Life => (IClassicDesktopStyleApplicationLifetime)Application.Current.ApplicationLifetime;

    static BACnetDevice Dev(int port, uint inst) => new BACnetDevice(comm, new BacnetAddress(BacnetAddressTypes.IP, "127.0.0.1:" + port), inst);

    static async Task SelectDevice(string title)
    {
        var tree = main.FindControl<TreeView>("DeviceTree");
        IEnumerable<DeviceNode> Flat(IEnumerable<DeviceNode> n) { foreach (var x in n.ToList()) { yield return x; foreach (var c in Flat(x.Children)) yield return c; } }
        var dn = Flat((IEnumerable<DeviceNode>)tree.ItemsSource).FirstOrDefault(n => n.Device != null && n.Device.deviceId == 599);
        tree.SelectedItem = dn;
        await Task.Delay(2500);
    }

    public static async Task Run()
    {
        try
        {
            Directory.CreateDirectory(Out);
            main = MainWindow.Instance;
            main.Position = new PixelPoint(3000, 3000);
            comm = new BacnetClient(new BacnetIpUdpProtocolTransport(47828, false, false, 1472, "127.0.0.1"), 1000, 3);
            main.AddChannel(comm, "UDP 127.0.0.1:47828", false);
            foreach (int p in new[] { 47809, 47810, 47811, 47814 }) comm.WhoIs(-1, -1, new BacnetAddress(BacnetAddressTypes.IP, "127.0.0.1:" + p));
            await Task.Delay(1500);
            d599 = Dev(47809, 599); d600 = Dev(47810, 600); d601 = Dev(47811, 601); d604 = Dev(47814, 604);
            await SelectDevice("599");

            var cases = new List<(string, Func<object>)>();
            void C(string n, Func<object> f) => cases.Add((n, f));
            C("SearchDialog", () => { var w = new SearchDialog(); w.Show(); return w; });
            C("SettingsDialog", () => { var w = new SettingsDialog(); w.Show(); return w; });
            C("ForeignRegistry", () => { var w = new ForeignRegistry(comm); w.Show(); return w; });
            C("BBMDEditor", () => { var w = new BBMDEditor(comm, d599.BacAdr); w.Show(); return w; });
            C("SourceAddressDialog", () => { var w = new SourceAddressDialog(); w.Show(); return w; });
            C("SCEditor", () => { var w = new SCEditor(); w.Show(); return w; });
            C("CreateObject", () => { var w = new CreateObject(); w.Show(); return w; });
            C("CreateObjectHidden", () => { var w = new CreateObject(false, "Add hidden object", "Add"); w.Show(); return w; });
            C("DCC", () => { var w = new DeviceCommunicationControlDialog(); w.Show(); return w; });
            C("Progress", () => { var w = new ProgressDialog { Title = "Downloading file ..." }; w.Show(); return w; });
            C("PlotY", () => { var pc = new YabeMac.Plot.PlotControl { ShowLegend = false, GradientBackground = true, XTitle = "Date/Time", YTitle = "Values" };
                var se = new YabeMac.Plot.PlotSeries(); double t0 = DateTime.Now.ToOADate(); for (int i = 0; i < 30; i++) se.Add(t0 + i * 60 / 86400.0, 20 + 5 * Math.Sin(i / 4.0)); pc.Series.Add(se);
                var host = new Avalonia.Controls.Grid { Width = 700, Height = 350, Children = { pc } };
                var w = new Avalonia.Controls.Window { Content = host, SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight, CanResize = false }; w.Show(); return w; });
            C("Schedule", () => ScheduleDisplay.Open(d600, new BacnetObjectId(BacnetObjectTypes.OBJECT_SCHEDULE, 1)));
            C("Calendar", () => CalendarEditor.Open(d600, new BacnetObjectId(BacnetObjectTypes.OBJECT_CALENDAR, 1)));
            C("CalEntryDate", () => { var w = new CalendarEditor.CalendarEntryEdit(new BacnetDate(126, 11, 8, 0)); w.Show(); return w; });
            C("CalEntryRange", () => { var w = new CalendarEditor.CalendarEntryEdit(new BacnetDateRange(new BacnetDate(126, 11, 8, 0), new BacnetDate(126, 12, 8, 0))); w.Show(); return w; });
            C("CalEntryWeek", () => { var w = new CalendarEditor.CalendarEntryEdit(new BacnetweekNDay(255, 2, 3)); w.Show(); return w; });
            C("Notification", () => NotificationEditor.Open(d601, new BacnetObjectId(BacnetObjectTypes.OBJECT_NOTIFICATION_CLASS, 1)));
            C("AlarmSummary", () => AlarmSummary.Open(d601));
            C("TrendLog", () => { var w = new TrendLogDisplay(d604, new BacnetObjectId(BacnetObjectTypes.OBJECT_TRENDLOG, 1)); w.Show(); return w; });
            C("MstpCapture", () => { var w = new MstpCapture(); w.Show(); return w; });
            C("PluginResult", () => PluginResultWindow.Run(main, "Yabe Reliability", "No objects with reliability problems", 400, 400, (d, id) => id.type == BacnetObjectTypes.OBJECT_ANALOG_VALUE ? new ResultNode("Setpoint (Analog Value:1)") : null));
            C("AnalogValues", () => AnalogValuesWindow.Run(main, new[] { BacnetObjectTypes.OBJECT_ANALOG_VALUE, BacnetObjectTypes.OBJECT_ANALOG_INPUT }));
            C("DeviceReport", () => DeviceReportWindow.Run(main));
            C("UserCommandStatus", () => { var w = new UserCommandStatus(new List<string> { "127.0.0.1:47809;Sim;ANALOG_VALUE:1;Setpoint;PRESENT_VALUE;22;OK", "127.0.0.1:47809;Sim;BINARY_VALUE:1;Fan Enable;PRESENT_VALUE;1;Error: write access denied" }); w.Show(); return w; });
            C("GlobalCommander", () => { var w = new GlobalCommanderWindow(main); w.Show(); return w; });
            C("GcAdvancedFilter", () => { var w = new GcAdvancedFilterWindow(new object[] { "==", "!=", "<", ">" }); w.Show(); return w; });
            C("GcValues", () => { var w = new PropertyValueListWindow(new List<GcProperty>(), "Present Value"); w.Show(); return w; });
            C("FindPriorities", () => { var w = new FindPrioritiesGlobalWindow(main); w.Show(); return w; });
            C("ObjectSearch", () => Dialogs.Input("Name", "Search object", "Setpoint"));
            C("Msg", () => Dialogs.Message("Operation completed.\nA second line of the message.", "Yabe"));
            C("Confirm", () => Dialogs.Confirm("Delete the selected object permanently?", "Delete"));
            C("Input", () => Dialogs.Input("Enter a value:", "Input", "42"));
            C("DateTimeFlyout", () =>
            {
                var node = new YabeMac.PropertyPanel.PropertyNode { Name = "Time_Of_Day", Kind = YabeMac.PropertyPanel.EditKind.DateTime };
                node.Parts = new YabeMac.PropertyPanel.DateTimeParts { HasDate = true, HasTime = true, Year = 2026, Month = 11, Day = 8, Hour = 14, Minute = 5, Second = 9, Hundredths = 25 };
                var fly = YabeMac.Forms.DateTimeEditor.CreateFlyout(node, _ => { }, () => { });
                var w = new Window { Width = 380, Height = 560, Title = "dtedit", Content = fly.Content };
                w.Show(); return w;
            });

            foreach (var (name, f) in cases)
            {
                if (Only.Length > 0 && !Only.Contains(name)) continue;
                var before = Life.Windows.ToList();
                try
                {
                    var r = f();
                    if (r is Window) { } 
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Out, name + ".err"), ex.ToString()); continue; }
                await Task.Delay(name is "Schedule" or "Calendar" or "Notification" or "AlarmSummary" or "TrendLog" or "PluginResult" or "AnalogValues" or "DeviceReport" or "GlobalCommander" or "FindPriorities" ? 3500 : 900);
                int i = 0;
                foreach (var w in Life.Windows.ToList().Where(w => !before.Contains(w) && w != main))
                {
                    try
                    {
                        w.Position = new PixelPoint(3000, 100);
                        Console.WriteLine("  bg=" + (w.Background == null ? "null" : w.Background.ToString()) + " theme=" + w.ActualThemeVariant);
                        if (w.Background == null || (w.Background is Avalonia.Media.ISolidColorBrush sb0 && sb0.Color.A == 0)) w.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(w.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? "#F3F3F3" : "#202020"));
                        await Task.Delay(300);
                        int pw = Math.Max(1, (int)w.Bounds.Width), ph = Math.Max(1, (int)w.Bounds.Height);
                        var bmp = new RenderTargetBitmap(new PixelSize(pw * 2, ph * 2), new Vector(192, 192));
                        bmp.Render(w);
                        bmp.Save(Path.Combine(Out, name + (i++ == 0 ? "" : "_" + i) + ".png"));
                        Console.WriteLine(name + " " + pw + "x" + ph + " title=" + w.Title);
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(Out, name + ".err"), ex.ToString()); }
                    w.Close();
                }
            }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Out, "gallery.err"), ex.ToString()); }
        Dispatcher.UIThread.Post(() => Environment.Exit(0));
    }
}

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Gallery.Out = args.Length > 0 ? args[0] : "gallery";
        Gallery.Only = args.Skip(1).ToArray();
        return AppBuilder.Configure<YabeMac.App>().UsePlatformDetect().AfterSetup(_ =>
        {
            Dispatcher.UIThread.Post(async () => { await Task.Delay(2500); await Gallery.Run(); }, DispatcherPriority.Background);
        }).StartWithClassicDesktopLifetime(args);
    }
}
