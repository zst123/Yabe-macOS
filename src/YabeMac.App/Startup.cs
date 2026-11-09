using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Xml.Serialization;
using Yabe;

namespace YabeMac
{
    /// <summary>One-time loading of the original "Common Files" data (see MainDialog_Load in the original).</summary>
    public static class Startup
    {
        /// <summary>Directory of the executable; the original "Common Files" are copied there by the build.</summary>
        public static string AppDir => AppContext.BaseDirectory;

        /// <summary>Content of SimplifiedViewFilter.xml (null if absent/invalid). Original: YabeMainDialog.SimplifiedViewFilter.</summary>
        public static List<BacnetObjectDescription> SimplifiedViewFilter { get; private set; }

        static bool _done;

        /// <summary>
        /// Call ONCE very early (before any channel starts, e.g. in MainWindow.OnStartup / Program.Main).
        /// Sets the current directory to the app directory (BACnetDevice loads ReadSinglePropDescr.xml and
        /// VendorPropertyMapping.csv relative to it, exactly as on Windows) and calls the original loaders.
        /// </summary>
        public static void LoadOriginalData()
        {
            if (_done) return;
            _done = true;
            try { Directory.SetCurrentDirectory(AppDir); } catch { }

            try
            {
                string f = Path.Combine(AppDir, "SimplifiedViewFilter.xml");
                if (File.Exists(f))
                    using (var sr = new StreamReader(f))
                        SimplifiedViewFilter = (List<BacnetObjectDescription>)new XmlSerializer(typeof(List<BacnetObjectDescription>)).Deserialize(sr);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Error with SimplifiedViewFilter.xml :" + ex.Message); }

            try { BACnetDevice.LoadVendorPropertyMapping(); } catch { }
            try { BACnetDevice.LoadObjectsDescription(); } catch { }
        }
    }
}
