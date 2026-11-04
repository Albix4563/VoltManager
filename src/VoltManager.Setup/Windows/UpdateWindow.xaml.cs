using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Windows
{
    public partial class UpdateWindow : Window
    {
        // System menu commands used to gate the native window controls.
        private const uint ScClose = 0xF060;
        private const uint ScMaximize = 0xF030;
        private const uint MfByCommand = 0x00000000;
        private const uint MfEnabled = 0x00000000;
        private const uint MfGrayed = 0x00000001;
        private const uint MfDisabled = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr windowHandle, bool revert);

        // Win32 returns the previous item state, not a success flag.
        [DllImport("user32.dll")]
        private static extern int EnableMenuItem(IntPtr menuHandle, uint item, uint enable);

        [DllImport("user32.dll")]
        private static extern bool DrawMenuBar(IntPtr windowHandle);

        private readonly TextBlock[] _stageLabels = new TextBlock[4];
        private readonly Border[] _stageMarkers = new Border[4];
        private bool _busy = true;
        private Func<System.Threading.Tasks.Task>? _retryLaunch;
        private bool _retryPending;
        private bool _canAbort;

        public event EventHandler? FirstRendered;
        public event EventHandler? UpdateAborted;

        private bool IsCloseBlocked => _busy && !_canAbort;

        public void SetVersion(string version)
            => VersionText.Text = string.Format(I18n.T("update_version"), version);

        public void SetWaitingForProceed()
        {
            _canAbort = true;
            _busy = true;
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.Content = I18n.T("btn_cancel");
            CloseButton.IsDefault = true;
            CloseButton.Focus();
            ApplyWindowCommandState();
        }

        public void SetUpdateStarted()
        {
            _canAbort = false;
            _busy = true;
            CloseButton.Visibility = Visibility.Collapsed;
            CloseButton.Content = I18n.T("btn_close");
            CloseButton.IsDefault = false;
            ApplyWindowCommandState();
        }

        public UpdateWindow()
        {
            InitializeComponent();
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 28);
            // The handle can already exist when the host sets the waiting state,
            // so the native guard is applied again on source initialization.
            SourceInitialized += (_, __) => ApplyWindowCommandState();
            if (!SystemParameters.ClientAreaAnimation)
                BusyIndicator.Style = (Style)FindResource("UpdateStaticProgress");
            ModeText.Text = I18n.T("update_mode");
            HeadingText.Text = I18n.T("update_title");
            DescriptionText.Text = I18n.T("update_description");
            MinimizeHintText.Text = I18n.T("update_minimize_hint");
            LogButton.Content = I18n.T("update_log");
            RetryLaunchButton.Content = I18n.T("update_retry_launch");
            CloseButton.Content = I18n.T("btn_close");
            AutomationProperties.SetName(BusyIndicator, I18n.T("update_progress"));
            string[] stages = {
                I18n.T("update_stage_prepare"),
                I18n.T("update_stage_install"),
                I18n.T("update_stage_finalize"),
                I18n.T("update_stage_reopen")
            };

            for (int index = 0; index < stages.Length; index++)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var badge = new Border {
                    Height = 24, Width = 24, CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)FindResource("BorderBrush"),
                    Background = (Brush)FindResource("SurfaceBrush")
                };
                badge.Child = new TextBlock {
                    Text = (index + 1).ToString(), FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextMutedBrush")
                };
                _stageMarkers[index] = badge;
                var label = new TextBlock {
                    Text = stages[index], FontSize = 13.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("TextMutedBrush")
                };
                _stageLabels[index] = label;
                Grid.SetColumn(label, 1);
                row.Children.Add(badge);
                row.Children.Add(label);
                StagesPanel.Children.Add(row);
            }

            ReportStage(0);
        }

        public void ReportStage(int stage, string? detail = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ReportStage(stage, detail)));
                return;
            }
            if (stage < 0 || stage >= _stageLabels.Length) return;

            for (int i = 0; i < _stageLabels.Length; i++)
            {
                bool done = i < stage;
                bool active = i == stage;
                _stageMarkers[i].Background = done
                    ? (Brush)FindResource("SuccessBrush")
                    : active ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("SurfaceBrush");
                _stageLabels[i].Foreground = active
                    ? (Brush)FindResource("TextPrimaryBrush")
                    : done ? (Brush)FindResource("TextSecondaryBrush") : (Brush)FindResource("TextMutedBrush");
                if (_stageMarkers[i].Child is TextBlock number)
                {
                    number.Text = done ? "✓" : (i + 1).ToString();
                    number.Foreground = done || active
                        ? ContrastOn(((SolidColorBrush)_stageMarkers[i].Background).Color)
                        : (Brush)FindResource("TextMutedBrush");
                }
            }

            CurrentStatusText.Text = string.IsNullOrWhiteSpace(detail)
                ? _stageLabels[stage].Text : detail;
        }

        public void ShowCompleted()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowCompleted));
                return;
            }
            _busy = false;
            _canAbort = false;
            BusyIndicator.Visibility = Visibility.Visible;
            BusyIndicator.IsIndeterminate = false;
            BusyIndicator.Value = 100;
            ReportStage(3, I18n.T("update_success"));
            for (int i = 0; i < _stageMarkers.Length; i++)
            {
                _stageMarkers[i].Background = (Brush)FindResource("SuccessBrush");
                if (_stageMarkers[i].Child is TextBlock number)
                {
                    number.Text = "✓";
                    number.Foreground = ContrastOn(((SolidColorBrush)FindResource("SuccessBrush")).Color);
                }
            }
            HeadingText.Text = I18n.T("update_success");
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.Focus();
            ApplyWindowCommandState();
        }

        public void ShowLegacyCompleted()
        {
            ShowCompleted();
            HeadingText.Text = I18n.T("update_legacy_success");
            CurrentStatusText.Text = I18n.T("update_legacy_success");
        }

        public void ShowFailure(Exception error, Func<System.Threading.Tasks.Task>? retryLaunch = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowFailure(error, retryLaunch)));
                return;
            }
            _busy = false;
            _canAbort = false;
            _retryPending = false;
            _retryLaunch = retryLaunch;
            HeadingText.Text = I18n.T("update_failure_title");
            CurrentStatusText.Text = I18n.T("update_failed_status");
            ErrorText.Text = error.Message;
            ErrorPanel.Visibility = Visibility.Visible;
            // No progress is invented for a failed run.
            BusyIndicator.Visibility = Visibility.Collapsed;
            RetryLaunchButton.Visibility = _retryLaunch != null ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.Focus();
            ApplyWindowCommandState();
        }

        private void UpdateWindow_ContentRendered(object sender, EventArgs e)
        {
            ContentRendered -= UpdateWindow_ContentRendered;
            FirstRendered?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_busy)
            {
                if (_canAbort)
                {
                    UpdateAborted?.Invoke(this, EventArgs.Empty);
                    return;
                }
                // Never interrupt filesystem replacement after the host proceeds.
                e.Cancel = true;
                ApplyWindowCommandState();
                return;
            }
        }

        /// <summary>
        /// Mirrors the managed closing guard on the native window: while the update
        /// owns the files only minimize/restore stay available. Alt+F4 and the
        /// taskbar still reach the Closing handler above as a second line of defence.
        /// </summary>
        private void ApplyWindowCommandState()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            IntPtr menu = GetSystemMenu(handle, false);
            if (menu == IntPtr.Zero) return;

            // This window never maximizes; growing it mid-update serves no purpose.
            EnableMenuItem(menu, ScMaximize, MfByCommand | MfGrayed | MfDisabled);
            EnableMenuItem(menu, ScClose, MfByCommand |
                (IsCloseBlocked ? MfGrayed | MfDisabled : MfEnabled));
            DrawMenuBar(handle);
        }

        private static Brush ContrastOn(Color background)
            => background.R * 0.299 + background.G * 0.587 + background.B * 0.114 > 150
                ? Brushes.Black
                : Brushes.White;

        private void LogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(SetupUpdateLog.FilePath))
                    Process.Start(new ProcessStartInfo(SetupUpdateLog.FilePath) { UseShellExecute = true });
                else
                    MessageBox.Show(I18n.T("update_log_missing"), "VoltManager",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "VoltManager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void RetryLaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_retryLaunch == null || _retryPending) return;
            _retryPending = true;
            _busy = true;
            RetryLaunchButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            BusyIndicator.Visibility = Visibility.Visible;
            BusyIndicator.IsIndeterminate = true;
            ErrorPanel.Visibility = Visibility.Collapsed;
            ReportStage(3);
            // Retrying re-enters the protected window: exit is blocked again.
            ApplyWindowCommandState();
            try
            {
                await _retryLaunch();
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Error("Retry application launch failed: " + ex);
                ShowFailure(ex, _retryLaunch);
            }
            finally
            {
                _retryPending = false;
                _busy = false;
                RetryLaunchButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
                ApplyWindowCommandState();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
