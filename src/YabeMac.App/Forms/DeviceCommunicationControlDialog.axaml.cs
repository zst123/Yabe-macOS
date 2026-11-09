using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.IO.BACnet;

namespace YabeMac
{
    /// <summary>Port of Forms/DeviceCommunicationControlDialog.</summary>
    public partial class DeviceCommunicationControlDialog : Window
    {
        public bool Accepted { get; private set; }

        public DeviceCommunicationControlDialog()
        {
            InitializeComponent();
            // DeviceCommunicationControlDialog_Load: BACNET_REINIT_IDLE is not put into the combo list
            string[] names = Enum.GetNames(typeof(BacnetReinitializedStates));
            var list = new System.Collections.Generic.List<string>();
            for (int i = 0; i < names.Length - 1; i++) list.Add(names[i].Replace("BACNET_REINIT_", ""));
            m_StateCombo.ItemsSource = list;
            m_StateCombo.SelectedIndex = 0;
        }

        public bool IsReinitialize { get => m_reinitializeRadio.IsChecked == true; set => m_reinitializeRadio.IsChecked = value; }
        public bool DisableCommunication { get => m_disableCheck.IsChecked == true; set => m_disableCheck.IsChecked = value; }
        public uint Duration { get => (uint)(m_durationValue.Value ?? 0); set => m_durationValue.Value = value; }
        public string Password { get => m_passwordText.Text ?? ""; set => m_passwordText.Text = value; }
        public BacnetReinitializedStates ReinitializeState =>
            (BacnetReinitializedStates)Enum.Parse(typeof(BacnetReinitializedStates), "BACNET_REINIT_" + (m_StateCombo.SelectedItem as string));

        void reinitializeRadio_CheckedChanged(object s, RoutedEventArgs e)
        {
            if (m_reinitializeGroup == null || m_communicationGroup == null) return;
            bool re = m_reinitializeRadio.IsChecked == true;
            m_reinitializeGroup.IsEnabled = re;
            m_communicationGroup.IsEnabled = !re;
        }

        void OnOk(object s, RoutedEventArgs e) { Accepted = true; Close(); }
        void OnCancel(object s, RoutedEventArgs e) => Close();
    }
}
