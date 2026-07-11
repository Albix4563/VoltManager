using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VoltManager.Setup.Engine;
using VoltManager.Setup.Pages;

namespace VoltManager.Setup.Windows
{
    public partial class SetupWindow : Window
    {
        private readonly SetupArgs _args;
        private readonly InstallOptions _opts = new InstallOptions();
        private readonly InstallEngine _engine = new InstallEngine();

        private WelcomePage?  _welcome;
        private OptionsPage?  _options;
        private ProgressPage? _progress;
        private DonePage?     _done;

        private enum Step { Welcome, Options, Progress, Done }
        private Step _current;
        private bool _isUninstall;

        public SetupWindow(SetupArgs args)
        {
            _args = args;
            _isUninstall = args.Mode == SetupMode.Uninstall;
            InitializeComponent();
            LoadLogo();
            VersionText.Text = "v" + App.GetVersion();
            BuildSteps();
            NavigateTo(Step.Welcome);
        }

        // ── Logo from WPF resource (packed into VoltManagerSetup.g.resources) ──
        private void LoadLogo()
        {
            try
            {
                // WPF <Resource> items are NOT manifest resources — load via pack URI.
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri("pack://application:,,,/Assets/voltmanager.ico");
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                LogoBrush.ImageSource = img;
            }
            catch { /* logo not critical */ }
        }

        // ── Step indicator: numbered badge + label + connector line ──
        private string[] _stepLabels = System.Array.Empty<string>();

        private void BuildSteps()
        {
            _stepLabels = _isUninstall
                ? new[] { I18n.T("uninst_title"), I18n.T("progress_title"), I18n.T("done_title") }
                : new[] { I18n.T("welcome_title"), I18n.T("options_title"), I18n.T("progress_title"), I18n.T("done_title") };

            StepPanel.Children.Clear();
            for (int i = 0; i < _stepLabels.Length; i++)
            {
                bool last = i == _stepLabels.Length - 1;
                StepPanel.Children.Add(BuildStepRow(i, _stepLabels[i], last));
            }
        }

        // One row = a vertical line column (badge + connector) + label.
        private Grid BuildStepRow(int idx, string text, bool isLast)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, isLast ? 0 : 4), Tag = idx };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // connector line below the badge (skipped on last step)
            var line = new Border
            {
                Width = 2, Height = 22,
                CornerRadius = new CornerRadius(1),
                Background = Brush("BorderBrush2"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -11),
                Visibility = isLast ? Visibility.Collapsed : Visibility.Visible,
            };
            Grid.SetColumn(line, 0);
            Panel.SetZIndex(line, 0);

            // numbered badge
            var badge = new Border
            {
                Width = 30, Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = Brush("PillBrush"),
                BorderBrush = Brush("BorderBrush2"),
                BorderThickness = new Thickness(1.4),
                Margin = new Thickness(0, 7, 0, 7),
            };
            Panel.SetZIndex(badge, 1);
            var num = new TextBlock
            {
                Text = (idx + 1).ToString(),
                FontSize = 12.5, FontWeight = FontWeights.Bold,
                Foreground = Brush("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.Child = num;
            Grid.SetColumn(badge, 0);

            var lbl = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                Foreground = Brush("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(13, 0, 0, 0),
            };
            Grid.SetColumn(lbl, 1);

            row.Children.Add(line);
            row.Children.Add(badge);
            row.Children.Add(lbl);
            return row;
        }

        private void HighlightStep(int stepIdx)
        {
            var cyan = System.Windows.Media.Color.FromRgb(0x00, 0xF1, 0xFE);
            for (int i = 0; i < StepPanel.Children.Count; i++)
            {
                var row   = (Grid)StepPanel.Children[i];
                var line  = (Border)row.Children[0];
                var badge = (Border)row.Children[1];
                var num   = (TextBlock)badge.Child;
                var lbl   = (TextBlock)row.Children[2];
                bool active = i == stepIdx;
                bool done   = i < stepIdx;

                if (active)
                {
                    badge.Background = Brush("AccentGradient");
                    badge.BorderBrush = Brush("AccentBrush");
                    badge.Effect = new DropShadowEffect { Color = cyan, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.9 };
                    num.Foreground = Brush("AccentTextBrush");
                    lbl.Foreground = Brush("AccentBrush");
                    lbl.FontWeight = FontWeights.SemiBold;
                    line.Background = Brush("BorderBrush2");
                }
                else if (done)
                {
                    badge.Background = Brush("AccentSoftBrush");
                    badge.BorderBrush = Brush("AccentPressedBrush");
                    badge.Effect = null;
                    num.Text = "✓";
                    num.Foreground = Brush("AccentBrush");
                    lbl.Foreground = Brush("TextBrush");
                    lbl.FontWeight = FontWeights.Normal;
                    line.Background = Brush("AccentPressedBrush");
                }
                else
                {
                    badge.Background = Brush("PillBrush");
                    badge.BorderBrush = Brush("BorderBrush2");
                    badge.Effect = null;
                    num.Text = (i + 1).ToString();
                    num.Foreground = Brush("MutedBrush");
                    lbl.Foreground = Brush("MutedBrush");
                    lbl.FontWeight = FontWeights.Normal;
                    line.Background = Brush("BorderBrush2");
                }
            }

            // header breadcrumb on the right titlebar
            if (stepIdx >= 0 && stepIdx < _stepLabels.Length)
                HeaderText.Text = (_isUninstall ? "VoltManager  ·  " : "VoltManager Setup  ·  ") + _stepLabels[stepIdx];
        }

        // ── Navigation ───────────────────────────────────────────────
        private void NavigateTo(Step step)
        {
            _current = step;
            BtnBack.Visibility = Visibility.Collapsed;

            if (_isUninstall)
            {
                NavigateUninstall(step);
                return;
            }

            switch (step)
            {
                case Step.Welcome:
                    _welcome = new WelcomePage();
                    PageHost.Content = _welcome;
                    HighlightStep(0);
                    BtnCancel.Content = I18n.T("btn_cancel");
                    BtnCancel.Visibility = Visibility.Visible;
                    BtnNext.Content = I18n.T("btn_next");
                    BtnNext.IsEnabled = true;
                    break;

                case Step.Options:
                    _options = new OptionsPage(_opts);
                    PageHost.Content = _options;
                    HighlightStep(1);
                    BtnBack.Visibility = Visibility.Visible;
                    BtnBack.Content = I18n.T("btn_back");
                    BtnCancel.Visibility = Visibility.Visible;
                    BtnNext.Content = I18n.T("btn_install");
                    break;

                case Step.Progress:
                    _progress = new ProgressPage();
                    PageHost.Content = _progress;
                    HighlightStep(2);
                    BtnBack.Visibility = Visibility.Collapsed;
                    BtnCancel.Visibility = Visibility.Collapsed;
                    BtnNext.IsEnabled = false;
                    BtnNext.Content = I18n.T("btn_install");
                    StartInstall();
                    break;

                case Step.Done:
                    _done = new DonePage(_opts);
                    PageHost.Content = _done;
                    HighlightStep(3);
                    BtnCancel.Visibility = Visibility.Collapsed;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = I18n.T("btn_finish");
                    break;
            }
        }

        private void NavigateUninstall(Step step)
        {
            switch (step)
            {
                case Step.Welcome:
                    PageHost.Content = new UninstallConfirmPage();
                    HighlightStep(0);
                    BtnCancel.Content = I18n.T("btn_cancel");
                    BtnCancel.Visibility = Visibility.Visible;
                    BtnNext.Content = I18n.T("btn_uninstall");
                    BtnNext.IsEnabled = true;
                    break;

                case Step.Progress:
                    _progress = new ProgressPage();
                    PageHost.Content = _progress;
                    HighlightStep(1);
                    BtnCancel.Visibility = Visibility.Collapsed;
                    BtnNext.IsEnabled = false;
                    BtnNext.Content = I18n.T("btn_uninstall");
                    StartUninstall();
                    break;

                case Step.Done:
                    _done = new DonePage(null);
                    PageHost.Content = _done;
                    HighlightStep(2);
                    BtnCancel.Visibility = Visibility.Collapsed;
                    BtnNext.IsEnabled = true;
                    BtnNext.Content = I18n.T("btn_close");
                    break;
            }
        }

        // ── Install / Uninstall ──────────────────────────────────────
        private async void StartInstall()
        {
            _engine.Progress += (msg, pct) =>
                Dispatcher.Invoke(() =>
                {
                    _progress?.SetStatus(msg, pct);
                });

            bool ok = true;
            string? errMsg = null;
            try
            {
                _opts.InstallDir = _options?.GetInstallDir() ?? _opts.InstallDir;
                _opts.CreateDesktopShortcut = _options?.DesktopShortcut ?? true;
                _opts.StartWithWindows = _options?.StartWithWindows ?? false;
                _opts.EnableWidgets = _options?.EnableWidgets ?? false;
                _opts.EnabledWidgetTypes = _options?.GetEnabledWidgetTypes()
                    ?? new System.Collections.Generic.HashSet<string> { "clock", "calendar", "usage", "temps", "power" };
                _opts.LaunchAfterInstall = _options?.LaunchAfterInstall ?? true;

                await _engine.InstallAsync(_opts, App.GetVersion());
            }
            catch (Exception ex)
            {
                ok = false;
                errMsg = ex.Message;
            }

            _done = new DonePage(_opts, ok, errMsg);
            BtnNext.IsEnabled = true;
            BtnNext.Content = I18n.T("btn_finish");
            _current = Step.Done;
            HighlightStep(3);
            PageHost.Content = _done;
            BtnCancel.Visibility = Visibility.Collapsed;
        }

        private async void StartUninstall()
        {
            _engine.Progress += (msg, pct) =>
                Dispatcher.Invoke(() => _progress?.SetStatus(msg, pct));

            bool ok = true;
            string? err = null;
            try
            {
                var result = await _engine.UninstallAsync(_args.TargetDir);
                ok = result.Success;
                if (!ok) err = result.Summary;
            }
            catch (Exception ex) { ok = false; err = ex.Message; }

            _done = new DonePage(null, ok, err, uninstall: true);
            BtnNext.IsEnabled = true;
            BtnNext.Content = I18n.T("btn_close");
            _current = Step.Done;
            HighlightStep(2);
            PageHost.Content = _done;
        }

        // ── Button handlers ──────────────────────────────────────────
        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_isUninstall)
            {
                switch (_current)
                {
                    case Step.Welcome:  NavigateTo(Step.Progress); break;
                    case Step.Done:     Close(); break;
                }
                return;
            }

            switch (_current)
            {
                case Step.Welcome:  NavigateTo(Step.Options);   break;
                case Step.Options:  NavigateTo(Step.Progress);  break;
                case Step.Done:
                    _done?.LaunchIfRequested();
                    Close();
                    break;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_current == Step.Options) NavigateTo(Step.Welcome);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnMin_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private static Brush Brush(string key)
            => (Brush)Application.Current.FindResource(key);
    }
}
