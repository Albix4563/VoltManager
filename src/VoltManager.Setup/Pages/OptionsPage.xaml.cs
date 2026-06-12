using System.Windows.Controls;
using WinForms = System.Windows.Forms;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Pages
{
    public partial class OptionsPage : System.Windows.Controls.UserControl
    {
        public bool DesktopShortcut  => ChkDesktop.IsChecked == true;
        public bool StartWithWindows => ChkStartup.IsChecked == true;
        public bool LaunchAfterInstall => ChkLaunch.IsChecked == true;
        public string GetInstallDir() => TxtDir.Text.Trim();

        public OptionsPage(InstallOptions opts)
        {
            InitializeComponent();
            TitleText.Text    = I18n.T("options_title");
            LabelFolder.Text  = I18n.T("options_folder");
            BtnBrowse.Content = I18n.T("options_browse");
            ChkDesktop.Content  = I18n.T("options_desktop");
            ChkStartup.Content  = I18n.T("options_startup");
            ChkLaunch.Content   = I18n.T("options_launch");

            TxtDir.Text            = opts.InstallDir;
            ChkDesktop.IsChecked   = opts.CreateDesktopShortcut;
            ChkStartup.IsChecked   = opts.StartWithWindows;
            ChkLaunch.IsChecked    = opts.LaunchAfterInstall;
        }

        private void BtnBrowse_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = I18n.T("options_folder"),
                SelectedPath = TxtDir.Text,
            };
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                TxtDir.Text = dlg.SelectedPath;
        }
    }
}
