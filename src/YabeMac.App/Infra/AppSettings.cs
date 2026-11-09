using System;
using System.IO;
using System.Text.Json;

namespace YabeMac
{
    /// <summary>
    /// Replacement for the original Properties.Settings (WinForms user settings / registry).
    /// Persisted as JSON in ~/Library/Application Support/YabeMac/settings.json.
    /// Property names and defaults mirror Yabe/Properties/Settings.settings.
    /// </summary>
    public class AppSettings
    {
        public static AppSettings Default { get; private set; } = Load();

        public int GUI_SplitterLeft { get; set; } = -1;
        public int GUI_SplitterRight { get; set; } = -1;
        public int GUI_SplitterButtom { get; set; } = -1;
        public int GUI_SplitterMiddle { get; set; } = -1;
        public bool Udp_ExclusiveUseOfSocket { get; set; }
        public bool MSTP_DisplayFreeAddresses { get; set; } = true;
        public bool MSTP_LogStateMachine { get; set; }
        public decimal DefaultRetries { get; set; } = 1;
        public decimal DefaultTimeout { get; set; } = 500;
        public decimal DefaultUdpPort { get; set; } = 47808;
        public decimal DefaultBaudrate { get; set; } = 38400;
        public decimal DefaultSourceAddress { get; set; } = -1;
        public decimal DefaultMaxMaster { get; set; } = 127;
        public decimal DefaultMaxInfoFrames { get; set; } = 1;
        public bool Subscriptions_IssueConfirmedNotifies { get; set; }
        public uint Subscriptions_Lifetime { get; set; } = 120;
        public int DefaultDownloadSpeed { get; set; }
        public byte Segments_ProposedWindowSize { get; set; } = 20;
        public byte Segments_Max { get; set; } = 65;
        public bool TimeSynchronize_UTC { get; set; }
        public bool Udp_DontFragment { get; set; }
        public int Udp_MaxPayload { get; set; } = 1472;
        public string Address_Space_Structured_View { get; set; } = "List";
        public string DefaultWritePriority { get; set; } = "LOWEST_AND_DEFAULT";
        public int YabeDeviceId { get; set; } = -1;
        public uint Subscriptions_ReplacementPollingPeriod { get; set; } = 1000;
        public string Auto_Store_Object_Names_File { get; set; } = "";
        public bool IPv6_Support { get; set; }
        public bool ShowDescriptionWhenUseful { get; set; }
        public bool DisplayIdWithName { get; set; } = true;
        public string Plugins { get; set; } = "";
        public string GraphDotStyle { get; set; } = "Square";
        public string GraphLineStep { get; set; } = "Automatic";
        public string COVTimeFormater { get; set; } = "HH:mm:ss";
        public string COV_Export_Path { get; set; } = "";
        public bool UsePollingByDefault { get; set; } = true;
        public bool Auto_Store_Object_Names { get; set; }
        public int Auto_Store_Period_Minutes { get; set; } = 5;
        public bool Vertical_Object_Splitter_Orientation { get; set; }
        public string BACnetSCConfigFile { get; set; } = "BACnetSCConfig.config";
        public bool Show_Property_Id_Numbers { get; set; }
        public bool ShowHighLightFilter { get; set; } = true;
        public bool EDE_SingleFile { get; set; } = true;
        public bool EDE_CommonFiles { get; set; }
        public string EDE_Separator { get; set; } = ";";
        public bool UseObjectsCache { get; set; } = true;
        public int GridArrayExpandMaxSize { get; set; } = 10;
        public string GridAlwaysExpandProperties { get; set; } = "Priority Array";
        public string DeviceViewMode { get; set; } = "Network";
        public string NotAffectedFolderName { get; set; } = "Not Affected";
        public string BackGroundOperations { get; set; } = "None";
        public int BackGroundThreadNumber { get; set; } = 1;
        public int IPv6_MulticastAddress { get; set; } = 4;
        public bool UseNativeMenu { get; set; } = true;   // macOS system menu bar instead of the in-window menu
        public bool GlassMergedTitleBar { get; set; }      // content under the title bar (traffic lights then look smaller)
        public bool GlassDark { get; set; } = true;       // force the dark appearance for the glass theme
        public bool UseGlassTheme { get; set; } = true;   // macOS translucent / Liquid-Glass-style look
        public bool ByteArray_PreferHex { get; set; }
        public string GUI_SubscriptionColumns { get; set; } = "";
        public string GUI_TrendReviewColumns { get; set; } = "";
        public string DefaultUdpIp { get; set; } = "";
        public string DefaultBBMD { get; set; } = "";
        public string DeviceClassStructure { get; set; } = "";
        public string GUI_LastFilename { get; set; } = "";

        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YabeMac");
        static string FilePath => Path.Combine(Dir, "settings.json");

        static AppSettings Load()
        {
            try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings(); }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }
    }
}
