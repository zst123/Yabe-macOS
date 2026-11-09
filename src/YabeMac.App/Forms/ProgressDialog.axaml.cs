using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;

namespace YabeMac
{
    /// <summary>
    /// Port of Forms/ProgressDialog: non-modal progress window shown while a file transfer runs on a worker thread.
    /// Value/Label/Maximum may be set from any thread. Closing the window (or Cancel) raises Cancel.
    /// </summary>
    public partial class ProgressDialog : Window
    {
        public event EventHandler Cancel;
        public bool IsCancelled { get; private set; }

        public ProgressDialog()
        {
            InitializeComponent();
            Closing += (_, __) => { IsCancelled = true; Cancel?.Invoke(this, null); };
        }

        static void Ui(Action a) { if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a); }

        public int Minimum { get => (int)m_progessbar.Minimum; set => Ui(() => m_progessbar.Minimum = value); }
        public int Maximum { get => (int)m_progessbar.Maximum; set => Ui(() => m_progessbar.Maximum = Math.Max(value, 1)); }
        public int Value { get => (int)m_progessbar.Value; set => Ui(() => m_progessbar.Value = Math.Min(value, m_progessbar.Maximum)); }
        public string Label { get => m_Label.Text; set => Ui(() => m_Label.Text = value); }
        public void Increment(int v) => Ui(() => m_progessbar.Value = Math.Min(m_progessbar.Value + v, m_progessbar.Maximum));

        void OnCancelClick(object s, RoutedEventArgs e) => Close();

        /// <summary>Close without raising Cancel (the original used Hide()).</summary>
        public void Finish() => Ui(() => { Cancel = null; Close(); });
    }
}
