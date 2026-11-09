using Avalonia.Interactivity;
using System.IO.BACnet;
using Yabe;

namespace YabeMac
{
    // Area "Schedule": handlers for Functions > Show schedule / Show calendar (original showScheduleToolStripMenuItem_Click
    // and showCalendarToolStripMenuItem_Click in MainDialog.Menus.cs). Windows: Forms/ScheduleDisplay.cs, Forms/CalendarEditor.cs.
    public partial class MainWindow
    {
        // Original GetSchedObjectLink(out device, out object_id, ExpectedType): false when nothing is selected or the type differs.
        bool GetSchedObjectLink(out BACnetDevice device, out BacnetObjectId objectId, BacnetObjectTypes expected)
        {
            objectId = new BacnetObjectId();
            device = SelectedDevice;
            if (device == null) return false;
            var id = SelectedObjectId;
            if (id == null) return false;
            objectId = id.Value;
            return objectId.type == expected;
        }

        async void OnShowSchedule(object s, RoutedEventArgs e)
        {
            if (!GetSchedObjectLink(out var device, out var id, BacnetObjectTypes.OBJECT_SCHEDULE)) { Log("Select a Schedule object first."); return; }
            await ScheduleDisplay.Open(device, id, this);
        }

        async void OnShowCalendar(object s, RoutedEventArgs e)
        {
            if (!GetSchedObjectLink(out var device, out var id, BacnetObjectTypes.OBJECT_CALENDAR)) { Log("Select a Calendar object first."); return; }
            await CalendarEditor.Open(device, id, this);
        }
    }
}
