using System;
using System.Collections.Generic;
using System.IO;
using System.IO.BACnet;
using System.Linq;
using System.Xml.Serialization;

namespace YabeMac.PropertyPanel
{
    /// <summary>Common Files/SimplifiedViewFilter.xml (objects and properties shown in the simplified / field technician view).</summary>
    public static class SimplifiedFilter
    {
        static List<BacnetObjectDescription> _list;
        static bool _loaded;
        public static string LoadError;

        public static List<BacnetObjectDescription> Descriptions
        {
            get
            {
                if (!_loaded) Load();
                return _list;
            }
        }

        public static string FindFile()
        {
            var cands = new List<string> { Path.Combine(AppContext.BaseDirectory, "SimplifiedViewFilter.xml"), "SimplifiedViewFilter.xml" };
            // development layout: walk up to external/yabe-svn/Yabe/Common Files
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                cands.Add(Path.Combine(d.FullName, "external", "yabe-svn", "Yabe", "Common Files", "SimplifiedViewFilter.xml"));
            return cands.FirstOrDefault(File.Exists);
        }

        public static void Load(string path = null)
        {
            _loaded = true;
            _list = null;
            try
            {
                path = path ?? FindFile();
                if (path == null) { LoadError = "SimplifiedViewFilter.xml not found"; return; }
                var xs = new XmlSerializer(typeof(List<BacnetObjectDescription>));
                using (var sr = new StreamReader(path))
                    _list = (List<BacnetObjectDescription>)xs.Deserialize(sr);
            }
            catch (Exception ex) { LoadError = ex.Message; _list = null; }
        }

        /// <summary>Property filter of an object type, or null when the object type is not described (no filtering, as in the original).</summary>
        public static List<BacnetPropertyIds> PropertiesFor(BacnetObjectTypes type)
        {
            var l = Descriptions;
            if (l == null) return null;
            var d = l.Find(o => o.typeId == type);
            return d.propsId;   // default(struct).propsId == null when absent
        }

        public static bool ObjectVisible(BacnetObjectTypes type) => Descriptions == null || Descriptions.Exists(o => o.typeId == type);
    }
}
