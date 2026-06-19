using System.Windows.Controls;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Pages
{
    public partial class ProgressPage : UserControl
    {
        public ProgressPage()
        {
            InitializeComponent();
            TitleText.Text = I18n.T("progress_title");
            WaitText.Text  = I18n.T("progress_wait");
            PctText.Text   = "0%";
        }

        public void SetStatus(string msg, double pct)
        {
            if (!string.IsNullOrEmpty(msg)) StatusText.Text = msg;
            Bar.Value  = pct;
            PctText.Text = (int)pct + "%";
        }
    }
}
