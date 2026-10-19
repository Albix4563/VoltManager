using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Windows
{
    public partial class UpdateWindow : Window
    {
        private readonly TextBlock[] _stageLabels = new TextBlock[4];
        private readonly Border[] _stageMarkers = new Border[4];
        private bool _busy = true;
        private Func<System.Threading.Tasks.Task>? _retryLaunch;
        private bool _retryPending;
        private bool _canAbort;

        public event EventHandler? FirstRendered;
        public event EventHandler? UpdateAborted;

        public void SetVersion(string version)
            => VersionText.Text = string.Format(I18n.T("update_version"), version);

        public void SetWaitingForProceed()
        {
            _canAbort = true;
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.Content = I18n.T("btn_cancel");
            CloseButton.IsDefault = true;
            CloseButton.Focus();
        }

        public void SetUpdateStarted()
        {
            _canAbort = false;
            CloseButton.Visibility = Visibility.Collapsed;
            CloseButton.Content = I18n.T("btn_close");
            CloseButton.IsDefault = false;
        }

        public UpdateWindow()
        {
            InitializeComponent();
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 28);
            if (!SystemParameters.ClientAreaAnimation)
                BusyIndicator.Style = (Style)FindResource("UpdateStaticProgress");
            HeadingText.Text = I18n.T("update_title");
            DescriptionText.Text = I18n.T("update_description");
            LogButton.Content = I18n.T("update_log");
            RetryLaunchButton.Content = I18n.T("update_retry_launch");
            CloseButton.Content = I18n.T("btn_close");
            string[] stages = {
                I18n.T("update_stage_prepare"),
                I18n.T("update_stage_install"),
                I18n.T("update_stage_finalize"),
                I18n.T("update_stage_reopen")
            };

            for (int index = 0; index < stages.Length; index++)
            {
                var row = new Grid { Margin = new Thickness(0, 6, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var badge = new Border {
                    Height = 23, Width = 23, CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                    BorderBrush = (Brush)FindResource("BorderBrush"),
                    Background = (Brush)FindResource("SurfaceBrush")
                };
                badge.Child = new TextBlock {
                    Text = (index + 1).ToString(), FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextMutedBrush")
                };
                _stageMarkers[index] = badge;
                var label = new TextBlock {
                    Text = stages[index], FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
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
                        ? Brushes.Black : (Brush)FindResource("TextMutedBrush");
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
            BusyIndicator.IsIndeterminate = false;
            BusyIndicator.Value = 100;
            ReportStage(3, I18n.T("update_success"));
            for (int i = 0; i < _stageMarkers.Length; i++)
            {
                _stageMarkers[i].Background = (Brush)FindResource("SuccessBrush");
                if (_stageMarkers[i].Child is TextBlock number)
                {
                    number.Text = "✓";
                    number.Foreground = Brushes.Black;
                }
            }
            HeadingText.Text = I18n.T("update_success");
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.Focus();
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
            _retryPending = false;
            _retryLaunch = retryLaunch;
            HeadingText.Text = I18n.T("update_failure_title");
            CurrentStatusText.Text = I18n.T("update_failed_status");
            ErrorText.Text = error.Message;
            ErrorText.Visibility = Visibility.Visible;
            BusyIndicator.IsIndeterminate = false;
            BusyIndicator.Value = 0;
            RetryLaunchButton.Visibility = _retryLaunch != null ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.Focus();
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
                return;
            }
        }

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
            BusyIndicator.IsIndeterminate = true;
            ErrorText.Visibility = Visibility.Collapsed;
            ReportStage(3);
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
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
