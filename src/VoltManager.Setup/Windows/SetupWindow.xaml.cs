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

        // ── Step indicator pills ─────────────────────────────────────
        private void BuildSteps()
        {
            string[] labels = _isUninstall
                ? new[] { I18n.T("uninst_title"), I18n.T("progress_title"), I18n.T("done_title") }
                : new[] { I18n.T("welcome_title"), I18n.T("options_title"), I18n.T("progress_title"), I18n.T("done_title") };

            StepPanel.Children.Clear();
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var dot = new Ellipse
                {
                    Width = 8, Height = 8,
                    Fill = Brush("BorderBrush2"),
                    Margin = new Thickness(0, 0, 10, 0),
                    Name = "Dot" + i,
                };
                var label = new TextBlock
                {
                    Text = labels[i],
                    FontSize = 12,
                    Foreground = Brush("MutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Name = "StepLabel" + i,
                };
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 6, 0, 6),
                    Tag = i,
                };
                row.Children.Add(dot);
                row.Children.Add(label);
                StepPanel.Children.Add(row);
            }
        }

        private void HighlightStep(int stepIdx)
        {
            for (int i = 0; i < StepPanel.Children.Count; i++)
            {
                var row = (StackPanel)StepPanel.Children[i];
                var dot   = (Ellipse)row.Children[0];
                var lbl   = (TextBlock)row.Children[1];
                bool active = i == stepIdx;
                bool done   = i < stepIdx;

                if (active)
                {
                    dot.Fill = Brush("AccentBrush");
                    dot.Width = dot.Height = 11;
                    dot.Effect = new DropShadowEffect
                    {
                        Color = System.Windows.Media.Color.FromRgb(0x00, 0xF1, 0xFE),
                        BlurRadius = 14, ShadowDepth = 0, Opacity = 0.95
                    };
                    lbl.Foreground = Brush("AccentBrush");
                    lbl.FontWeight = FontWeights.SemiBold;
                }
                else if (done)
                {
                    dot.Fill = Brush("AccentPressedBrush");
                    dot.Width = dot.Height = 8;
                    dot.Effect = null;
                    lbl.Foreground = Brush("TextBrush");
                    lbl.FontWeight = FontWeights.Normal;
                }
                else
                {
                    dot.Fill = Brush("BorderBrush2");
                    dot.Width = dot.Height = 8;
                    dot.Effect = null;
                    lbl.Foreground = Brush("MutedBrush");
                    lbl.FontWeight = FontWeights.Normal;
                }
            }
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
                await _engine.UninstallAsync(_args.TargetDir);
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

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private static Brush Brush(string key)
            => (Brush)Application.Current.FindResource(key);
    }
}
