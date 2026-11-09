using Yabe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.BACnet;
using System.Linq;

namespace YabeMac.PropertyPanel
{
    /// <summary>
    /// UI independent logic of the Properties panel (replacement of DynamicPropertyGridContainer + the property
    /// handling of MainDialog). This file: which enumeration / bit list / array naming a property gets.
    /// </summary>
    public static partial class PropertyModel
    {
        // ---- uint enumerations by property id (CustomPropertyDescriptor.GetConverterBasedOnPropertyIdOnly) ----
        static readonly Dictionary<BacnetPropertyIds, Type> EnumByProperty = BuildEnumMap();

        static Dictionary<BacnetPropertyIds, Type> BuildEnumMap()
        {
            var m = new Dictionary<BacnetPropertyIds, Type>();
            void Add(Type t, params BacnetPropertyIds[] ids) { foreach (var i in ids) m[i] = t; }
            Add(typeof(BacnetObjectTypes), BacnetPropertyIds.PROP_OBJECT_TYPE);
            Add(typeof(BacnetEventNotificationData.BacnetNotifyTypes), BacnetPropertyIds.PROP_NOTIFY_TYPE);
            Add(typeof(BacnetEventNotificationData.BacnetEventTypes), BacnetPropertyIds.PROP_EVENT_TYPE);
            Add(typeof(BacnetEventNotificationData.BacnetEventStates), BacnetPropertyIds.PROP_EVENT_STATE);
            Add(typeof(BacnetPolarity), BacnetPropertyIds.PROP_POLARITY);
            Add(typeof(BacnetUnitsId), BacnetPropertyIds.PROP_UNITS, BacnetPropertyIds.PROP_OUTPUT_UNITS,
                BacnetPropertyIds.PROP_CONTROLLED_VARIABLE_UNITS, BacnetPropertyIds.PROP_PROPORTIONAL_CONSTANT_UNITS,
                BacnetPropertyIds.PROP_INTEGRAL_CONSTANT_UNITS, BacnetPropertyIds.PROP_DERIVATIVE_CONSTANT_UNITS,
                BacnetPropertyIds.PROP_CAR_LOAD_UNITS);
            Add(typeof(BacnetReliability), BacnetPropertyIds.PROP_RELIABILITY);
            Add(typeof(BacnetSegmentations), BacnetPropertyIds.PROP_SEGMENTATION_SUPPORTED);
            Add(typeof(BacnetDeviceStatus), BacnetPropertyIds.PROP_SYSTEM_STATUS);
            Add(typeof(BacnetRestartReason), BacnetPropertyIds.PROP_LAST_RESTART_REASON);
            Add(typeof(BacnetWritePriority), BacnetPropertyIds.PROP_PRIORITY_FOR_WRITING);
            Add(typeof(BacnetProgramChange), BacnetPropertyIds.PROP_PROGRAM_CHANGE);
            Add(typeof(BacnetProgramState), BacnetPropertyIds.PROP_PROGRAM_STATE);
            Add(typeof(BACnetProgramError), BacnetPropertyIds.PROP_REASON_FOR_HALT);
            Add(typeof(BACnetBackupState), BacnetPropertyIds.PROP_BACKUP_AND_RESTORE_STATE);
            Add(typeof(BacnetFileAccessMethod), BacnetPropertyIds.PROP_FILE_ACCESS_METHOD);
            Add(typeof(BACnetDoorStatus), BacnetPropertyIds.PROP_DOOR_STATUS, BacnetPropertyIds.PROP_LOCK_STATUS, BacnetPropertyIds.PROP_CAR_DOOR_STATUS);
            Add(typeof(BACnetAccessCredentialDisable), BacnetPropertyIds.PROP_CREDENTIAL_DISABLE);
            Add(typeof(BACnetAccessZoneOccupancyState), BacnetPropertyIds.PROP_OCCUPANCY_STATE);
            Add(typeof(BACnetAuthenticationStatus), BacnetPropertyIds.PROP_AUTHENTICATION_STATUS);
            Add(typeof(BACnetEscalatorMode), BacnetPropertyIds.PROP_ESCALATOR_MODE);
            Add(typeof(BACnetIPMode), BacnetPropertyIds.PROP_BACNET_IP_MODE);
            Add(typeof(BACnetMaintenance), BacnetPropertyIds.PROP_MAINTENANCE_REQUIRED);
            Add(typeof(BACnetNetworkNumberQuality), BacnetPropertyIds.PROP_NETWORK_NUMBER_QUALITY);
            Add(typeof(BACnetNetworkPortCommand), BacnetPropertyIds.PROP_COMMAND);
            Add(typeof(BACnetNetworkType), BacnetPropertyIds.PROP_NETWORK_TYPE);
            Add(typeof(BACnetSecurityLevel), BacnetPropertyIds.PROP_BASE_DEVICE_SECURITY_POLICY);
            Add(typeof(BACnetProtocolLevel), BacnetPropertyIds.PROP_PROTOCOL_LEVEL);
            Add(typeof(BACnetSilencedState), BacnetPropertyIds.PROP_SILENCED);
            Add(typeof(BACnetTimerState), BacnetPropertyIds.PROP_TIMER_STATE);
            Add(typeof(BACnetTimerTransition), BacnetPropertyIds.PROP_LAST_STATE_CHANGE);
            Add(typeof(BACnetWriteStatus), BacnetPropertyIds.PROP_WRITE_STATUS);
            Add(typeof(BACnetDoorAlarmState), BacnetPropertyIds.PROP_DOOR_ALARM_STATE);
            Add(typeof(BACnetDoorSecuredStatus), BacnetPropertyIds.PROP_SECURED_STATUS);
            Add(typeof(BACnetAccessEvents), BacnetPropertyIds.PROP_ACCESS_EVENT, BacnetPropertyIds.PROP_LAST_ACCESS_EVENT,
                BacnetPropertyIds.PROP_ACCESS_ALARM_EVENTS, BacnetPropertyIds.PROP_ACCESS_TRANSACTION_EVENTS);
            Add(typeof(BACnetFaultParameter.BACnetFaultType), BacnetPropertyIds.PROP_FAULT_TYPE);
            return m;
        }

        // ---- bit strings with named bits (BacnetBitStringValueConverter / BacnetBitStringToEnumListDisplay) ----
        public sealed class BitInfo { public Type EnumType; public bool Linear; }
        static readonly Dictionary<BacnetPropertyIds, BitInfo> BitsByProperty = new Dictionary<BacnetPropertyIds, BitInfo>
        {
            { BacnetPropertyIds.PROP_PROTOCOL_OBJECT_TYPES_SUPPORTED, new BitInfo { EnumType = typeof(BacnetObjectTypes), Linear = true } },
            { BacnetPropertyIds.PROP_PROTOCOL_SERVICES_SUPPORTED, new BitInfo { EnumType = typeof(BacnetServicesSupported), Linear = true } },
            { BacnetPropertyIds.PROP_STATUS_FLAGS, new BitInfo { EnumType = typeof(BacnetStatusFlags) } },
            { BacnetPropertyIds.PROP_LIMIT_ENABLE, new BitInfo { EnumType = typeof(BacnetEventNotificationData.BacnetLimitEnable) } },
            { BacnetPropertyIds.PROP_EVENT_ENABLE, new BitInfo { EnumType = typeof(BacnetEventNotificationData.BacnetEventEnable) } },
            { BacnetPropertyIds.PROP_ACK_REQUIRED, new BitInfo { EnumType = typeof(BacnetEventNotificationData.BacnetEventEnable) } },
            { BacnetPropertyIds.PROP_ACKED_TRANSITIONS, new BitInfo { EnumType = typeof(BacnetEventNotificationData.BacnetEventEnable) } },
        };

        /// <summary>Bit names of a named bit string property: index = bit number, entry may be null (unnamed bit).</summary>
        public static string[] BitNames(BitInfo info, int minBits = 0)
        {
            var names = new Dictionary<int, string>();
            foreach (var v in Enum.GetValues(info.EnumType))
            {
                long l = Convert.ToInt64(v);
                int bit;
                if (info.Linear) bit = (int)l;
                else { if (l <= 0) continue; bit = (int)Math.Log(l, 2); if ((1L << bit) != l) continue; }
                if (!names.ContainsKey(bit)) names[bit] = BitNiceName(Enum.GetName(info.EnumType, v));
            }
            int n = Math.Max(minBits, names.Count == 0 ? 0 : names.Keys.Max() + 1);
            var r = new string[n];
            foreach (var kv in names) if (kv.Key < n) r[kv.Key] = kv.Value;
            return r;
        }

        // ---- arrays whose elements get names instead of "[i]" -------------------------------------------------
        /// <summary>Name of element i (0 based) of the array, mirroring BacnetArrayIndexConverter / ArrayConverters.</summary>
        public static string ElementName(uint propertyId, int i)
        {
            switch ((BacnetPropertyIds)propertyId)
            {
                case BacnetPropertyIds.PROP_PRIORITY_ARRAY:
                    {
                        int p = i + 1;  // BACnet priorities are 1..16
                        if (p <= 16)
                        {
                            var fi = typeof(BacnetWritePriority).GetField(((BacnetWritePriority)p).ToString());
                            var d = fi?.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).OfType<System.ComponentModel.DescriptionAttribute>().FirstOrDefault();
                            if (d != null) return d.Description;
                        }
                        return "[" + p + "]";
                    }
                case BacnetPropertyIds.PROP_PRIORITY:
                case BacnetPropertyIds.PROP_EVENT_TIME_STAMPS:
                    if (i < 3) return TitleCase(new[] { "TO_OFF_NORMAL", "TO_FAULT", "TO_NORMAL" }[i].Replace('_', ' '));
                    break;
                case BacnetPropertyIds.PROP_STATE_TEXT:
                    return "[" + (i + 1) + "]";                  // 1 based (multistate objects)
                case BacnetPropertyIds.PROP_WEEKLY_SCHEDULE:
                    if (i < 7) return ((BACnetDailySchedule.DaysOfTheWeek)i).ToString();
                    break;
            }
            return "[" + i + "]";
        }

        /// <summary>Properties that are a BACnet "List of" (no array index access): written whole / add-remove element.</summary>
        static readonly HashSet<BacnetPropertyIds> ListProperties = new HashSet<BacnetPropertyIds>
        {
            BacnetPropertyIds.PROP_LIST_OF_OBJECT_PROPERTY_REFERENCES, BacnetPropertyIds.PROP_DATE_LIST,
            BacnetPropertyIds.PROP_RECIPIENT_LIST, BacnetPropertyIds.PROP_ACTIVE_COV_SUBSCRIPTIONS,
            BacnetPropertyIds.PROP_ALARM_VALUES, BacnetPropertyIds.PROP_FAULT_VALUES,
            BacnetPropertyIds.PROP_TIME_SYNCHRONIZATION_RECIPIENTS, BacnetPropertyIds.PROP_UTC_TIME_SYNCHRONIZATION_RECIPIENTS,
            BacnetPropertyIds.PROP_RESTART_NOTIFICATION_RECIPIENTS, BacnetPropertyIds.PROP_MANUAL_SLAVE_ADDRESS_BINDING,
            BacnetPropertyIds.PROP_SLAVE_ADDRESS_BINDING, BacnetPropertyIds.PROP_DEVICE_ADDRESS_BINDING,
            BacnetPropertyIds.PROP_LIST_OF_GROUP_MEMBERS, BacnetPropertyIds.PROP_MEMBER_OF,
            BacnetPropertyIds.PROP_ZONE_MEMBERS, BacnetPropertyIds.PROP_ACTIVE_VT_SESSIONS,
            BacnetPropertyIds.PROP_SUBSCRIBED_RECIPIENTS,
        };
        static readonly HashSet<BacnetPropertyIds> ArrayProperties = new HashSet<BacnetPropertyIds>
        {
            BacnetPropertyIds.PROP_OBJECT_LIST, BacnetPropertyIds.PROP_PRIORITY_ARRAY, BacnetPropertyIds.PROP_STATE_TEXT,
            BacnetPropertyIds.PROP_WEEKLY_SCHEDULE, BacnetPropertyIds.PROP_EXCEPTION_SCHEDULE, BacnetPropertyIds.PROP_EVENT_TIME_STAMPS,
            BacnetPropertyIds.PROP_EVENT_MESSAGE_TEXTS, BacnetPropertyIds.PROP_PRIORITY, BacnetPropertyIds.PROP_SUBORDINATE_LIST,
            BacnetPropertyIds.PROP_STRUCTURED_OBJECT_LIST,
        };
        public static bool IsListProperty(uint id) => ListProperties.Contains((BacnetPropertyIds)id);
        public static bool IsArrayProperty(uint id) => ArrayProperties.Contains((BacnetPropertyIds)id);

        /// <summary>Properties no BACnet device lets a client write; shown as read-only.</summary>
        static readonly HashSet<BacnetPropertyIds> ReadOnlyProperties = new HashSet<BacnetPropertyIds>
        {
            BacnetPropertyIds.PROP_OBJECT_IDENTIFIER, BacnetPropertyIds.PROP_OBJECT_TYPE,
            BacnetPropertyIds.PROP_PROTOCOL_VERSION, BacnetPropertyIds.PROP_PROTOCOL_REVISION,
            BacnetPropertyIds.PROP_PROTOCOL_SERVICES_SUPPORTED, BacnetPropertyIds.PROP_PROTOCOL_OBJECT_TYPES_SUPPORTED,
            BacnetPropertyIds.PROP_OBJECT_LIST, BacnetPropertyIds.PROP_VENDOR_IDENTIFIER, BacnetPropertyIds.PROP_VENDOR_NAME,
            BacnetPropertyIds.PROP_MODEL_NAME, BacnetPropertyIds.PROP_FIRMWARE_REVISION, BacnetPropertyIds.PROP_APPLICATION_SOFTWARE_VERSION,
            BacnetPropertyIds.PROP_SEGMENTATION_SUPPORTED, BacnetPropertyIds.PROP_MAX_APDU_LENGTH_ACCEPTED,
            BacnetPropertyIds.PROP_DATABASE_REVISION, BacnetPropertyIds.PROP_SYSTEM_STATUS,
            BacnetPropertyIds.PROP_ACKED_TRANSITIONS, BacnetPropertyIds.PROP_EVENT_TIME_STAMPS,
            BacnetPropertyIds.PROP_DEVICE_ADDRESS_BINDING, BacnetPropertyIds.PROP_ACTIVE_COV_SUBSCRIPTIONS,
        };
        public static bool IsReadOnlyProperty(uint id) => ReadOnlyProperties.Contains((BacnetPropertyIds)id);

        // ---- names -----------------------------------------------------------------------------------------------
        public static string TitleCase(string s) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase((s ?? "").ToLower());

        /// <summary>BacnetEnumValueDisplay.GetNiceName</summary>
        public static string EnumNiceName(string name)
        {
            if (name == null) return "";    // outbound (proprietary) value
            foreach (var p in new[] { "EVENT_STATE_", "POLARITY_", "RELIABILITY_", "SEGMENTATION_", "STATUS_", "NOTIFY_", "UNITS_", "OBJECTS_" })
                if (name.StartsWith(p)) name = name.Substring(p.Length);
            if (name.StartsWith("SERVICE_SUPPORTED_")) name = name.Substring(18);
            return TitleCase(name.Replace('_', ' '));
        }

        /// <summary>BacnetBitStringToEnumListDisplay.GetNiceName</summary>
        public static string BitNiceName(string name)
        {
            if (name == null) return null;
            foreach (var p in new[] { "OBJECT_", "SERVICE_SUPPORTED_", "STATUS_FLAG_", "EVENT_ENABLE_", "EVENT_" })
                if (name.StartsWith(p)) name = name.Substring(p.Length);
            return TitleCase(name.Replace('_', ' '));
        }

        // ---- enumeration choice lists ----------------------------------------------------------------------------
        static readonly Dictionary<Type, List<Choice>> _choiceCache = new Dictionary<Type, List<Choice>>();
        public static List<Choice> ChoicesOf(Type enumType)
        {
            lock (_choiceCache)
            {
                if (_choiceCache.TryGetValue(enumType, out var l)) return l;
                l = new List<Choice>();
                var seen = new HashSet<uint>();
                foreach (var v in Enum.GetValues(enumType))
                {
                    long lv = Convert.ToInt64(v);
                    if (lv < 0) continue;
                    uint u = (uint)lv;
                    if (!seen.Add(u)) continue;
                    l.Add(new Choice { Value = u, Label = u + " : " + EnumNiceName(Enum.GetName(enumType, v)) });
                }
                l.Sort((a, b) => a.Value.CompareTo(b.Value));
                _choiceCache[enumType] = l;
                return l;
            }
        }

        public static string ChoiceLabel(Type enumType, uint value)
        {
            string name = Enum.IsDefined(enumType, ToEnum(enumType, value)) ? Enum.GetName(enumType, ToEnum(enumType, value)) : null;
            return value + " : " + EnumNiceName(name);
        }

        static object ToEnum(Type enumType, uint v)
        {
            try { return Enum.ToObject(enumType, v); } catch { return v; }
        }
    }
}
