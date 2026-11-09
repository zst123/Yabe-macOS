using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.IO.BACnet;

namespace YabeMac
{
    /// <summary>Port of Forms/CreateObject (also used as "Add hidden object" with the name input hidden).</summary>
    public partial class CreateObject : Window
    {
        public bool Accepted { get; private set; }

        public CreateObject() : this(true, null, null) { }

        public CreateObject(bool showObjectNameInput = true, string title = null, string createButtonName = null)
        {
            InitializeComponent();
            if (!showObjectNameInput)
            {
                ObjectNameTextBox.IsEnabled = false;
                ObjectNameTextBox.IsVisible = false;
                ObjectNameLabel.IsVisible = false;
            }
            if (!string.IsNullOrEmpty(title)) Title = title;
            if (!string.IsNullOrEmpty(createButtonName)) CreateButton.Content = createButtonName;

            // CreateObject_Load
            var items = new System.Collections.Generic.List<string>();
            for (int i = 0; i <= (int)BacnetObjectTypes.OBJECT_BINARY_LIGHTING_OUTPUT; i++)
                items.Add(Enum.GetName(typeof(BacnetObjectTypes), i));
            _items = items;
            ObjectTypeCombo.ItemsSource = items;
            ObjectTypeCombo.Text = items[0];
        }

        System.Collections.Generic.List<string> _items;
        /// <summary>Selected index in the list, or -1 when the user typed a free text (proprietary type number).</summary>
        public int SelectedTypeIndex => _items.IndexOf(TypeText);
        /// <summary>Text of the combo (the selected name or what was typed).</summary>
        public string TypeText => ObjectTypeCombo.Text ?? "";
        void TypeDropDown_Click(object s, RoutedEventArgs e) { ObjectTypeCombo.Focus(); ObjectTypeCombo.IsDropDownOpen = true; }
        public uint InstanceNumber => (uint)(ObjectInstanceNumeric.Value ?? 0);
        public string ObjectName => ObjectNameTextBox.Text ?? "";

        void CancelCommandButton_Click(object s, RoutedEventArgs e) => Close();
        void CreateButton_Click(object s, RoutedEventArgs e) { Accepted = true; Close(); }
    }
}
