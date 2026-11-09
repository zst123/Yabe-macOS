using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace YabeMac
{
    /// <summary>"Set Source Address" dialog for MS/TP (port of Forms/SourceAddressDialog).</summary>
    public partial class SourceAddressDialog : Window
    {
        public SourceAddressDialog() { InitializeComponent(); }

        public byte SourceAddress
        {
            get => (byte)(SourceAddressValue.Value ?? 0);
            set => SourceAddressValue.Value = value;
        }

        void OnOk(object s, RoutedEventArgs e) => Close(true);
        void OnCancel(object s, RoutedEventArgs e) => Close(false);

        /// <summary>Shows the dialog; returns the chosen address, or null when cancelled.</summary>
        public static async Task<byte?> Prompt(byte suggested, Window owner = null)
        {
            var dlg = new SourceAddressDialog { SourceAddress = suggested };
            bool ok = await dlg.ShowDialog<bool>(owner ?? MainWindow.Instance);
            return ok ? dlg.SourceAddress : (byte?)null;
        }
    }
}
