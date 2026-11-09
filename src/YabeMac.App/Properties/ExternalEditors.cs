using System;
using System.IO.BACnet;
using System.Linq;
using System.Reflection;
using Yabe;

namespace YabeMac.PropertyPanel
{
    /// <summary>
    /// Entry points of the Schedule / Calendar editors (delivered by another worker as
    /// ScheduleDisplay.Open(BACnetDevice, BacnetObjectId) / CalendarEditor.Open(...)). Looked up by reflection so the
    /// property panel builds without them; replace the body by a direct call once they are merged.
    /// </summary>
    public static class ExternalEditors
    {
        public static bool OpenSchedule(BACnetDevice device, BacnetObjectId id) => Invoke("ScheduleDisplay", device, id);
        public static bool OpenCalendar(BACnetDevice device, BacnetObjectId id) => Invoke("CalendarEditor", device, id);

        static bool Invoke(string typeName, BACnetDevice device, BacnetObjectId id)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t;
                    try { t = asm.GetTypes().FirstOrDefault(x => x.Name == typeName && x.GetMethod("Open", BindingFlags.Public | BindingFlags.Static) != null); }
                    catch { continue; }
                    if (t == null) continue;
                    var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static).First(x => x.Name == "Open" && x.GetParameters().Length >= 2);
                    var args = new object[m.GetParameters().Length];
                    args[0] = device; args[1] = id;
                    for (int i = 2; i < args.Length; i++) args[i] = m.GetParameters()[i].HasDefaultValue ? m.GetParameters()[i].DefaultValue : null;
                    m.Invoke(null, args);
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
