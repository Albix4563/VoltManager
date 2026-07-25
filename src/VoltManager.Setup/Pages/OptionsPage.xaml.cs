using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Pages
{
    public partial class OptionsPage : System.Windows.Controls.UserControl
    {
        public bool DesktopShortcut  => ChkDesktop.IsChecked == true;
        public bool StartWithWindows => ChkStartup.IsChecked == true;
        public bool EnableWidgets => ChkWidgets.IsChecked == true;
        public bool LaunchAfterInstall => ChkLaunch.IsChecked == true;

        /// <summary>Install directory from the UI; never empty (falls back to default).</summary>
        public string GetInstallDir() => InstallOptions.NormalizeInstallDir(TxtDir.Text);

        public OptionsPage(InstallOptions opts)
        {
            InitializeComponent();
            TitleText.Text    = I18n.T("options_title");
            LabelFolder.Text  = I18n.T("options_folder");
            BtnBrowse.Content = I18n.T("options_browse");
            ChkDesktop.Content  = I18n.T("options_desktop");
            ChkStartup.Content  = I18n.T("options_startup");
            ChkWidgets.Content  = I18n.T("options_widgets");
            ChkLaunch.Content   = I18n.T("options_launch");
            DescDesktop.Text    = I18n.T("options_desktop_d");
            DescStartup.Text    = I18n.T("options_startup_d");
            DescWidgets.Text    = I18n.T("options_widgets_d");
            DescLaunch.Text     = I18n.T("options_launch_d");

            LblWidgetPick.Text   = I18n.T("options_widgets_select");
            ChkWClock.Content    = I18n.T("widget_clock");
            ChkWCalendar.Content = I18n.T("widget_calendar");
            ChkWUsage.Content    = I18n.T("widget_usage");
            ChkWTemps.Content    = I18n.T("widget_temps");
            ChkWPower.Content    = I18n.T("widget_power");
            ChkWPlans.Content    = I18n.T("widget_plans");

            // Always show a concrete default path (never leave the field blank).
            ApplyInstallDir(opts.InstallDir);
            ChkDesktop.IsChecked   = opts.CreateDesktopShortcut;
            ChkStartup.IsChecked   = opts.StartWithWindows;
            ChkWidgets.IsChecked   = opts.EnableWidgets;

            // stato iniziale del picker basato su opts (nessun widget pre-selezionato)
            WidgetPicker.Visibility = opts.EnableWidgets ? Visibility.Visible : Visibility.Collapsed;
            var selected = opts.EnabledWidgetTypes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in new[] { ("clock",ChkWClock),("calendar",ChkWCalendar),
                                       ("usage",ChkWUsage),("temps",ChkWTemps),("power",ChkWPower),("plans",ChkWPlans) })
                t.Item2.IsChecked = selected.Contains(t.Item1);
            ChkLaunch.IsChecked    = opts.LaunchAfterInstall;

            // Re-apply after layout in case a style/template reset the visual Text.
            Loaded += (_, __) =>
            {
                if (string.IsNullOrWhiteSpace(TxtDir.Text))
                    ApplyInstallDir(null);
            };
        }

        private void ApplyInstallDir(string? preferred)
        {
            string path = InstallOptions.NormalizeInstallDir(preferred);
            TxtDir.Text = path;
            // Ensure the caret is at the start so long paths show the drive letter first.
            TxtDir.CaretIndex = 0;
            TxtDir.ScrollToHome();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = I18n.T("options_folder"),
                SelectedPath = string.IsNullOrWhiteSpace(TxtDir.Text)
                    ? InstallOptions.GetDefaultInstallDir()
                    : TxtDir.Text,
            };
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                ApplyInstallDir(dlg.SelectedPath);
        }

        private void ChkWidgets_Changed(object sender, RoutedEventArgs e)
            => WidgetPicker.Visibility = ChkWidgets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        public HashSet<string> GetEnabledWidgetTypes()
        {
            var s = new HashSet<string>();
            if (ChkWClock.IsChecked    == true) s.Add("clock");
            if (ChkWCalendar.IsChecked == true) s.Add("calendar");
            if (ChkWUsage.IsChecked    == true) s.Add("usage");
            if (ChkWTemps.IsChecked    == true) s.Add("temps");
            if (ChkWPower.IsChecked    == true) s.Add("power");
            if (ChkWPlans.IsChecked    == true) s.Add("plans");
            return s;
        }
    }
}
