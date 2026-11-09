using Avalonia.Controls;
using System.Collections.Generic;
using System.Linq;

namespace YabeMac
{
    /// <summary>Port of WriteRecipeForm (MainDialog.UserCmd.cs): result list of a SnapShot / Write recipe.</summary>
    public partial class UserCommandStatus : Window
    {
        public class Row
        {
            public string Status { get; set; }
            public string Device { get; set; }
            public string DeviceName { get; set; }
            public string ObjectId { get; set; }
            public string ObjectName { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
        }

        public UserCommandStatus() { InitializeComponent(); }

        public UserCommandStatus(List<string> status) : this()
        {
            var rows = new List<Row>();
            foreach (string s in status)
            {
                string[] st = s.Split(';');
                string Sub(int i) => i < st.Length - 1 ? st[i] : "";   // all fields but the last (= Status)
                rows.Add(new Row { Status = st.Last(), Device = Sub(0), DeviceName = Sub(1), ObjectId = Sub(2), ObjectName = Sub(3), Property = Sub(4), Value = Sub(5) });
            }
            Grid.ItemsSource = rows;
        }
    }
}
