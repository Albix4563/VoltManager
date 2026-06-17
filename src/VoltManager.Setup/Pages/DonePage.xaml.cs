using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Pages
{
    public partial class DonePage : UserControl
    {
        private readonly InstallOptions? _opts;

        public DonePage(InstallOptions? opts, bool success = true, string? errMsg = null, bool uninstall = false)
        {
            _opts = opts;
            InitializeComponent();

            if (success)
            {
                IconText.Text     = "✓";
                IconText.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush");
                TitleText.Text    = uninstall ? I18n.T("uninst_done") : I18n.T("done_title");
                SubText.Text      = uninstall ? "" : I18n.T("done_sub");

                if (!uninstall && opts != null)
                {
                    ChkLaunch.Content    = I18n.T("done_launch");
                    ChkLaunch.IsChecked  = opts.LaunchAfterInstall;
                    ChkLaunch.Visibility = Visibility.Visible;
                }
            }
            else
            {
                IconText.Text = "✗";
                IconText.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("DangerBrush");
                TitleText.Text = I18n.T("done_title_err");
                if (!string.IsNullOrEmpty(errMsg))
                {
                    ErrText.Text       = errMsg;
                    ErrText.Visibility = Visibility.Visible;
                }
            }
        }

        public void LaunchIfRequested()
        {
            if (ChkLaunch.IsChecked != true || _opts == null) return;
            string exe = Path.Combine(_opts.InstallDir, "VoltManager.exe");
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
    }
}
