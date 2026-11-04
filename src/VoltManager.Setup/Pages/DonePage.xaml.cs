using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Pages
{
    public partial class DonePage : UserControl
    {
        private readonly InstallOptions? _opts;

        public DonePage(
            InstallOptions? opts,
            bool success = true,
            string? errMsg = null,
            bool uninstall = false,
            string? warningMsg = null)
        {
            _opts = opts;
            InitializeComponent();

            if (success)
            {
                IconPath.Data   = (Geometry)FindResource("Icon.CheckCircle");
                IconPath.Stroke = (Brush)FindResource("SuccessBrush");
                Ring.Stroke     = (Brush)FindResource("SuccessBrush");
                TitleText.Text  = uninstall ? I18n.T("uninst_done") : I18n.T("done_title");
                SubText.Text    = uninstall ? "" : I18n.T("done_sub");

                if (!uninstall && opts != null)
                {
                    ChkLaunch.Content    = I18n.T("done_launch");
                    ChkLaunch.IsChecked  = opts.LaunchAfterInstall;
                    LaunchPill.Visibility = Visibility.Visible;
                }

                if (!string.IsNullOrWhiteSpace(warningMsg))
                {
                    ErrCard.Background = (Brush)FindResource("InsetBrush");
                    ErrCard.BorderBrush = (Brush)FindResource("WarningBrush");
                    ErrText.Foreground = (Brush)FindResource("WarningBrush");
                    ErrText.Text = I18n.T(uninstall ? "uninst_notes" : "done_warning") + "\n" + warningMsg;
                    ErrCard.Visibility = Visibility.Visible;
                }
            }
            else
            {
                IconPath.Data   = (Geometry)FindResource("Icon.XCircle");
                IconPath.Stroke = (Brush)FindResource("DangerBrush");
                Ring.Stroke     = (Brush)FindResource("DangerBrush");
                TitleText.Text  = I18n.T("done_title_err");
                if (!string.IsNullOrEmpty(errMsg))
                {
                    ErrText.Text       = errMsg;
                    ErrCard.Visibility = Visibility.Visible;
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
