using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.BACnet;
using System.Text;
using Yabe;

namespace YabeMac.PropertyPanel
{
    /// <summary>
    /// A BACnet Date, Time or DateTime *with* its wildcards ('unspecified' = 255 for every field, month 13/14 = odd/even,
    /// day 32 = last day, 33/34 = odd/even days). System.DateTime used by the decoder of the library cannot hold them.
    /// </summary>
    public sealed class DateTimeParts
    {
        public const int Any = 255;

        public bool HasDate, HasTime;
        public int Year = Any;      // full year (1900..2154) or Any
        public int Month = Any;     // 1..12, 13 odd, 14 even, Any
        public int Day = Any;       // 1..31, 32 last, 33 odd, 34 even, Any
        public int Weekday = Any;   // 1 (Monday) .. 7 (Sunday), Any
        public int Hour = Any, Minute = Any, Second = Any, Hundredths = Any;

        public bool DateIsSpecific => HasDate && Year != Any && Month >= 1 && Month <= 12 && Day >= 1 && Day <= 31;
        public bool DateHasWildcard => HasDate && !DateIsSpecific;
        public bool TimeHasWildcard => HasTime && (Hour == Any || Minute == Any || Second == Any || Hundredths == Any);
        public bool HasWildcard => DateHasWildcard || TimeHasWildcard;

        public DateTimeParts Clone() => (DateTimeParts)MemberwiseClone();

        // ---- construction ------------------------------------------------------------------------------------
        static int Norm(int b, int max) => (b >= 1 && b <= max) ? b : Any;
        static int NormT(int b, int max) => (b >= 0 && b <= max) ? b : Any;

        public static DateTimeParts FromDateBytes(byte[] b, int o = 0) => new DateTimeParts().SetDate(b, o);
        public static DateTimeParts FromTimeBytes(byte[] b, int o = 0) => new DateTimeParts().SetTime(b, o);

        DateTimeParts SetDate(byte[] b, int o)
        {
            HasDate = true;
            Year = b[o] == 255 ? Any : 1900 + b[o];
            Month = Norm(b[o + 1], 14); Day = Norm(b[o + 2], 34); Weekday = Norm(b[o + 3], 7);
            return this;
        }
        DateTimeParts SetTime(byte[] b, int o)
        {
            HasTime = true;
            Hour = NormT(b[o], 23); Minute = NormT(b[o + 1], 59); Second = NormT(b[o + 2], 59); Hundredths = NormT(b[o + 3], 99);
            return this;
        }

        /// <summary>Fallback when the raw bytes are not known: from the decoded DateTime (a DateTime(1,1,1) date means 'any').</summary>
        public static DateTimeParts FromDateTime(DateTime dt, BacnetApplicationTags? tag)
        {
            var p = new DateTimeParts();
            bool time = tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME || tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_DATETIME;
            bool date = tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME;
            if (date)
            {
                p.HasDate = true;
                if (!(dt.Year == 1 && dt.Month == 1 && dt.Day == 1))
                {
                    p.Year = dt.Year; p.Month = dt.Month; p.Day = dt.Day;
                    p.Weekday = dt.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)dt.DayOfWeek;
                }
            }
            if (time)
            {
                p.HasTime = true; p.Hour = dt.Hour; p.Minute = dt.Minute; p.Second = dt.Second; p.Hundredths = dt.Millisecond / 10;
            }
            return p;
        }

        /// <summary>Parses the data of a ReadProperty ack (service data) for a Date, Time or Date+Time value.</summary>
        public static DateTimeParts FromAck(byte[] apdu)
        {
            // objectId [0], propertyId [1], optional arrayIndex [2], opening tag [3] ... closing tag [3]
            int i = 0; DateTimeParts p = new DateTimeParts();
            try
            {
                while (i < apdu.Length && apdu[i] != 0x3E)
                {
                    int len = apdu[i] & 7;
                    if ((apdu[i] & 0xF0) == 0xF0) throw new FormatException();
                    i += 1 + len;
                }
                i++;
                while (i < apdu.Length && apdu[i] != 0x3F)
                {
                    byte b = apdu[i];
                    if (b == 0xA4) { if (p.HasDate) return null; p.SetDate(apdu, i + 1); i += 5; }
                    else if (b == 0xB4) { if (p.HasTime) return null; p.SetTime(apdu, i + 1); i += 5; }
                    else return null;
                }
            }
            catch { return null; }
            return (p.HasDate || p.HasTime) ? p : null;
        }

        // ---- conversion -------------------------------------------------------------------------------------
        public bool TryToDateTime(out DateTime dt)
        {
            dt = new DateTime(1, 1, 1);
            try
            {
                if (HasDate)
                {
                    if (!DateIsSpecific) return false;
                    dt = new DateTime(Year, Month, Day);
                }
                if (HasTime)
                {
                    if (TimeHasWildcard) return false;
                    dt = new DateTime(dt.Year, dt.Month, dt.Day, Hour, Minute, Second, Hundredths * 10);
                }
                return true;
            }
            catch { return false; }
        }

        public int ComputedWeekday()
        {
            if (!DateIsSpecific) return Any;
            try { var d = new DateTime(Year, Month, Day).DayOfWeek; return d == DayOfWeek.Sunday ? 7 : (int)d; } catch { return Any; }
        }

        /// <summary>The 4 bytes of a BACnet Date (year-1900, month, day, weekday).</summary>
        public byte[] DateBytes()
        {
            int wd = DateIsSpecific ? ComputedWeekday() : Weekday;
            return new[] { (byte)(Year == Any ? 255 : Math.Max(0, Year - 1900)), (byte)Month, (byte)Day, (byte)wd };
        }
        public byte[] TimeBytes() => new[] { (byte)Hour, (byte)Minute, (byte)Second, (byte)Hundredths };

        /// <summary>Application tagged encoding (A4 yy mm dd wd / B4 hh mm ss hh ...).</summary>
        public byte[] Encode()
        {
            var l = new List<byte>();
            if (HasDate) { l.Add(0xA4); l.AddRange(DateBytes()); }
            if (HasTime) { l.Add(0xB4); l.AddRange(TimeBytes()); }
            return l.ToArray();
        }

        /// <summary>
        /// The value to write: the usual BacnetValue(DATE/TIME/DATETIME, DateTime) when no field is a wildcard (the library encoder
        /// computes the weekday), otherwise the exact application encoding (the library cannot encode partial wildcards).
        /// </summary>
        public BacnetValue ToBacnetValue()
        {
            var tag = HasDate && HasTime ? BacnetApplicationTags.BACNET_APPLICATION_TAG_DATETIME
                    : HasTime ? BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME : BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE;
            if (!HasWildcard && TryToDateTime(out var dt)) return new BacnetValue(tag, dt);
            return new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_CONTEXT_SPECIFIC_ENCODED, Encode());
        }

        // ---- display ------------------------------------------------------------------------------------------
        static readonly string[] Wd = { "", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        public string Format()
        {
            if (!HasWildcard && TryToDateTime(out var dt))
            {
                if (HasDate && HasTime) return dt.ToString();
                return HasTime ? dt.ToLongTimeString() : dt.ToShortDateString();
            }
            var sb = new StringBuilder();
            if (HasDate)
            {
                sb.Append(Year == Any ? "*" : Year.ToString("0000"));
                sb.Append('-').Append(Month == Any ? "*" : Month == 13 ? "odd" : Month == 14 ? "even" : Month.ToString("00"));
                sb.Append('-').Append(Day == Any ? "*" : Day == 32 ? "last" : Day == 33 ? "odd" : Day == 34 ? "even" : Day.ToString("00"));
                int wd = DateIsSpecific ? ComputedWeekday() : Weekday;
                if (wd >= 1 && wd <= 7) sb.Append(" (").Append(Wd[wd]).Append(')');
            }
            if (HasTime)
            {
                if (sb.Length > 0) sb.Append(' ');
                string F(int v) => v == Any ? "*" : v.ToString("00");
                sb.Append(F(Hour)).Append(':').Append(F(Minute)).Append(':').Append(F(Second)).Append('.').Append(F(Hundredths));
            }
            return sb.ToString();
        }
        public override string ToString() => Format();

        // ---- reading the raw bytes (the decoder of the library loses the wildcards) ---------------------------
        /// <summary>Re-reads one scalar property to get its Date / Time bytes; null when not possible.</summary>
        public static DateTimeParts ReadRaw(BACnetDevice device, BacnetObjectId obj, uint propertyId)
        {
            try
            {
                lock (device.channel)
                {
                    using (var res = (BacnetAsyncResult)device.channel.BeginReadPropertyRequest(device.BacAdr, obj, (BacnetPropertyIds)propertyId, true))
                    {
                        for (int r = 0; r < Math.Max(1, device.channel.Retries); r++)
                        {
                            if (res.WaitForDone(device.channel.Timeout))
                                return res.Error == null && res.Result != null ? FromAck(res.Result) : null;
                            if (r < device.channel.Retries - 1) res.Resend();
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
