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
                IconPath.Data   = (System.Windows.Media.Geometry)FindResource("Icon.CheckCircle");
                IconPath.Stroke = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                Ring.Stroke     = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                StateHalo.Fill  = (System.Windows.Media.Brush)FindResource("GlowBlobSuccessBrush");
                RingGlow.Color  = System.Windows.Media.Color.FromRgb(0x34, 0xE0, 0xA1);
                TitleText.Text  = uninstall ? I18n.T("uninst_done") : I18n.T("done_title");
                SubText.Text    = uninstall ? "" : I18n.T("done_sub");

                if (!uninstall && opts != null)
                {
                    ChkLaunch.Content    = I18n.T("done_launch");
                    ChkLaunch.IsChecked  = opts.LaunchAfterInstall;
                    LaunchPill.Visibility = Visibility.Visible;
                }
            }
            else
            {
                IconPath.Data   = (System.Windows.Media.Geometry)FindResource("Icon.XCircle");
                IconPath.Stroke = (System.Windows.Media.Brush)FindResource("DangerBrush");
                Ring.Stroke     = (System.Windows.Media.Brush)FindResource("DangerBrush");
                StateHalo.Fill  = (System.Windows.Media.Brush)FindResource("GlowBlobDangerBrush");
                RingGlow.Color  = System.Windows.Media.Color.FromRgb(0xFF, 0x5B, 0x4A);
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
