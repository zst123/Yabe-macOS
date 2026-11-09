using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;
using System.Threading;
using Yabe;

// Headless smoke test: discover the simulator, list objects, read all properties of each.
class Probe
{
    static int Main(string[] args)
    {
        string target = args.Length > 0 ? args[0] : "127.0.0.1:47809";
        BACnetDevice found = null;
        var client = new BacnetClient(new BacnetIpUdpProtocolTransport(47808, false, false, 1472, "127.0.0.1"), 1000, 3);
        client.OnIam += (s, adr, id, apdu, seg, vendor) => { found = new BACnetDevice(s, adr, id, apdu, seg, vendor); };
        client.Start();
        client.WhoIs(-1, -1, new BacnetAddress(BacnetAddressTypes.IP, target));
        for (int i = 0; i < 50 && found == null; i++) Thread.Sleep(100);
        if (found == null) { Console.WriteLine("FAIL: no I-Am"); return 1; }
        Console.WriteLine($"I-Am: device {found.deviceId} at {found.BacAdr} vendor {found.vendor_Id}");

        if (!found.ReadObjectList(out var objs, out _)) { Console.WriteLine("FAIL: object list"); return 1; }
        found.ReadAllObjectsName();
        foreach (var o in objs)
        {
            Console.WriteLine($"  {o}  \"{found.GetObjectName(o)}\"");
            var all = new[] { new BacnetPropertyReference((uint)BacnetPropertyIds.PROP_ALL, System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL) };
            if (!found.ReadPropertyMultipleRequest(o, all, out var res)) { Console.WriteLine("    FAIL RPM"); continue; }
            foreach (var pv in res.SelectMany(r => r.values).Take(6))
                Console.WriteLine($"    {found.GetNiceName((BacnetPropertyIds)pv.property.propertyIdentifier)} = {string.Join(",", pv.value.Select(v => v.Value))}");
        }

        var sp = new BacnetObjectId(BacnetObjectTypes.OBJECT_ANALOG_VALUE, 1);
        bool w = found.WritePropertyRequest(sp, BacnetPropertyIds.PROP_PRESENT_VALUE, new[] { new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, 23.5f) });
        found.ReadPropertyRequest(sp, BacnetPropertyIds.PROP_PRESENT_VALUE, out var back, true);
        Console.WriteLine($"Write setpoint 23.5 -> ok={w}, readback={back?[0].Value}");
        return 0;
    }
}
