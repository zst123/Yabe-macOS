using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Yabe;
using YabeMac;
using YabeMac.PropertyPanel;

// Headless tests of the commandable-object features against tools/sim/sim_commandable.py (device 603, 127.0.0.1:47813):
// property tree priority array / write / relinquish, Global Commander write, Find Priorities Global, device control, time sync.
// usage: dotnet run --project tools/cmdtest [-- host:port outdir]     (outdir receives the rendered PNGs)
class CmdApp : Application
{
    public override void Initialize() { Styles.Add(new FluentTheme()); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l) l.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Task.Run(() => { int rc; try { rc = CmdTest.Run(CmdTest.Args); } catch (Exception ex) { Console.WriteLine("EXCEPTION " + ex); rc = 2; } Environment.Exit(rc); });
        base.OnFrameworkInitializationCompleted();
    }
}

class CmdTest
{
    public static string[] Args;
    static int fails = 0, passes = 0; public static string OutDir = Path.GetTempPath();
    static void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  ok   " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }
    [STAThread] static void Main(string[] args) { Args = args; AppBuilder.Configure<CmdApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args); }

    static T UI<T>(Func<T> f) => Dispatcher.UIThread.InvokeAsync(f).GetAwaiter().GetResult();
    static void Render(Control c, int w, int h, string png)
    {
        UI(() =>
        {
            var rt = new RenderTargetBitmap(new PixelSize(w * 2, h * 2), new Vector(192, 192));
            rt.Render(c); rt.Save(png); return 0;
        });
        Console.WriteLine("    rendered " + png);
    }

    static PropertyNode Find(PropertyReadResult r, BacnetPropertyIds id) => r.Nodes.First(n => n.PropertyId == (uint)id);

    public static int Run(string[] args)
    {
        string target = args.Length > 0 ? args[0] : "127.0.0.1:47813";
        string outDir = args.Length > 1 ? args[1] : Path.GetTempPath(); OutDir = outDir;
        BACnetDevice dev = null;
        var client = new BacnetClient(new BacnetIpUdpProtocolTransport(47868, false, false, 1472, "127.0.0.1"), 1000, 3);
        client.OnIam += (s, adr, id, apdu, seg, vendor) => { if (dev == null) dev = new BACnetDevice(s, adr, id, apdu, seg, vendor); };
        client.Start();
        client.WhoIs(-1, -1, new BacnetAddress(BacnetAddressTypes.IP, target));
        for (int i = 0; i < 50 && dev == null; i++) Thread.Sleep(100);
        if (dev == null) { Console.WriteLine("no commandable simulator at " + target + " (tools/sim/sim_commandable.py)"); return 1; }
        Console.WriteLine($"== device {dev.deviceId} ==");
        dev.ReadObjectList(out var objs, out _);
        dev.ReadAllObjectsName();
        var opt = ReadOptions.FromSettings(false);
        AppSettings.Default.DefaultWritePriority = "LOWEST_AND_DEFAULT";

        PropertyModelTests(dev, opt);
        GlobalCommanderTests(dev, opt, outDir);
        FindPrioritiesTests(dev, opt, outDir);
        DeviceControlTests(client, dev, outDir);

        Console.WriteLine($"\n{passes} passed, {fails} failed");
        client.Dispose();
        return fails == 0 ? 0 : 1;
    }

    static void Relinquish16(BACnetDevice dev, BacnetObjectId o)
    {
        for (uint p = 1; p <= 16; p++)
        {
            dev.channel.WritePriority = p;
            dev.channel.WritePropertyRequest(dev.BacAdr, o, BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null) });
        }
        dev.channel.WritePriority = 16;
    }

    // ------------------------------------------------------------------------------------------------------
    static void PropertyModelTests(BACnetDevice dev, ReadOptions opt)
    {
        Console.WriteLine("== property tree: priority array (AO1 / BO1 / MSO1 / AV1) ==");
        foreach (var o in new[] { new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1), new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 2), new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1), new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1), new BacnetObjectId(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, 1) })
            Relinquish16(dev, o);

        var ao = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);
        var rr = PropertyModel.Read(dev, ao, opt);
        Check(rr.Error == null, "AO1 read ok " + rr.Error);
        var pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        Check(pa.IsArray && pa.Children.Count == 16, "AO1 priority array has 16 slots (" + pa.Children.Count + ")");
        Check(pa.Expanded, "priority array expanded by default");
        Check(pa.Children.All(c => c.IsNullValue), "all 16 slots NULL initially: " + string.Join("|", pa.Children.Take(3).Select(c => c.Display)));
        Check(pa.Children[7].Name.Contains("8"), "slot 8 labelled '" + pa.Children[7].Name + "'");
        Check(Find(rr, BacnetPropertyIds.PROP_RELINQUISH_DEFAULT) != null && Find(rr, BacnetPropertyIds.PROP_CURRENT_COMMAND_PRIORITY) != null, "Relinquish_Default and Current_Command_Priority shown");
        var pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(PropertyModel.CanRelinquish(pv), "Present_Value offers relinquish");
        Check(pa.Children.All(c => !PropertyModel.CanRelinquish(c)), "NULL slots do not offer relinquish");

        // write PV at default priority (16)
        PropertyModel.TryParse(pv, "12.5", out var v, out var err);
        var w = PropertyModel.Write(dev, ao, pv, v);
        Check(w.Ok, "write Present_Value 12.5 at default priority " + w.Error);
        rr = PropertyModel.Read(dev, ao, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pa.Children[15].Display == "12.5" && pa.Children.Take(15).All(c => c.IsNullValue), "slot 16 = " + pa.Children[15].Display + ", others NULL");
        Check(pv.Display == "12.5", "PV = " + pv.Display);

        // write a priority array element (slot 8)
        Check(PropertyModel.CanEdit(pa.Children[7]), "NULL slot 8 is editable");
        PropertyModel.TryParse(pa.Children[7], "40", out v, out err);
        Check(v is float, "slot 8 parsed as real (tag of the array element template)");
        w = PropertyModel.Write(dev, ao, pa.Children[7], v);
        Check(w.Ok, "write priority array slot 8 = 40 " + w.Error);
        rr = PropertyModel.Read(dev, ao, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pa.Children[7].Display == "40" && pv.Display == "40", "slot 8 = 40, PV = 40 (highest priority wins): " + pv.Display);
        var ccp = Find(rr, BacnetPropertyIds.PROP_CURRENT_COMMAND_PRIORITY);
        Check(ccp.Display == "8", "Current_Command_Priority = " + ccp.Display);
        Check(PropertyModel.CanRelinquish(pa.Children[7]), "set slot offers relinquish");

        // higher priority slot 3
        PropertyModel.TryParse(pa.Children[2], "75", out v, out err);
        Check(PropertyModel.Write(dev, ao, pa.Children[2], v).Ok, "write slot 3 = 75");
        rr = PropertyModel.Read(dev, ao, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Display == "75", "PV = 75 (slot 3)");

        // relinquish slot 3 -> falls back to 40; relinquish slot 8 -> 12.5; relinquish slot 16 -> Relinquish_Default 0
        w = PropertyModel.Relinquish(dev, ao, pa.Children[2]);
        Check(w.Ok, "relinquish slot 3 " + w.Error);
        rr = PropertyModel.Read(dev, ao, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pa.Children[2].IsNullValue && pv.Display == "40", "slot 3 NULL again, PV = " + pv.Display);
        Check(PropertyModel.Relinquish(dev, ao, pa.Children[7]).Ok, "relinquish slot 8");
        rr = PropertyModel.Read(dev, ao, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Display == "12.5", "PV back to 12.5 = " + pv.Display);
        // relinquish through Present_Value node (writes NULL at default priority 16)
        Check(PropertyModel.Relinquish(dev, ao, pv).Ok, "relinquish via Present_Value node (priority 16)");
        rr = PropertyModel.Read(dev, ao, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pa.Children.All(c => c.IsNullValue) && pv.Display == "0", "all slots NULL, PV = Relinquish_Default = " + pv.Display);

        // default write priority setting honoured
        AppSettings.Default.DefaultWritePriority = "MANUAL_OPERATOR";
        PropertyModel.TryParse(pv, "9", out v, out err);
        Check(PropertyModel.Write(dev, ao, pv, v).Ok, "write PV with DefaultWritePriority=MANUAL_OPERATOR");
        rr = PropertyModel.Read(dev, ao, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        Check(pa.Children[7].Display == "9", "value landed in slot 8: " + pa.Children[7].Display);
        AppSettings.Default.DefaultWritePriority = "LOWEST_AND_DEFAULT";
        PropertyModel.Relinquish(dev, ao, pa.Children[7]);

        // keeping a representation for the screenshot: slots 1, 8, 16 set
        foreach (var (slot, val) in new[] { (1, "5"), (8, "40"), (16, "12.5") })
        {
            rr = PropertyModel.Read(dev, ao, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
            PropertyModel.TryParse(pa.Children[slot - 1], val, out v, out err); PropertyModel.Write(dev, ao, pa.Children[slot - 1], v);
        }
        rr = PropertyModel.Read(dev, ao, opt);
        var tree = UI(() => { var t = new PropertyTree { Width = 340, Height = 700 }; t.SetNodes(rr.Nodes, false); var win = new Window { Width = 360, Height = 720, Content = t, Title = "AO1" }; win.Show(); return win; });
        Thread.Sleep(600);
        Render((Control)UI(() => tree.Content), 340, 700, Path.Combine(OutDir, "cmd_ao1_tree.png"));
        UI(() => { tree.Close(); return 0; });
        Relinquish16(dev, ao);

        // binary output: choice values, priority array of choices, relinquish
        var bo = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1);
        rr = PropertyModel.Read(dev, bo, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pa.Children.Count == 16 && pv.Kind == EditKind.Choice, "BO1: 16 slots, PV choice: " + pv.Display);
        Check(PropertyModel.TryParse(pa.Children[4], "1 : Active", out v, out err), "BO1 slot 5 parse 'active' " + err);
        w = PropertyModel.Write(dev, bo, pa.Children[4], v);
        Check(w.Ok, "BO1 write slot 5 = active " + w.Error);
        rr = PropertyModel.Read(dev, bo, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Console.WriteLine("    BO1 slot5 = '" + pa.Children[4].Display + "' PV = '" + pv.Display + "'");
        Check(pa.Children[4].Display.StartsWith("1") && pv.Display.StartsWith("1"), "BO1 slot 5 active -> PV active");
        Check(PropertyModel.Relinquish(dev, bo, pa.Children[4]).Ok, "BO1 relinquish slot 5");
        rr = PropertyModel.Read(dev, bo, opt); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Display.StartsWith("0"), "BO1 PV back to inactive: " + pv.Display);

        // multi-state output
        var mso = new BacnetObjectId(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, 1);
        rr = PropertyModel.Read(dev, mso, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pa.Children.Count == 16, "MSO1: 16 slots; PV = " + pv.Display + " kind " + pv.Kind);
        Console.WriteLine("    MSO1 slot kinds: " + pa.Children[0].Kind + " " + pa.Children[0].Display);
        PropertyModel.TryParse(pa.Children[9], "3", out v, out err);
        w = PropertyModel.Write(dev, mso, pa.Children[9], v);
        Check(w.Ok, "MSO1 write slot 10 = 3 " + w.Error);
        rr = PropertyModel.Read(dev, mso, opt); pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Display.StartsWith("3"), "MSO1 PV = " + pv.Display);
        Check(PropertyModel.Relinquish(dev, mso, pa.Children[9]).Ok, "MSO1 relinquish slot 10");
        rr = PropertyModel.Read(dev, mso, opt); pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Display.StartsWith("1"), "MSO1 PV back to relinquish default 1: " + pv.Display);

        // error from the device: direct write to the read-only Priority_Array property
        var r2 = PropertyModel.Read(dev, ao, opt); var pa2 = Find(r2, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        var dw = DirectWrite(dev, ao, BacnetPropertyIds.PROP_PRIORITY_ARRAY, 3, 5f);
        Console.WriteLine("    direct write to Priority_Array[3] -> " + dw);
        Check(!dw.StartsWith("OK") && dw.Length > 0, "device rejection of Priority_Array element write is an error text, not an exception");
    }

    static string DirectWrite(BACnetDevice dev, BacnetObjectId o, BacnetPropertyIds p, uint idx, float v)
    {
        try { dev.channel.WritePriority = 16; return dev.channel.WritePropertyRequest(dev.BacAdr, o, p, new[] { new BacnetValue(v) }, 0, idx) ? "OK" : "false"; }
        catch (Exception ex) { return PropertyModel.Friendly(ex); }
    }

    // ------------------------------------------------------------------------------------------------------
    static GcProperty GcLoad(GcPoint point, BACnetDevice dev, BacnetPropertyIds pid)
    {
        // same as GlobalCommanderWindow.PopulatePropertiesForPoint
        var properties = new[] { new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_ALL, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL) };
        dev.channel.ReadPropertyMultipleRequest(dev.BacAdr, point.ObjectID, properties, out var multi);
        point.Properties.Clear();
        foreach (var pv in multi[0].values)
        {
            var id = (BacnetPropertyIds)pv.property.propertyIdentifier;
            var prop = new GcProperty(point, id);
            if (pv.value != null)
            {
                var bv = pv.value.ToList(); prop.Values.AddRange(bv);
                for (int i = 0; i < bv.Count; i++) prop.ValueTypes.Add(bv[i].Value != null ? bv[i].Value.GetType() : null);
            }
            point.Properties.Add(prop);
        }
        return point.Properties.First(p => p.PropertyID == pid);
    }

    static void GlobalCommanderTests(BACnetDevice dev, ReadOptions opt, string outDir)
    {
        Console.WriteLine("== Global Commander: write Present_Value / priority array (GcProperty) ==");
        var win = UI(() => new GlobalCommanderWindow(null));
        var gd = new GcDevice(dev, win);
        var ao = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 2);
        var pt = new GcPoint(gd, ao); gd.Points.Add(pt);
        Relinquish16(dev, ao);
        var pv = GcLoad(pt, dev, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(win.DetermineWritePriority() == 8, "default Global Commander priority = 8");
        string err = null;
        try { pv.ValueObjectString = "61.5"; } catch (Exception ex) { err = ex.Message; }
        Check(err == null, "write PV 61.5 at priority 8 " + err);
        var r = PropertyModel.Read(dev, ao, opt); var pa = Find(r, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        Check(pa.Children[7].Display == "61.5", "value in slot 8: " + pa.Children[7].Display + "; GC shows " + pv.ValueObjectString);

        // change the combo priority to 5
        UI(() => { var cbo = (ComboBox)typeof(GlobalCommanderWindow).GetField("cboPriority", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(win); cbo.SelectedIndex = 4; return 0; });
        Check(win.DetermineWritePriority() == 5, "priority combo 5 -> DetermineWritePriority = " + win.DetermineWritePriority());
        pv = GcLoad(pt, dev, BacnetPropertyIds.PROP_PRESENT_VALUE);
        try { pv.ValueObjectString = "11"; err = null; } catch (Exception ex) { err = ex.Message; }
        r = PropertyModel.Read(dev, ao, opt); pa = Find(r, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        Check(err == null && pa.Children[4].Display == "11", "write PV 11 at priority 5: slot5 = " + pa.Children[4].Display + " " + err);

        // write the priority array as a whole: only changed elements are sent
        var paProp = GcLoad(pt, dev, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        string cur = paProp.ValueObjectString;
        Console.WriteLine("    GC priority array text: " + cur);
        Check(paProp.Values.Count == 16, "GC priority array has 16 values");
        var parts = cur.Trim('[', ']').Split(',').Select(s => s.Trim()).ToArray();
        Check(parts.Length == 16, "array text splits to 16 elements");
        parts[1] = "22";        // slot 2 -> 22
        parts[7] = "null";      // slot 8 -> relinquish
        parts[15] = "33";       // slot 16 -> 33
        try { paProp.ValueObjectString = "[" + string.Join(",", parts) + "]"; err = null; } catch (Exception ex) { err = ex.Message; }
        Check(err == null, "write priority array text " + err);
        r = PropertyModel.Read(dev, ao, opt); pa = Find(r, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        Check(pa.Children[1].Display == "22" && pa.Children[7].IsNullValue && pa.Children[15].Display == "33" && pa.Children[4].Display == "11",
              $"slots: 2={pa.Children[1].Display} 5={pa.Children[4].Display} 8={pa.Children[7].Display} 16={pa.Children[15].Display}");
        Check(paProp.ValueObjectString.Contains("22"), "GC re-read priority array after write");

        // wrong element count
        try { paProp.ValueObjectString = "[1,2,3]"; err = null; } catch (Exception ex) { err = ex.Message; }
        Check(err != null && err.Contains("incorrect number"), "wrong element count rejected: " + (err == null ? "" : err.Substring(0, Math.Min(err.Length, 80))));

        // invalid value
        pv = GcLoad(pt, dev, BacnetPropertyIds.PROP_PRESENT_VALUE);
        try { pv.ValueObjectString = "abc"; err = null; } catch (Exception ex) { err = ex.Message; }
        Check(err != null, "non-numeric value rejected: " + (err == null ? "" : err.Substring(0, Math.Min(err.Length, 110))));

        // write to a nonexistent object -> device error text as exception text (the UI shows it in a message box)
        var ghost = new GcPoint(gd, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 4000));
        var gprop = new GcProperty(ghost, BacnetPropertyIds.PROP_PRESENT_VALUE);
        gprop.Values.Add(new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 1f)); gprop.ValueTypes.Add(typeof(float));
        try { gprop.ValueObjectString = "5"; err = null; } catch (Exception ex) { err = ex.Message; }
        Console.WriteLine("    write to unknown object -> " + err);
        Check(err != null && err.Contains("UNKNOWN_OBJECT"), "device error surfaced as exception text (caught by UI)");

        // binary output through GC
        var bo = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1);
        var bpt = new GcPoint(gd, bo); Relinquish16(dev, bo);
        var bpv = GcLoad(bpt, dev, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Console.WriteLine("    BO1 GC PV = " + bpv.ValueObjectString + " tag " + bpv.Values[0].Tag);
        try { bpv.ValueObjectString = "1"; err = null; } catch (Exception ex) { err = ex.Message; }
        r = PropertyModel.Read(dev, bo, opt);
        Check(err == null && Find(r, BacnetPropertyIds.PROP_PRESENT_VALUE).Display.StartsWith("1"), "GC write BO1 = 1 -> " + Find(r, BacnetPropertyIds.PROP_PRESENT_VALUE).Display + " " + err);

        // all-NULL priority array of a binary output: the type has to come from Present_Value (Enumerated), not be guessed as Unsigned
        Relinquish16(dev, bo);
        bpv = GcLoad(bpt, dev, BacnetPropertyIds.PROP_PRESENT_VALUE);
        var bpa = GcLoad(bpt, dev, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        var el = Enumerable.Repeat("null", 16).ToArray(); el[11] = "1";
        try { bpa.ValueObjectString = "[" + string.Join(",", el) + "]"; err = null; } catch (Exception ex) { err = ex.Message; }
        r = PropertyModel.Read(dev, bo, opt); pa = Find(r, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        Check(err == null && pa.Children[11].Display.StartsWith("1"), "GC all-NULL BO1 priority array: slot 12 = 1 -> '" + pa.Children[11].Display + "' " + err);
        var aoAll = new GcPoint(gd, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1)); Relinquish16(dev, aoAll.ObjectID);
        var apa = GcLoad(aoAll, dev, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        el = Enumerable.Repeat("null", 16).ToArray(); el[0] = "7.5";
        try { apa.ValueObjectString = "[" + string.Join(",", el) + "]"; err = null; } catch (Exception ex) { err = ex.Message; }
        r = PropertyModel.Read(dev, aoAll.ObjectID, opt); pa = Find(r, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        Check(err == null && pa.Children[0].Display == "7.5", "GC all-NULL AO1 priority array: slot 1 = 7.5 -> '" + pa.Children[0].Display + "' " + err);
        Relinquish16(dev, aoAll.ObjectID);
        Relinquish16(dev, bo);
        // GC priority-array relinquish of everything the Global Commander wrote
        UI(() => { win.Close(); return 0; });
        Relinquish16(dev, ao);
    }

    // ------------------------------------------------------------------------------------------------------
    static void FindPrioritiesTests(BACnetDevice dev, ReadOptions opt, string outDir)
    {
        Console.WriteLine("== Find Priorities Global ==");
        var ao1 = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 1);
        var ao2 = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, 2);
        var bo1 = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_OUTPUT, 1);
        foreach (var o in new[] { ao1, ao2, bo1, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1), new BacnetObjectId(BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, 1) }) Relinquish16(dev, o);
        void W(BacnetObjectId o, uint prio, object val)
        {
            dev.channel.WritePriority = prio;
            Check(dev.channel.WritePropertyRequest(dev.BacAdr, o, BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { val is BacnetValue bv ? bv : new BacnetValue(val) }), $"setup: {o} prio {prio} = {val}");
            dev.channel.WritePriority = 16;
        }
        W(ao1, 8, 40f); W(ao1, 16, 12.5f); W(ao2, 3, 75f);
        W(bo1, 8, new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, (uint)1));

        var winT = typeof(FindPrioritiesGlobalWindow);
        var devExportT = winT.GetNestedType("DevExport", BindingFlags.NonPublic | BindingFlags.Public);
        var pointT = winT.GetNestedType("PointExport", BindingFlags.NonPublic | BindingFlags.Public);
        var populate = winT.GetMethod("PopulatePointsForDevice", BindingFlags.NonPublic | BindingFlags.Static);
        var buildTree = winT.GetMethod("BuildTree", BindingFlags.NonPublic | BindingFlags.Static);
        var buildCsv = winT.GetMethod("BuildCsv", BindingFlags.NonPublic | BindingFlags.Static);
        Check(populate != null && buildTree != null && buildCsv != null, "FindPrioritiesGlobalWindow static helpers found");

        var de = Activator.CreateInstance(devExportT);
        devExportT.GetField("Device").SetValue(de, dev);
        devExportT.GetField("Name").SetValue(de, dev.ReadObjectName(new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, dev.deviceId)) + " [" + dev.deviceId + "] ");

        // default filter: all priorities except 8
        var filter = Enumerable.Range(0, 16).Select(i => i != 7).ToArray();
        populate.Invoke(null, new object[] { de, filter });
        var pts = ((IEnumerable)devExportT.GetField("Points").GetValue(de)).Cast<object>().ToList();
        string PName(object p) => (string)pointT.GetField("Name").GetValue(p);
        bool[] Set(object p) => (bool[])pointT.GetField("aPriosSet").GetValue(p);
        string[] Vals(object p) => (string[])pointT.GetField("aValues").GetValue(p);
        Console.WriteLine("    filter without 8 -> " + string.Join(" ; ", pts.Select(p => PName(p) + " prios " + string.Join(",", Enumerable.Range(0, 16).Where(i => Set(p)[i]).Select(i => i + 1)))));
        Check(pts.Count == 2 && pts.Any(p => PName(p).Contains("OBJECT_ANALOG_OUTPUT:1")) && pts.Any(p => PName(p).Contains("OBJECT_ANALOG_OUTPUT:2")), "default filter lists AO1 (prio 16) and AO2 (prio 3), not BO1 (prio 8 only)");
        var p1 = pts.First(p => PName(p).Contains("ANALOG_OUTPUT:1"));
        Check(Set(p1)[15] && !Set(p1)[7] && Vals(p1)[15] == "12.5", "AO1: only prio 16 (value " + Vals(p1)[15] + "); prio 8 filtered out");

        // with all priorities
        filter = Enumerable.Repeat(true, 16).ToArray();
        populate.Invoke(null, new object[] { de, filter });
        pts = ((IEnumerable)devExportT.GetField("Points").GetValue(de)).Cast<object>().ToList();
        Check(pts.Count == 3, "all priorities -> 3 objects (AO1, AO2, BO1): " + pts.Count);
        p1 = pts.First(p => PName(p).Contains("ANALOG_OUTPUT:1"));
        Check(Set(p1)[7] && Vals(p1)[7] == "40" && Set(p1)[15] && Vals(p1)[15] == "12.5", "AO1 prios 8=" + Vals(p1)[7] + " and 16=" + Vals(p1)[15]);
        Check(!pts.Any(p => PName(p).Contains("ANALOG_VALUE") || PName(p).Contains("MULTI_STATE")), "objects with all-NULL arrays are not listed");

        // tree
        var pointList = Activator.CreateInstance(typeof(List<>).MakeGenericType(pointT));
        foreach (var p in pts) ((IList)pointList).Add(p);
        var roots = (List<FpgNode>)buildTree.Invoke(null, new object[] { pointList, true, true });
        Check(roots.Count == 1, "one device root: " + roots[0].Title);
        var titles = roots[0].Children.Select(c => c.Title).ToList();
        Console.WriteLine("    tree: " + string.Join(" | ", titles));
        Check(titles.Any(t => t == "Prio: 8 Manual Operator Value: 40") && titles.Any(t => t == "Prio: 16 Available Value: 12.5") && titles.Any(t => t == "Prio: 3 Available Value: 75"), "tree lists priority rows with names and values");
        var roots2 = (List<FpgNode>)buildTree.Invoke(null, new object[] { pointList, false, false });
        Check(roots2[0].Children.Any(t => t.Title == "Prio: 8") && !roots2[0].Children.Any(t => t.Title.Contains("Value")), "tree without names/values: 'Prio: 8'");

        // csv
        string csv = (string)buildCsv.Invoke(null, new object[] { pointList, true, true });
        Console.WriteLine(csv.Replace("\r\n", "\n"));
        var lines = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Check(lines.Length == 4, "csv: header + 3 rows");
        Check(lines[0].StartsWith("Device;Object;1 - Manual-Life Safety;2 - Automatic-Life Safety;") && lines[0].Split(';').Length == 18, "csv header: " + lines[0].Substring(0, 70) + "...");
        Check(lines.All(l => l.Split(';').Length == 18), "every csv line has 18 columns");
        var ao1Line = lines.First(l => l.Contains("ANALOG_OUTPUT:1")).Split(';');
        Check(ao1Line[2 + 7] == "40" && ao1Line[2 + 15] == "12.5" && ao1Line[2] == "", "csv AO1: prio 8 = 40, prio 16 = 12.5, prio 1 empty");
        string csvX = (string)buildCsv.Invoke(null, new object[] { pointList, false, false });
        var l2 = csvX.Replace("\r\n", "\n").Split('\n');
        Check(l2[0] == "Device;Object;1;2;3;4;5;6;7;8;9;10;11;12;13;14;15;16" && l2[1].Contains(";x"), "csv without names/values: header '" + l2[0] + "', marks 'x'");
        File.WriteAllText(Path.Combine(OutDir, "cmd_find_priorities.csv"), csv);

        // render the real window populated with the result tree
        var win = UI(() => { var w = new FindPrioritiesGlobalWindow(null); w.Show(); return w; });
        UI(() =>
        {
            var nodes = (System.Collections.ObjectModel.ObservableCollection<FpgNode>)typeof(FindPrioritiesGlobalWindow).GetField("nodes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(win);
            foreach (var r in roots) { r.IsExpanded = true; nodes.Add(r); }
            var items = (System.Collections.ObjectModel.ObservableCollection<GcItem>)typeof(FindPrioritiesGlobalWindow).GetField("deviceItems", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(win);
            items.Add(new GcItem { Name = "Dev", Text = (string)devExportT.GetField("Name").GetValue(de), Tag = de });
            return 0;
        });
        Thread.Sleep(800);
        UI(() => { win.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2A2C31")); return 0; });
        Render(win, 1234, 721, Path.Combine(OutDir, "cmd_find_priorities.png"));
        UI(() => { win.Close(); return 0; });
        foreach (var o in new[] { ao1, ao2, bo1 }) Relinquish16(dev, o);
    }

    // ------------------------------------------------------------------------------------------------------
    static void DeviceControlTests(BacnetClient client, BACnetDevice dev, string outDir)
    {
        Console.WriteLine("== Device control / time synchronize ==");
        // the exact calls of MainWindow.OnDeviceControl / GlobalCommanderWindow.DeviceControl / SyncTime
        string Try(Func<bool> f) { try { return f() ? "OK" : "FALSE"; } catch (Exception ex) { return "ERR " + ex.Message; } }
        string r;
        r = Try(() => dev.channel.ReinitializeRequest(dev.BacAdr, BacnetReinitializedStates.BACNET_REINIT_WARMSTART, "secret"));
        Check(r == "OK", "ReinitializeDevice WARMSTART with the correct password -> " + r);
        r = Try(() => dev.channel.ReinitializeRequest(dev.BacAdr, BacnetReinitializedStates.BACNET_REINIT_COLDSTART, ""));
        Check(r == "OK", "ReinitializeDevice COLDSTART without password -> " + r);
        r = Try(() => dev.channel.ReinitializeRequest(dev.BacAdr, BacnetReinitializedStates.BACNET_REINIT_ACTIVATE_CHANGES, "wrong"));
        Console.WriteLine("    wrong password -> " + r);
        Check(r.StartsWith("ERR") && r.Contains("PASSWORD_FAILURE"), "device rejects wrong password; error text from the device is available (" + r + ")");
        r = Try(() => dev.channel.DeviceCommunicationControlRequest(dev.BacAdr, 1, 1, ""));
        Check(r == "OK", "DeviceCommunicationControl disable, 1 min -> " + r);
        r = Try(() => dev.channel.DeviceCommunicationControlRequest(dev.BacAdr, 0, 0, ""));
        Check(r == "OK", "DeviceCommunicationControl enable -> " + r);
        r = Try(() => dev.channel.DeviceCommunicationControlRequest(dev.BacAdr, 0, 0, "bad"));
        Check(r.StartsWith("ERR") && r.Contains("PASSWORD_FAILURE"), "DCC bad password -> " + r);
        // unsupported-by-device behaviour: ask a device that does not answer (no crash, timeout error text)
        var dead = new BacnetAddress(BacnetAddressTypes.IP, "127.0.0.1:47999");
        r = Try(() => client.ReinitializeRequest(dead, BacnetReinitializedStates.BACNET_REINIT_WARMSTART, ""));
        Console.WriteLine("    unreachable device -> " + r);
        Check(r.StartsWith("ERR") || r == "FALSE", "unreachable device: exception/false, no crash");
        r = Try(() => { dev.channel.SynchronizeTime(dev.BacAdr, DateTime.Now, false); return true; });
        Check(r == "OK", "SynchronizeTime local -> " + r);
        r = Try(() => { dev.channel.SynchronizeTime(dev.BacAdr, DateTime.Now.ToUniversalTime(), true); return true; });
        Check(r == "OK", "SynchronizeTime UTC -> " + r);
        Thread.Sleep(300);

        // dialog -> request mapping (DeviceCommunicationControlDialog)
        var dlg = UI(() => { var d = new DeviceCommunicationControlDialog(); d.Show(); return d; });
        UI(() => { d(dlg); return 0; });
        Thread.Sleep(500);
        UI(() => { dlg.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2A2C31")); return 0; });
        Render(dlg, (int)UI(() => dlg.Bounds.Width), (int)UI(() => dlg.Bounds.Height), Path.Combine(OutDir, "cmd_device_control_dialog.png"));
        UI(() => { dlg.Close(); return 0; });
    }
    static void d(DeviceCommunicationControlDialog dlg) { dlg.IsReinitialize = true; dlg.Password = "secret"; }
}
