using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.IO.BACnet;
using System.Linq;
using System.Threading.Tasks;
using Yabe;

namespace YabeMac
{
    // Area "Alarms": notification editor, alarm summary, incoming event notifications and the Ack buttons.
    public partial class MainWindow
    {
        /// <summary>
        /// Raised on the UI thread for every incoming Confirmed/UnconfirmedEventNotification, after it was logged and
        /// BEFORE the SimpleAck is sent. The handler for this event in the Subscriptions area should add/update the
        /// row of the subscription grid (original MainDialog.OnEventNotify: key = initiatingDevice:type:instance,
        /// columns device, object, name, "FROM to TO", time, notify type, description) and call the alarm log writer.
        /// This area already sends the SimpleAck (when need_confirm) - do not ack again.
        /// </summary>
        public event Action<BacnetClient, BacnetAddress, BacnetEventNotificationData> EventNotified;

        /// <summary>"EVENT_STATE_" prefix removed, like the original EventTypeNiceName.</summary>
        public static string EventStateNiceName(BacnetEventNotificationData.BacnetEventStates state) => state.ToString().Substring(12);

        // ---- menu handlers ------------------------------------------------------------------
        async void OnShowNotification(object s, RoutedEventArgs e)
        {
            var device = SelectedDevice; var id = SelectedObjectId;
            // original: GetObjectLink(..., OBJECT_NOTIFICATION_CLASS) == false -> silently return
            if (device == null || id == null || id.Value.type != BacnetObjectTypes.OBJECT_NOTIFICATION_CLASS) return;
            await NotificationEditor.Open(device, id.Value);
        }

        async void OnAlarmSummary(object s, RoutedEventArgs e)
        {
            var device = SelectedDevice;
            if (device == null) { await Dialogs.Message("Please select a device node", "Wrong node"); return; }
            await AlarmSummary.Open(device);
        }

        // ---- event notifications --------------------------------------------------------------
        void OnEventNotify(BacnetClient sender, BacnetAddress adr, byte invoke_id, BacnetEventNotificationData EventData, bool need_confirm)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnEventNotify(sender, adr, invoke_id, EventData, need_confirm));
                return;
            }
            HandleEventNotify(sender, adr, invoke_id, EventData, need_confirm);
        }

        async void HandleEventNotify(BacnetClient sender, BacnetAddress adr, byte invoke_id, BacnetEventNotificationData EventData, bool need_confirm)
        {
            uint deviceInstance = EventData.initiatingObjectIdentifier.instance;
            // could be absent from the device list (broadcast events for instance)
            var device = new BACnetDevice(sender, adr, deviceInstance);
            var objectId = EventData.eventObjectIdentifier;

            // send ack first: the object name lookup below may take a network timeout
            if (need_confirm)
                device.SimpleAckResponse(BacnetConfirmedServices.SERVICE_CONFIRMED_EVENT_NOTIFICATION, invoke_id);

            string name = null;
            try { name = await Task.Run(() => device.ReadObjectName(objectId)); } catch { }
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("[")) name = objectId.ToString();

            Log($"Event from device {deviceInstance}: {BACnetDevice.ShortenObjectId(objectId.ToString())} \"{name}\" " +
                $"{EventStateNiceName(EventData.fromState)} to {EventStateNiceName(EventData.toState)} " +
                $"at {EventData.timeStamp.Time.ToString(AppSettings.Default.COVTimeFormater)} ({EventData.notifyType})" +
                (string.IsNullOrEmpty(EventData.messageText) ? "" : " - " + EventData.messageText));

            try { EventNotified?.Invoke(sender, adr, EventData); } catch (Exception ex) { Log("EventNotified handler error: " + ex.Message); }
        }

        // ---- Ack buttons ----------------------------------------------------------------------
        readonly DateTime[] _cachedEventTimeStampsForAcknowledgementButtons = new DateTime[3];
        BACnetDevice _ackDevice; BacnetObjectId _ackObject;

        /// <summary>Hide the three Ack buttons (original: done when the selection changes / grid is cleared).</summary>
        void HideAckButtons()
        {
            AckNormal.IsVisible = AckFault.IsVisible = AckOffnormal.IsVisible = false;
            AckPanel.IsVisible = false;
        }

        /// <summary>
        /// Called after a property read (rows in _propertyRows): shows Ack Offnormal/Fault/Normal for each bit of
        /// Acked_Transitions that is 0 (i.e. an acknowledge is pending) and caches Event_Time_Stamps for the ack.
        /// </summary>
        void UpdateAckButtons(BacnetObjectId id)
        {
            bool[] show = new bool[3];
            foreach (var row in _propertyRows.ToList())
            {
                if (row.Raw == null) continue;
                if (row.Property == BacnetPropertyIds.PROP_ACKED_TRANSITIONS)
                {
                    if (row.Raw.Count > 0 && row.Raw[0].Value is BacnetBitString acked)
                        for (byte i = 0; i < 3; i++) show[i] = !acked.GetBit(i);
                }
                else if (row.Property == BacnetPropertyIds.PROP_EVENT_TIME_STAMPS)
                {
                    for (int i = 0; i < row.Raw.Count && i < 3; i++)
                        if (row.Raw[i].Value is DateTime dt) _cachedEventTimeStampsForAcknowledgementButtons[i] = dt;
                }
            }
            _ackDevice = SelectedDevice; _ackObject = id;
            AckOffnormal.IsVisible = show[0];
            AckFault.IsVisible = show[1];
            AckNormal.IsVisible = show[2];
            AckPanel.IsVisible = show[0] || show[1] || show[2];
        }

        void OnAckNormal(object s, RoutedEventArgs e) => DoAck(BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_NORMAL);
        void OnAckFault(object s, RoutedEventArgs e) => DoAck(BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_FAULT);
        void OnAckOffnormal(object s, RoutedEventArgs e) => DoAck(BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_OFFNORMAL);

        async void DoAck(BacnetEventNotificationData.BacnetEventStates eventState)
        {
            var device = _ackDevice; var object_id = _ackObject;
            if (device == null) return;
            var evtT = new BacnetGenericTime { Tag = BacnetTimestampTags.TIME_STAMP_DATETIME };
            switch (eventState)
            {
                case BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_NORMAL: evtT.Time = _cachedEventTimeStampsForAcknowledgementButtons[2]; break;
                case BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_FAULT: evtT.Time = _cachedEventTimeStampsForAcknowledgementButtons[1]; break;
                case BacnetEventNotificationData.BacnetEventStates.EVENT_STATE_OFFNORMAL: evtT.Time = _cachedEventTimeStampsForAcknowledgementButtons[0]; break;
            }
            var ackT = new BacnetGenericTime { Tag = BacnetTimestampTags.TIME_STAMP_DATETIME, Time = DateTime.Now };
            bool ok = false;
            try { ok = await Task.Run(() => device.AlarmAcknowledgement(object_id, eventState, eventState.ToString() + " acked manually by Yabe", evtT, ackT)); } catch { }
            if (ok)
            {
                if (SelectedDevice == device && Equals(SelectedObjectId, object_id)) await ShowProperties();
            }
            else
                await Dialogs.Message("Alarm acknowledge failed!", "Error");
        }
    }
}
