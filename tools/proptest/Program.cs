using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Reflection;
using System.Threading;
using Yabe;
using YabeMac;
using YabeMac.PropertyPanel;

// Headless test of the Properties panel logic (read / format / parse / write) against the simulator
// (127.0.0.1:47809, device 599).  Usage: dotnet run --project tools/proptest [-- host:port]
class PropTest
{
    static int fails = 0, passes = 0;
    static void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  ok   " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }

    static void Dump(IList<PropertyNode> nodes, int indent = 0, bool all = true)
    {
        foreach (var n in nodes)
        {
            Console.WriteLine($"{new string(' ', indent * 2)}{n.Name} = {n.Display} [{n.Kind}{(n.ReadOnly ? ",ro" : "")}{(n.Nullable ? ",null-ok" : "")}]");
            if (n.HasChildren && (n.Expanded || all) && indent < 1) Dump(n.Children.Take(20).ToList(), indent + 1, all);
        }
    }

    static PropertyNode Find(PropertyReadResult r, BacnetPropertyIds id) => r.Nodes.First(n => n.PropertyId == (uint)id);

    static int Main(string[] args)
    {
        Console.WriteLine("== offline: parse / format / reflection ==");
        OfflineTests();
        string target = args.Length > 0 ? args[0] : "127.0.0.1:47809";
        BACnetDevice dev = null;
        var client = new BacnetClient(new BacnetIpUdpProtocolTransport(47838, false, false, 1472, "127.0.0.1"), 1000, 3);
        client.OnIam += (s, adr, id, apdu, seg, vendor) => { dev = new BACnetDevice(s, adr, id, apdu, seg, vendor); };
        client.Start();
        client.WhoIs(-1, -1, new BacnetAddress(BacnetAddressTypes.IP, target));
        for (int i = 0; i < 50 && dev == null; i++) Thread.Sleep(100);
        if (dev == null) { Console.WriteLine("no simulator at " + target + " - online tests skipped"); return fails == 0 ? 0 : 1; }
        Console.WriteLine($"== online: device {dev.deviceId} ==");
        dev.ReadObjectList(out var objs, out _);
        dev.ReadAllObjectsName();

        var opt = ReadOptions.FromSettings(false);
        foreach (var o in objs)
        {
            Console.WriteLine($"--- {o} \"{dev.GetObjectName(o)}\"");
            var r = PropertyModel.Read(dev, o, opt);
            if (r.Error != null) { Console.WriteLine("  ERROR " + r.Error); fails++; continue; }
            Dump(r.Nodes);
        }

        // ---- AV1: write present value, check priority array, relinquish
        var av = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1);
        Console.WriteLine("== write tests (AV1 / BV1 / MSV1) ==");
        var rr = PropertyModel.Read(dev, av, opt);
        var pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Kind == EditKind.Real && pv.Nullable, "AV1 present value is an editable real");
        Check(PropertyModel.TryParse(pv, "23.5", out var nv, out var err) && nv is float, "parse 23.5");
        var w = PropertyModel.Write(dev, av, pv, nv);
        Check(w.Ok, "write PV 23.5 (default priority) " + w.Error);
        rr = PropertyModel.Read(dev, av, opt);
        pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Display == "23.5", "readback PV = " + pv.Display);
        var pa = rr.Nodes.FirstOrDefault(n => n.PropertyId == (uint)BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        if (pa == null) Console.WriteLine("  (simulated AV1 has no Priority_Array: priority tests skipped)"); else {
        Check(pa.IsArray && pa.Children.Count == 16, "priority array has 16 entries");
        Check(pa.Expanded, "priority array expanded by default (GridAlwaysExpandProperties)");
        Console.WriteLine("    prio16 = '" + pa.Children[15].Display + "'  name=" + pa.Children[15].Name);
        // write at priority 8 through the array element
        PropertyModel.TryParse(pa.Children[7], "30", out var v8, out err);
        w = PropertyModel.Write(dev, av, pa.Children[7], v8);
        Check(w.Ok, "write priority array entry 8 = 30 (" + w.Error + ")");
        rr = PropertyModel.Read(dev, av, opt);
        pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pa.Children[7].Display == "30" && pv.Display == "30", "priority 8 active, PV = " + pv.Display);
        w = PropertyModel.Relinquish(dev, av, pa.Children[7]);
        Check(w.Ok, "relinquish priority 8 " + w.Error);
        rr = PropertyModel.Read(dev, av, opt);
        pa = Find(rr, BacnetPropertyIds.PROP_PRIORITY_ARRAY);
        pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pa.Children[7].IsNullValue && pv.Display == "23.5", "after relinquish PV falls back to prio16 = " + pv.Display);
        }
        // restore a sane setpoint
        PropertyModel.TryParse(pv, "22", out nv, out err); PropertyModel.Write(dev, av, pv, nv);
        // read only: object type
        Check(Find(rr, BacnetPropertyIds.PROP_OBJECT_TYPE).ReadOnly, "object type is read only");
        // error from the device: unknown object
        w = PropertyModel.Write(dev, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 4000), pv, 1f);
        Console.WriteLine("    unknown object -> " + w.Error);
        Check(!w.Ok && w.Error != null && w.Error.Contains("Unknown Object"), "device error is reported as text");

        // ---- BV1: choice
        var bv = new BacnetObjectId(BacnetObjectTypes.OBJECT_BINARY_VALUE, 1);
        rr = PropertyModel.Read(dev, bv, opt);
        pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Kind == EditKind.Choice && pv.Choices.Count == 2, "BV1 present value is a choice: " + pv.Display);
        PropertyModel.TryParse(pv, "1 : Active", out nv, out err);
        w = PropertyModel.Write(dev, bv, pv, nv);
        Check(w.Ok, "write BV1 active " + w.Error);
        rr = PropertyModel.Read(dev, bv, opt);
        pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Display.StartsWith("1"), "BV1 now " + pv.Display);
        PropertyModel.TryParse(pv, "inactive", out nv, out err); PropertyModel.Write(dev, bv, pv, nv);

        // ---- status flags bit string
        var sf = Find(rr, BacnetPropertyIds.PROP_STATUS_FLAGS);
        Check(sf.Kind == EditKind.BitString && sf.EnumType == typeof(BacnetStatusFlags), "status flags: named bit string (" + sf.Display + ")");
        var names = PropertyModel.BitNames(new PropertyModel.BitInfo { EnumType = typeof(BacnetStatusFlags) }, 4);
        Check(names.Length == 4 && names[0] == "In Alarm" && names[3] == "Out Of Service", "status flag bit names: " + string.Join(",", names));

        // ---- MSV1: state text choices
        var msv = new BacnetObjectId(BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, 1);
        rr = PropertyModel.Read(dev, msv, opt);
        pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Kind == EditKind.Choice && pv.Choices.Count == 3, "MSV1 present value choices from State_Text: " + pv.Display);
        var st = Find(rr, BacnetPropertyIds.PROP_STATE_TEXT);
        Console.WriteLine("    state text children: " + string.Join(", ", st.Children.Select(c => c.Name + "=" + c.Display)));
        int cur = int.Parse(pv.Display.Split(' ')[0]);
        PropertyModel.TryParse(pv, "3", out nv, out err);
        w = PropertyModel.Write(dev, msv, pv, nv);
        Check(w.Ok, "write MSV1 = 3 " + w.Error);
        rr = PropertyModel.Read(dev, msv, opt);
        pv = Find(rr, BacnetPropertyIds.PROP_PRESENT_VALUE);
        Check(pv.Display.StartsWith("3 : Cool"), "MSV1 now " + pv.Display);
        PropertyModel.TryParse(pv, cur.ToString(), out nv, out err); PropertyModel.Write(dev, msv, pv, nv);

        // ---- device object: nested / enums
        var dobj = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, dev.deviceId);
        rr = PropertyModel.Read(dev, dobj, opt);
        Check(Find(rr, BacnetPropertyIds.PROP_SEGMENTATION_SUPPORTED).Kind == EditKind.Choice, "segmentation is a choice: " + Find(rr, BacnetPropertyIds.PROP_SEGMENTATION_SUPPORTED).Display);
        var ol = Find(rr, BacnetPropertyIds.PROP_OBJECT_LIST);
        Check(ol.IsArray && ol.Children.All(c => c.Kind == EditKind.ObjectId), "object list: array of object ids (" + ol.Display + ")");
        var pst = rr.Nodes.FirstOrDefault(n => n.PropertyId == (uint)BacnetPropertyIds.PROP_PROTOCOL_SERVICES_SUPPORTED);
        if (pst != null) Check(pst.Kind == EditKind.BitString, "services supported bit string: " + (pst.Display.Length > 60 ? pst.Display.Substring(0, 60) + "..." : pst.Display));

        // ---- simplified view
        var simple = PropertyModel.Read(dev, av, new ReadOptions { Simplified = true, ArrayExpandMaxSize = 10, AlwaysExpand = new string[0] });
        var full = PropertyModel.Read(dev, av, opt);
        Console.WriteLine($"    simplified view: {simple.Nodes.Count} of {full.Nodes.Count} properties (filter {(SimplifiedFilter.Descriptions == null ? "missing: " + SimplifiedFilter.LoadError : SimplifiedFilter.Descriptions.Count + " object types")})");
        Check(SimplifiedFilter.Descriptions != null && simple.Nodes.Count <= full.Nodes.Count, "simplified filter loaded and applied");

        // ---- change highlighting
        var a = PropertyModel.Read(dev, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1), opt);
        Thread.Sleep(2500);
        var b = PropertyModel.Read(dev, new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1), opt);
        PropertyModel.MarkChanges(a.Nodes, b.Nodes);
        Console.WriteLine("    changed since last read: " + string.Join(", ", b.Nodes.Where(n => n.Changed).Select(n => n.Name)));

        Console.WriteLine($"\n{passes} passed, {fails} failed");
        DateTimeTests(client, dev, args.Length > 1 ? args[1] : "127.0.0.1:47812");
        client.Dispose();
        return fails == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------------
    // Date / Time / DateTime editing (wildcards)
    static void DateTimeTests(BacnetClient client, BACnetDevice dev599, string target2)
    {
        Console.WriteLine("== date/time: offline encode / decode ==");
        var any = new DateTimeParts { HasDate = true };
        Check(BitConverter.ToString(any.Encode()) == "A4-FF-FF-FF-FF", "all-unspecified date = A4 FF FF FF FF");
        var d = new DateTimeParts { HasDate = true, Year = 2026, Month = 11, Day = 7 };
        Check(BitConverter.ToString(d.Encode()) == "A4-7E-0B-07-06", "2026-11-07 = Saturday (6): " + BitConverter.ToString(d.Encode()));
        var d2 = DateTimeParts.FromDateBytes(new byte[] { 255, 13, 32, 255 });
        Check(d2.Year == 255 && d2.Month == 13 && d2.Day == 32 && d2.Weekday == 255 && d2.Format() == "*-odd-last", "decode odd-month/last-day wildcard -> " + d2.Format());
        var t = DateTimeParts.FromTimeBytes(new byte[] { 255, 30, 255, 0 });
        Check(t.Format() == "*:30:*.00" && BitConverter.ToString(t.Encode()) == "B4-FF-1E-FF-00", "time wildcard hour/second: " + t.Format());
        var tagged = d.ToBacnetValue();
        Check(tagged.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE && tagged.Value is DateTime, "specific date -> BacnetValue(DATE, DateTime)");
        var tt = new DateTimeParts { HasTime = true, Hour = 7, Minute = 30, Second = 0, Hundredths = 0 }.ToBacnetValue();
        Check(tt.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME, "specific time -> BacnetValue(TIME, DateTime)");
        var dtv = new DateTimeParts { HasDate = true, HasTime = true, Year = 2026, Month = 3, Day = 4, Hour = 1, Minute = 2, Second = 3, Hundredths = 4 }.ToBacnetValue();
        Check(dtv.Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_DATETIME, "specific date+time -> BacnetValue(DATETIME, DateTime)");
        Check(DateTimeParts.FromAck(new byte[] { 0x0C, 0, 0x80, 0, 1, 0x19, 0x55, 0x3E, 0xA4, 255, 255, 255, 255, 0xB4, 1, 255, 3, 255, 0x3F }) is DateTimeParts q && q.HasDate && q.HasTime && q.Format() == "*-*-* 01:*:03.*", "parse a ReadProperty ack with date+time wildcards");

        // ---- device 599: Local_Date / Local_Time of the Device object are read only
        Console.WriteLine("== date/time: simulator 599 Local_Date / Local_Time ==");
        var opt = ReadOptions.FromSettings(false);
        var dev = Find599(dev599, opt);
        var ld = dev.Nodes.FirstOrDefault(n => n.PropertyId == (uint)BacnetPropertyIds.PROP_LOCAL_DATE);
        var lt = dev.Nodes.FirstOrDefault(n => n.PropertyId == (uint)BacnetPropertyIds.PROP_LOCAL_TIME);
        Check(ld != null && ld.Kind == EditKind.Date && ld.Parts != null && ld.Parts.HasDate, "Local_Date: Kind Date with raw parts (" + ld?.Display + ")");
        Check(lt != null && lt.Kind == EditKind.Time && lt.Parts != null && lt.Parts.HasTime, "Local_Time: Kind Time with raw parts (" + lt?.Display + ")");

        // ---- device 602: writable
        BACnetDevice d602 = null;
        client.OnIam += (s, adr, id, apdu, seg, vendor) => { if (id == 602) d602 = new BACnetDevice(s, adr, id, apdu, seg, vendor); };
        client.WhoIs(-1, -1, new BacnetAddress(BacnetAddressTypes.IP, target2));
        for (int i = 0; i < 50 && d602 == null; i++) Thread.Sleep(100);
        if (d602 == null) { Console.WriteLine("no date/time simulator at " + target2 + " (tools/sim/sim_datetime.py) - write tests skipped"); return; }
        Console.WriteLine("== date/time: simulator 602 write tests ==");
        var dv = new BacnetObjectId(BacnetObjectTypes.OBJECT_DATE_VALUE, 1);
        var tv = new BacnetObjectId(BacnetObjectTypes.OBJECT_TIME_VALUE, 1);
        var dtvo = new BacnetObjectId(BacnetObjectTypes.OBJECT_DATETIME_VALUE, 1);

        PropertyNode Pv(BacnetObjectId o) => Find(PropertyModel.Read(d602, o, opt), BacnetPropertyIds.PROP_PRESENT_VALUE);
        void WriteParts(BacnetObjectId o, DateTimeParts p, string what)
        {
            var n = Pv(o);
            var w = PropertyModel.Write(d602, o, n, p);
            Check(w.Ok, "write " + what + " " + w.Error);
        }

        // known start state (earlier runs leave wildcards behind)
        WriteParts(dv, new DateTimeParts { HasDate = true, Year = 2026, Month = 12, Day = 25 }, "DV1 reset");
        WriteParts(dtvo, new DateTimeParts { HasDate = true, HasTime = true, Year = 2026, Month = 3, Day = 4, Hour = 12, Minute = 15, Second = 30, Hundredths = 0 }, "DTV1 reset");
        var pvD = Pv(dv);
        Check(pvD.Kind == EditKind.Date && pvD.Parts != null && pvD.Parts.Year == 2026 && pvD.Parts.Month == 12 && pvD.Parts.Day == 25, "DV1 read: " + pvD.Display);
        WriteParts(dv, new DateTimeParts { HasDate = true, Year = 2027, Month = 1, Day = 31 }, "DV1 = 2027-01-31 (no wildcard)");
        pvD = Pv(dv); Check(pvD.Parts.Year == 2027 && pvD.Parts.Month == 1 && pvD.Parts.Day == 31 && pvD.Parts.Weekday == 7 && !pvD.Parts.HasWildcard, "  readback " + pvD.Display + " weekday=" + pvD.Parts.Weekday);
        WriteParts(dv, new DateTimeParts { HasDate = true, Year = 255, Month = 13, Day = 32, Weekday = 255 }, "DV1 = any year / odd months / last day");
        pvD = Pv(dv); Check(pvD.Parts.Year == 255 && pvD.Parts.Month == 13 && pvD.Parts.Day == 32 && pvD.Parts.Weekday == 255, "  readback " + pvD.Display);
        WriteParts(dv, new DateTimeParts { HasDate = true, Year = 2026, Month = 255, Day = 255, Weekday = 5 }, "DV1 = 2026 / any / any / Friday");
        pvD = Pv(dv); Check(pvD.Parts.Year == 2026 && pvD.Parts.Month == 255 && pvD.Parts.Weekday == 5, "  readback " + pvD.Display);
        WriteParts(dv, new DateTimeParts { HasDate = true }, "DV1 = all unspecified");
        pvD = Pv(dv); Check(pvD.Parts.Year == 255 && pvD.Parts.Month == 255 && pvD.Parts.Day == 255 && pvD.Parts.Weekday == 255, "  readback " + pvD.Display);

        WriteParts(tv, new DateTimeParts { HasTime = true, Hour = 6, Minute = 45, Second = 10, Hundredths = 50 }, "TV1 = 06:45:10.50");
        var pvT = Pv(tv); Check(pvT.Kind == EditKind.Time && pvT.Parts.Hour == 6 && pvT.Parts.Minute == 45 && pvT.Parts.Second == 10 && pvT.Parts.Hundredths == 50, "  readback " + pvT.Display);
        WriteParts(tv, new DateTimeParts { HasTime = true, Hour = 255, Minute = 30, Second = 255, Hundredths = 0 }, "TV1 = *:30:*.00");
        pvT = Pv(tv); Check(pvT.Parts.Hour == 255 && pvT.Parts.Minute == 30 && pvT.Parts.Second == 255 && pvT.Parts.Hundredths == 0, "  readback " + pvT.Display);
        WriteParts(tv, new DateTimeParts { HasTime = true }, "TV1 = all unspecified");
        pvT = Pv(tv); Check(pvT.Parts.Hour == 255 && pvT.Parts.Minute == 255 && pvT.Parts.Second == 255 && pvT.Parts.Hundredths == 255, "  readback " + pvT.Display);

        var pvDT = Pv(dtvo);
        Check(pvDT.Kind == EditKind.DateTime && !pvDT.IsArray && pvDT.Parts != null && pvDT.Parts.HasDate && pvDT.Parts.HasTime && pvDT.Parts.Year == 2026, "DTV1 read as one DateTime: " + pvDT.Display);
        WriteParts(dtvo, new DateTimeParts { HasDate = true, HasTime = true, Year = 2028, Month = 2, Day = 29, Hour = 23, Minute = 59, Second = 58, Hundredths = 7 }, "DTV1 = 2028-02-29 23:59:58.07");
        pvDT = Pv(dtvo); Check(pvDT.Parts.Year == 2028 && pvDT.Parts.Month == 2 && pvDT.Parts.Day == 29 && pvDT.Parts.Hour == 23 && pvDT.Parts.Hundredths == 7, "  readback " + pvDT.Display);
        WriteParts(dtvo, new DateTimeParts { HasDate = true, HasTime = true, Year = 255, Month = 14, Day = 255, Weekday = 2, Hour = 255, Minute = 0, Second = 0, Hundredths = 255 }, "DTV1 = */even/*(Tue) *:00:00.*");
        pvDT = Pv(dtvo); Check(pvDT.Parts.Year == 255 && pvDT.Parts.Month == 14 && pvDT.Parts.Day == 255 && pvDT.Parts.Weekday == 2 && pvDT.Parts.Hour == 255 && pvDT.Parts.Minute == 0 && pvDT.Parts.Hundredths == 255, "  readback " + pvDT.Display);
        WriteParts(dtvo, new DateTimeParts { HasDate = true, HasTime = true }, "DTV1 = everything unspecified");
        pvDT = Pv(dtvo); Check(pvDT.Parts.Year == 255 && pvDT.Parts.Hour == 255 && pvDT.Parts.Hundredths == 255, "  readback " + pvDT.Display);
        Console.WriteLine("  final display: " + pvDT.Display);
    }

    static PropertyReadResult Find599(BACnetDevice dev, ReadOptions opt) =>
        PropertyModel.Read(dev, new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, dev.deviceId), opt);

    static void OfflineTests()
    {
        PropertyNode N(EditKind k, Type t, object v = null, bool nullable = false) => new PropertyNode { Kind = k, ClrType = t, Value = v, Nullable = nullable };
        object o; string e;
        Check(PropertyModel.TryParse(N(EditKind.Real, typeof(float)), "21.5", out o, out e) && (float)o == 21.5f, "real");
        Check(!PropertyModel.TryParse(N(EditKind.Real, typeof(float)), "abc", out o, out e), "bad real rejected: " + e);
        Check(PropertyModel.TryParse(N(EditKind.Unsigned, typeof(ulong)), "0x10", out o, out e) && (ulong)o == 16, "unsigned hex");
        Check(!PropertyModel.TryParse(N(EditKind.Unsigned, typeof(byte)), "300", out o, out e), "byte overflow rejected: " + e);
        Check(PropertyModel.TryParse(N(EditKind.Signed, typeof(long)), "-5", out o, out e) && (long)o == -5, "signed");
        Check(PropertyModel.TryParse(N(EditKind.Boolean, typeof(bool)), "false", out o, out e) && (bool)o == false, "bool");
        Check(PropertyModel.TryParse(N(EditKind.Real, typeof(float), null, true), "", out o, out e) && o == null, "empty = null for nullable");
        Check(PropertyModel.TryParse(N(EditKind.ObjectId, typeof(BacnetObjectId)), "OBJECT_ANALOG_INPUT:7", out o, out e) && ((BacnetObjectId)o).instance == 7, "object id long form");
        Check(PropertyModel.TryParse(N(EditKind.ObjectId, typeof(BacnetObjectId)), "AV:3", out o, out e) && ((BacnetObjectId)o).type == BacnetObjectTypes.OBJECT_ANALOG_VALUE, "object id short form");
        Check(PropertyModel.TryParse(N(EditKind.Octets, typeof(byte[])), "[1,0x0A,255]", out o, out e) && ((byte[])o).SequenceEqual(new byte[] { 1, 10, 255 }), "octets list");
        Check(PropertyModel.TryParse(N(EditKind.Octets, typeof(byte[])), "0A FF", out o, out e) && ((byte[])o).SequenceEqual(new byte[] { 10, 255 }), "octets hex");
        Check(PropertyModel.FormatBytes(new byte[] { 1, 255 }) == "[1,255]", "octets format");
        Check(PropertyModel.TryParse(N(EditKind.BitString, typeof(BacnetBitString)), "0101", out o, out e) && ((BacnetBitString)o).ToString() == "0101", "bit string");
        Check(PropertyModel.TryParse(N(EditKind.Time, typeof(DateTime)), "13:45:10", out o, out e) && ((DateTime)o).Hour == 13 && ((DateTime)o).Year == 1, "time");
        Check(PropertyModel.TryParse(N(EditKind.Date, typeof(DateTime)), "2024-03-05", out o, out e) && ((DateTime)o).Month == 3, "date");
        Check(PropertyModel.TryParse(N(EditKind.IpAddress, typeof(System.Net.IPAddress)), "10.1.2.3", out o, out e), "ip");
        var ch = N(EditKind.Choice, typeof(uint)); ch.EnumType = typeof(BacnetUnitsId); ch.Choices = PropertyModel.ChoicesOf(typeof(BacnetUnitsId));
        Check(PropertyModel.TryParse(ch, "62", out o, out e) && (uint)o == 62, "choice by number");
        Check(PropertyModel.ChoicesOf(typeof(BacnetPolarity)).Count == 2, "polarity choices");
        Check(PropertyModel.FormatObject(21.5f) == "21.5" && PropertyModel.FormatObject(true) == "True", "format scalars");
        Check(PropertyModel.FormatObject(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1)) == "OBJECT_ANALOG_INPUT:1", "format object id");
        // every public struct of the BACnet library can be reflected without exceptions
        int types = 0;
        foreach (var t in typeof(BacnetObjectId).Assembly.GetTypes().Where(t => t.IsPublic && t.IsValueType && !t.IsEnum && t.Namespace == "System.IO.BACnet"))
        {
            try { PropertyModel.MembersOf(t); types++; } catch (Exception ex) { Check(false, "reflect " + t.Name + ": " + ex.Message); }
        }
        Check(true, $"reflected {types} structures");
        // composite editing: BacnetDeviceObjectPropertyReference
        var r = new BacnetDeviceObjectPropertyReference(new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_INPUT, 1), BacnetPropertyIds.PROP_PRESENT_VALUE);
        Console.WriteLine("    DeviceObjectPropertyReference members: " + string.Join(", ", PropertyModel.MembersOf(r.GetType()).Select(m => m.Name + ":" + m.Type.Name + (m.CanWrite ? "" : "(ro)"))));
        Check(PropertyModel.FormatObject(r) == "Reference to OBJECT_ANALOG_INPUT:1", "format reference");
    }
}
