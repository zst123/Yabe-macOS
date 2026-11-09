using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.BACnet;
using System.IO.BACnet.Serialize;
using System.Linq;
using System.Net;
using System.Text;
using Yabe;

namespace YabeMac
{
    // Data model of the GlobalCommander plugin, copied from YabePlugins/GlobalCommander/GlobalCommander.cs
    // (value parsing / writing logic unchanged; only the WinForms Cursor calls were dropped and the
    // Newtonsoft JSON export of non-printable values replaced by System.Text.Json).
    static class GcUtil
    {
    internal static BacnetValue ConvertVal(string value, BacnetApplicationTags appTag)
    {
        if(value==null)
        {
            return new BacnetValue(appTag, null);
        }

        switch (appTag)
        {
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL:
                return new BacnetValue(value);
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN:
                int vint;
                if (int.TryParse(value, out vint)) return new BacnetValue(appTag, (vint != 0)); // allows 0 and 1 to be used
                return new BacnetValue(appTag, bool.Parse(value));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT:
                return new BacnetValue(appTag, uint.Parse(value));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT:
                return new BacnetValue(appTag, int.Parse(value));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL:
                return new BacnetValue(appTag, float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE:
                return new BacnetValue(appTag, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_OCTET_STRING:
                try
                {
                    return new BacnetValue(appTag,StringToByteArray(value));
                }
                catch
                {
                    return new BacnetValue(appTag, value);
                }
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_CONTEXT_SPECIFIC_DECODED:
                try
                {
                    return new BacnetValue(appTag, Convert.FromBase64String(value));
                }
                catch
                {
                    return new BacnetValue(appTag, value);
                }
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING:
                return new BacnetValue(appTag, value);
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING:
                return new BacnetValue(appTag, BacnetBitString.Parse(value));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED:
                return new BacnetValue(appTag, uint.Parse(value));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE:
                return new BacnetValue(appTag, DateTime.Parse(value));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME:
                return new BacnetValue(appTag, DateTime.Parse(value));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID:
                return new BacnetValue(appTag, BacnetObjectId.Parse(value));
            case BacnetApplicationTags.BACNET_APPLICATION_TAG_READ_ACCESS_SPECIFICATION:
                return new BacnetValue(appTag, BacnetReadAccessSpecification.Parse(value));
        }

        throw new DataException("Unable to parse value \"" + value + "\" using application tag \"" + appTag.ToString() + "\".");

    }

    internal static BacnetValue ConvertVal(string value, BacnetApplicationTags appTag, Type type)
    {
        if (value == null)
        {
            return new BacnetValue(appTag, null);
        }

        if (type == null)
            return ConvertVal(value, appTag);
        if (type == typeof(string))
            return new BacnetValue(appTag, value);
        if (type == typeof(bool))
            return new BacnetValue(appTag, bool.Parse(value));
        if (type == typeof(byte))
            return new BacnetValue(appTag, byte.Parse(value));
        if (type == typeof(uint))
            return new BacnetValue(appTag, uint.Parse(value));
        if (type == typeof(UInt64))
            return new BacnetValue(appTag, UInt64.Parse(value));
        if (type == typeof(int))
            return new BacnetValue(appTag, int.Parse(value));
        if (type == typeof(Int64))
            return new BacnetValue(appTag, Int64.Parse(value));
        if (type == typeof(float))
            return new BacnetValue(appTag, float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        if (type == typeof(double))
            return new BacnetValue(appTag, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
        if (type == typeof(byte[]))
        {
            return new BacnetValue(appTag, StringToByteArray(value));
        }
        if (type.IsEnum)
        {
            object result = null;
            try
            {
                // The third parameter (true) makes the parsing case-insensitive.
                result = Enum.Parse(type, value, true);
                return new BacnetValue(appTag, result);
            }
            catch
            {
                if(int.TryParse(value,out int integerEnum))
                {
                    object myEnum = Enum.ToObject(type, integerEnum);
                    return new BacnetValue(appTag, myEnum);
                }
                throw;
            }
        }
        if (type == typeof(BacnetBitString))
            return new BacnetValue(appTag, BacnetBitString.Parse(value));
        if (type == typeof(DateTime))
            return new BacnetValue(appTag, DateTime.Parse(value));
        if (type == typeof(BacnetObjectId))
            return new BacnetValue(appTag, BacnetObjectId.Parse(value));
        if (type == typeof(BacnetReadAccessSpecification))
            return new BacnetValue(appTag, BacnetReadAccessSpecification.Parse(value));
        if(type==typeof(IPAddress))
        {
            IPAddress ip = IPAddress.Parse(value);
            byte[] ipBytes = ip.GetAddressBytes();
            return new BacnetValue(appTag, ipBytes);
        }

        throw new DataException("Unable to parse value \"" + value + "\" to type \""+type.Name+"\".");
    }

    internal static string ArrayToString(object[] arrayObj)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < arrayObj.Length; i++)
        {
            if (arrayObj[i] != null)
            {
                if (arrayObj[i] is byte[] byteArray)
                {
                    sb.Append(ByteArrayToString(byteArray));
                }
                else
                {
                    sb.Append(arrayObj[i].ToString());
                }
            }
            else
            {
                sb.Append("null");
            }
            if (i < (arrayObj.Length - 1))
            {
                sb.Append(",");
            }
        }
        sb.Append("]");
        return sb.ToString();
    }

    internal static string ByteArrayToString(byte[] arrayObj)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < arrayObj.Length; i++)
        {
            sb.Append(arrayObj[i].ToString());
            if (i < (arrayObj.Length - 1))
            {
                sb.Append(",");
            }
        }
        sb.Append("]");
        return sb.ToString();
    }

    internal static byte[] StringToByteArray(string enteredValue)
    {
        string enteredValueWithoutBrackets = enteredValue.Replace("[", "").Replace("]", "");
        string[] enteredValues = enteredValueWithoutBrackets.Split(',','.');
        int dataLength = enteredValues.Length;
        for (int i = 0; i < dataLength; i++)
        {
            enteredValues[i] = enteredValues[i].Trim();
        }

        byte[] bytes = new byte[dataLength];
        for (int i = 0; i < dataLength; i++)
        {
            string currentEnteredValue = enteredValues[i];
            byte enteredByte = 0;
            try
            {
                if (currentEnteredValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    enteredByte = HexToSingleByte(currentEnteredValue);
                }
                else
                {
                    enteredByte = byte.Parse(currentEnteredValue);
                }
            }
            catch
            {
                return null;
            }

            bytes[i] = enteredByte;
        }
        return bytes;
    }

    internal static byte HexToSingleByte(string hexByte)
    {
        // Handle the "0x" prefix
        if (hexByte.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hexByte = hexByte.Substring(2);
        }

        // Ensure the string has an even number of characters
        if (hexByte.Length < 2)
        {
            hexByte = new string('0',2 - hexByte.Length) + hexByte;
        }
        else if (hexByte.Length > 2)
        {
            hexByte = hexByte.Substring(hexByte.Length - 2, 2);
        }
        byte myByte = Convert.ToByte(hexByte, 16);
        return myByte;
    }

        static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new System.Text.Json.JsonSerializerOptions
        { IncludeFields = true, ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles, MaxDepth = 32 };
        public static string Json(object o)
        {
            try { return System.Text.Json.JsonSerializer.Serialize(o, JsonOpts); }
            catch { return o == null ? "null" : o.ToString(); }
        }
    }

    public class GcDevice : IEquatable<GcDevice>, IComparable<GcDevice>
    {
        public uint DeviceID { get; }
        private string _name;
        public string Name { get { return _name; } set { _name = value; } }
        private string _description;
        public string Description { get { return _description; } set { _description = value; if (!string.IsNullOrEmpty(_description)) { _searchString = (string.IsNullOrEmpty(_name) ? string.Empty : (_name + " - ")) + _description; } else { _searchString = null; } } }
        private string _searchString = null;
        public string SearchString
        {
            get
            {
                if (_searchString == null)
                {
                    return _name;
                }
                return _searchString;
            }
        }
        public BacnetAddress DeviceAddress { get; }
        public BacnetClient Comm { get; }
        public BACnetDevice Device { get;}
        public GlobalCommanderWindow ParentWindow { get; }
        public List<GcPoint> Points { get; }

        public override string ToString()
        {
            return Name;
        }

        public bool Equals(GcDevice other)
        {
            return Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int CompareTo(GcDevice other)
        {
            return Name.CompareTo(other.Name);
        }

        public GcDevice(BACnetDevice device, GlobalCommanderWindow parentWindow)
        {
            Device = device;
            Comm = device.channel;
            ParentWindow = parentWindow;
            DeviceID = device.deviceId;
            BacnetObjectId deviceObjectID = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, device.deviceId);
            _name = deviceObjectID.ToString();
            DeviceAddress = device.BacAdr;
            Points = new List<GcPoint>();
        }
    }

    public class GcPoint : IEquatable<GcPoint>, IComparable<GcPoint>
    {
        public GcDevice ParentDevice { get; }
        public BacnetObjectId ObjectID { get; }
        private string _name;
        public string Name { get { return _name; } set {  _name = value; } }
        private string _objectName;
        public string ObjectName { get { return _objectName; } set { _objectName = value; } }
        private string _description;
        public string Description { get { return _description; } set { _description = value; if (!string.IsNullOrEmpty(_description)) { _searchString = (string.IsNullOrEmpty(_name) ? string.Empty : (_name + " - ")) + _description; } else { _searchString = null; } } }
        private string _searchString = null;
        public string SearchString { get
            { 
                if(_searchString==null)
                {
                    return _name;
                }
                return _searchString;
            }
        }
        public List<GcProperty> Properties { get; }

        public override string ToString()
        {
            return Name;
        }

        public bool Equals(GcPoint other)
        {
            return Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int CompareTo(GcPoint other)
        {
            return Name.CompareTo(other.Name);
        }
        public GcPoint(GcDevice parentDevice, BacnetObjectId objectID)
        {
            ObjectID = objectID;
            ParentDevice = parentDevice;
            _name = objectID.ToString();
            Properties = new List<GcProperty>();
        }
    }

    public class GcProperty
    {
        //public static bool FORCE_SEND_WRITE = false;
        private static string ArrayToString(object[] arrayObj)
        {
            return GcUtil.ArrayToString(arrayObj);
        }
        private static string ByteArrayToString(byte[] arrayObj)
        {
            return GcUtil.ByteArrayToString(arrayObj);
        }

        public string ParentDeviceName { get { return ParentPoint.ParentDevice.Name; } }
        public string ParentPointName { get { return ParentPoint.Name; } }
        public BacnetPropertyIds PropertyID { get; }
        public GcPoint ParentPoint { get; }
        public uint ArrayIndex { get; }
        private string _name;
        public string Name { get { return _name; } set { _nameIsSet = true; _name = value; } }
        private bool _nameIsSet;
        public bool NameIsSet { get { return _nameIsSet; } }

        public static bool IsPrintableValue(object t)
        {
            if (t == null)
                return true;
            else if (t is string)
                return true;
            else if (t is long || t is int || t is short || t is sbyte)
                return true;
            else if (t is ulong || t is uint || t is ushort || t is byte)
                return true;
            else if (t is bool)
                return true;
            else if (t is float || t is double)
                return true;
            else if (t is BacnetBitString)
                return true;
            else if (t is BacnetObjectId)
                return true;
            else if (t is byte[])
                return true;
            else if (t is IPAddress)
                return true;
            else
                return false;
        }
        public static bool IsPrintableValue(BacnetValue bv)
        {
            object t = bv.Value;
            return IsPrintableValue(t);
        }
        public static bool ContainsNonPrintableValues(List<BacnetValue> values)
        {
            foreach (BacnetValue bv in values)
            {
                if (!IsPrintableValue(bv))
                {
                    return true;
                }
            }

            return false;
        }
        public bool ExportAsJSON
        {
            get
            {
                return ContainsNonPrintableValues(Values);
            }
        }
        public List<BacnetValue> Values { get; private set; }
        public List<Type> ValueTypes { get; private set; }
        public Tuple<BacnetValue[], bool[]> ParseInput(string inputValue, bool throwIfLengthIsWrong = true)
        {
            string enteredValue = null;
            if (inputValue != null)
            {
                enteredValue = inputValue.Trim();
            }

            string[] enteredValues = null;
            string[] existingStringValues = null;
            bool[] valuesChanged = null;

            int dataLength = 0;

            BacnetValue[] newBacnetValues = null;

            if ((throwIfLengthIsWrong && (Values.Count > 1)) ||
                (!throwIfLengthIsWrong && (enteredValue != null && enteredValue.StartsWith("[") && enteredValue.EndsWith("]"))))
            {
                // It's an array - most likely PRIORITY_ARRAY or byte[] array.

                if (enteredValue==null)
                {
                    throw new FormatException(String.Format("Couldn't parse entered value for property {0} of device {1}, object {2}. The array format was not correct. Please enter the array as follows: [x1,x2,x3,x4,...]", PropertyID.ToString(), ParentPoint.ParentDevice.Name, ParentPoint.Name));
                }

                if (!(enteredValue.StartsWith("[") && enteredValue.EndsWith("]")))
                {
                    throw new FormatException(String.Format("Couldn't parse entered value for property {0} of device {1}, object {2}. The array format was not correct. Please enter the array as follows: [x1,x2,x3,x4,...]", PropertyID.ToString(), ParentPoint.ParentDevice.Name, ParentPoint.Name));
                }

                // Account for the fact we might have arrays within arrays - i.e. an array of byte arrays (currently the only supported case) - DNS servers maybe?
                string enteredValueWithoutBrackets = enteredValue.Substring(1, enteredValue.Length - 2);

                if (enteredValue.Contains("[["))
                {
                    var result = new List<string>();
                    int depth = 0;
                    int start = 0;

                    for (int i = 0; i < enteredValueWithoutBrackets.Length; i++)
                    {
                        if (enteredValueWithoutBrackets[i] == '[')
                        {
                            if (depth == 0) start = i;
                            depth++;
                        }
                        else if (enteredValueWithoutBrackets[i] == ']')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                string segment = enteredValueWithoutBrackets.Substring(start, i - start + 1);
                                result.Add(segment);
                            }
                        }
                    }

                    enteredValues = result.ToArray();
                }
                else
                {
                    // Normal array
                    enteredValues = enteredValueWithoutBrackets.Split(',');
                }

                dataLength = enteredValues.Length;
                if (throwIfLengthIsWrong && dataLength != Values.Count)
                {
                    throw new FormatException(String.Format("Couldn't parse entered value for property {0} of device {1}, object {2}. The array format was not correct (incorrect number of elements).", PropertyID.ToString(), ParentPoint.ParentDevice.Name, ParentPoint.Name));
                }

                for (int i = 0; i < dataLength; i++)
                {
                    enteredValues[i] = enteredValues[i].Trim();
                }

                string existingArray = ArrayToString((object[])ValueObject);
                string existingArrayWithoutBrackets = existingArray.Replace("[", "").Replace("]", "");
                existingStringValues = existingArrayWithoutBrackets.Split(',');
                for (int i = 0; i < existingStringValues.Length; i++)
                {
                    existingStringValues[i] = existingStringValues[i].Trim();
                }
            }
            else
            {
                // Not an array, just a single value
                dataLength = 1;

                enteredValues = new string[dataLength];
                enteredValues[0] = enteredValue;
                // existingStringValues needed for the priority array, to avoid sending 16 commands every time.
                existingStringValues = new string[dataLength];
                existingStringValues[0] = ValueObjectString;
            }

            // --------------------------------------------------------------------------------------------------
            // Below is relevant if we are NOT entering the value as JSON (Note: JOSN entry not yet implemented).
            // --------------------------------------------------------------------------------------------------
            newBacnetValues = new BacnetValue[dataLength];
            for (int i = 0; i < dataLength; i++)
            {
                BacnetValue b_value = new BacnetValue();
                BacnetApplicationTags dataType = BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL;

                if (i<Values.Count)
                {
                    b_value = Values[i];
                    dataType = b_value.Tag;
                }

                // A NULL priority array slot has no type of its own: use the type of a set slot (or of Present_Value),
                // otherwise a typed value such as "22" is guessed as Unsigned and rejected by Real / Enumerated objects.
                Type slotType = null;
                if (dataType == BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL && PropertyID == BacnetPropertyIds.PROP_PRIORITY_ARRAY
                    && !(string.IsNullOrEmpty(enteredValues[i]) || enteredValues[i].Equals("null", StringComparison.OrdinalIgnoreCase)))
                {
                    int k = Values.FindIndex(v => v.Tag != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL && v.Value != null);
                    if (k >= 0) { dataType = Values[k].Tag; slotType = Values[k].Value.GetType(); }
                    else
                    {
                        var pvp = ParentPoint.Properties.Find(x => x.PropertyID == BacnetPropertyIds.PROP_PRESENT_VALUE);
                        if (pvp != null && pvp.Values.Count > 0 && pvp.Values[0].Value != null) { dataType = pvp.Values[0].Tag; slotType = pvp.Values[0].Value.GetType(); }
                    }
                }

                if (string.IsNullOrEmpty(enteredValues[i]) || (PropertyID == BacnetPropertyIds.PROP_PRIORITY_ARRAY && enteredValues[i].Equals("null", StringComparison.OrdinalIgnoreCase)))
                {
                    newBacnetValues[i] = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null);
                    continue;
                }

                bool gotDatatype = false;

                // --------------------------------------------------------------------------------------------------
                // 1. Check for type that we recorded before, and try to cast to it.
                // --------------------------------------------------------------------------------------------------
                if (!gotDatatype && ValueTypes!=null && ((ValueTypes.Count>i && ValueTypes[i] != null) || slotType != null) && dataType != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL)
                {
                    try
                    {
                        newBacnetValues[i] = GcUtil.ConvertVal(enteredValues[i], dataType, slotType ?? ValueTypes[i]);
                    }
                    catch (Exception ex)
                    {
                        throw new FormatException("Error converting types. " + ex.GetType().ToString() + " - " + ex.Message);
                    }
                    gotDatatype = true;
                }

                // --------------------------------------------------------------------------------------------------
                // 2. Maybe derive from the application tag if there is no Type for some reason
                // --------------------------------------------------------------------------------------------------
                if (!gotDatatype && dataType != BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL)
                {
                    try
                    {
                        newBacnetValues[i] = GcUtil.ConvertVal(enteredValues[i], dataType);
                    }
                    catch (Exception ex)
                    {
                        throw new FormatException("Error converting types. " + ex.GetType().ToString() + " - " + ex.Message);
                    }
                    gotDatatype = true;
                }

                // --------------------------------------------------------------------------------------------------
                // 3. Try and make up a data type from the value
                // --------------------------------------------------------------------------------------------------
                if (!gotDatatype)
                {
                    object o = null;
                    // try to convert to the simplest type
                    String[] typelist = { "Boolean", "UInt32", "Int32", "Single", "Double" };

                    foreach (String typename in typelist)
                    {
                        try
                        {
                            o = Convert.ChangeType(enteredValues[i], Type.GetType("System." + typename));
                            gotDatatype = true;
                            break;
                        }
                        catch { }
                    }

                    if (o == null)
                        newBacnetValues[i] = new BacnetValue(enteredValue);
                    else
                        newBacnetValues[i] = new BacnetValue(o);
                }
            }

            int changedArrayLength = Math.Max(dataLength, Values.Count);

            valuesChanged = new bool[changedArrayLength];
            for (int i = 0; i < changedArrayLength; i++)
            {
                if(i<dataLength && i < Values.Count)
                {
                    // Bug discovered 01/09/2026: enteredValues[i] could be null here, e.g. if we are trying to relinquish a value from a priority array.
                    // Bug introduced [r607], unfortunately a huge, messy commit.
                    // Fixed: by adding some extra null checks.
                    if ((enteredValues[i] != null && !enteredValues[i].Equals(existingStringValues[i])) ||
                        ((enteredValues[i] == null) != (existingStringValues[i] == null)))
                    {
                        valuesChanged[i] = true;
                    }
                }
                else if (i >= Values.Count)
                {
                    valuesChanged[i] = true;
                }
                else if (i >= dataLength)
                {
                    valuesChanged[i] = false;
                }
                else
                {
                    // Unreachable!!
                    valuesChanged[i] = false;
                }
            }

            return new Tuple<BacnetValue[], bool[]>(newBacnetValues,valuesChanged);
        }
        public static object GetValueObject(List<BacnetValue> values)
        {
            if(values==null)
            {
                return null;
            }
            if (values.Count > 1)
            {
                object[] arr = new object[values.Count];
                for (int j = 0; j < arr.Length; j++)
                    arr[j] = values[j].Value;
                return arr;
            }
            else if (values.Count == 1)
            {
                return values[0].Value;
            }
            else
            {
                return null;
            }
        }
        public object ValueObject
        {
            get
            {
                return GetValueObject(Values);
            }
        }
        public static string GetValueObjectString(List<BacnetValue> values, int index = -1)
        {
            object rawObjectValues = GetValueObject(values);
            if (rawObjectValues != null)
            {
                if (ContainsNonPrintableValues(values))
                {
                    if (index >= 0 && index < values.Count)
                    {
                        return "JSON (NOT EDITABLE): " + GcUtil.Json(values[index].Value);
                    }
                    else
                    {
                        return "JSON (NOT EDITABLE): " + GcUtil.Json(rawObjectValues);
                    }
                }
                else
                {
                    if (values.Count > 1)
                    {
                        if(index >= 0)
                        {
                            object arrayIndexObject = ((object[])rawObjectValues)[index];
                            if(arrayIndexObject is byte[] arrayIndexByteArray)
                            {
                                return ByteArrayToString(arrayIndexByteArray);
                            }
                            else
                            {
                                return arrayIndexObject.ToString();
                            }
                        }
                        else
                        {
                            return ArrayToString((object[])rawObjectValues);
                        }
                    }
                    else if (rawObjectValues is byte[] valueObjectByteArray)
                    {
                        return ByteArrayToString(valueObjectByteArray);
                    }
                    else
                    {
                        return rawObjectValues.ToString();
                    }
                }
            }
            else
            {
                return "";
            }
        }
        public string ValueObjectString
        {
            get
            {
                return GetValueObjectString(Values);
            }
            set
            {
                try
                {
                    BacnetClient comm = ParentPoint.ParentDevice.Comm;
                    BacnetAddress adr = ParentPoint.ParentDevice.DeviceAddress;
                    BacnetObjectId object_id = ParentPoint.ObjectID;

                    if (ExportAsJSON)
                    {
                        throw new FormatException(String.Format("Unfortunately, JSON is not editable at this point in time."));
                    }

                    uint writePriority = (uint)8;
                    writePriority = ParentPoint.ParentDevice.ParentWindow.DetermineWritePriority();

                    if (Values.Count < 1)
                    {
                        throw new FormatException(String.Format("Couldn't write property {0} of device {1}, object {2}. The Global Commander needs a value to already be present in order to command it to something else.", PropertyID.ToString(), ParentPoint.ParentDevice.Name, ParentPoint.Name));
                    }

                    Tuple<BacnetValue[], bool[]> parsedValues = ParseInput(value);
                    BacnetValue[] newBacnetValues = parsedValues.Item1;
                    bool[] valuesChanged = parsedValues.Item2;

                    /*bool atLeastOneValueChanged = false;
                    for (int i = 0; i < newBacnetValues.Length; i++)
                    {
                        if (valuesChanged[i])
                        {
                            atLeastOneValueChanged = true;
                            break;
                        }
                    }*/

                    //if (atLeastOneValueChanged || FORCE_SEND_WRITE)
                    //{
                        try
                        {
                            if (PropertyID == BacnetPropertyIds.PROP_PRIORITY_ARRAY)
                            {

                                for (int i = 0; i < newBacnetValues.Length; i++)
                                {
                                    uint priority = (uint)i + 1;
                                    if (!valuesChanged[i])
                                    {
                                        continue;
                                    }
                                    try
                                    {
                                        comm.WritePriority = priority;
                                        if (!comm.WritePropertyRequest(adr, object_id, BacnetPropertyIds.PROP_PRESENT_VALUE, new BacnetValue[1] { newBacnetValues[i] }))
                                        {
                                            throw new IOException(String.Format("Couldn't write property {0} at priority {1} of device {2}, object {3}", PropertyID.ToString(), priority, ParentPoint.ParentDevice.Name, ParentPoint.Name));
                                        }

                                    }
                                    catch (Exception ex)
                                    {
                                        throw new IOException(String.Format("Error writing property {0} at priority {1} to device {2}, object {3}. Exception: {4}.", PropertyID.ToString(), priority, ParentPoint.ParentDevice.Name, ParentPoint.Name, ex.GetType().ToString() + " - " + ex.Message));
                                    }
                                }
                            }
                            else
                            {
                                comm.WritePriority = writePriority;
                                if (!comm.WritePropertyRequest(adr, object_id, PropertyID, newBacnetValues))
                                {
                                    throw new IOException(String.Format("Couldn't write property {0} of device {1}, object {2}", PropertyID.ToString(), ParentPoint.ParentDevice.Name, ParentPoint.Name));

                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new IOException(String.Format("Error writing property {0} to device {1}, object {2}. Exception: {3}.", PropertyID.ToString(), ParentPoint.ParentDevice.Name, ParentPoint.Name, ex.GetType().ToString() + " - " + ex.Message));
                        }
                    //}

                    try
                    {
                        if (!comm.ReadPropertyRequest(adr, object_id, PropertyID, out IList<BacnetValue> valueList))
                        {
                            throw new IOException(String.Format("Couldn't read back property {0} of device {1}, object {2}", PropertyID.ToString(), ParentPoint.ParentDevice.Name, ParentPoint.Name));
                        }
                        Values = valueList.ToList<BacnetValue>();
                    }
                    catch (Exception ex)
                    {
                        throw new IOException(String.Format("Error reading back property {0} from device {1}, object {2}. Exception: {3}.", PropertyID.ToString(), ParentPoint.ParentDevice.Name, ParentPoint.Name, ex.GetType().ToString() + " - " + ex.Message));
                    }
                }
                finally
                {
                }
            }
        }
        public override string ToString()
        {
            return Name;
        }

        public GcProperty(GcPoint parentPoint, BacnetPropertyIds propertyID, uint arrayIndex = System.IO.BACnet.Serialize.ASN1.BACNET_ARRAY_ALL)
        {
            PropertyID = propertyID;
            ParentPoint = parentPoint;
            ArrayIndex = arrayIndex;
            Name = propertyID.ToString();
            Values = new List<BacnetValue>();
            ValueTypes = new List<Type>();
        }
    }
}
