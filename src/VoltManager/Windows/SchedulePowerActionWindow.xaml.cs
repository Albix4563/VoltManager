using System.Windows;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager;

internal enum ScheduleDelayError
{
    None,
    InvalidHours,
    InvalidMinutes,
    MinDelay,
    MaxDelay,
}

public partial class SchedulePowerActionWindow : Window
{
    private readonly LocalizationService _loc;

    public TimeSpan SelectedDelay { get; private set; }
    public ScheduledPowerActionType SelectedAction { get; private set; } = ScheduledPowerActionType.Shutdown;

    public SchedulePowerActionWindow(LocalizationService loc)
    {
        _loc = loc;
        InitializeComponent();
        ApplyLocalization();
        UpdateSummary();

        HoursTextBox.TextChanged += (_, _) => UpdateSummary();
        MinutesTextBox.TextChanged += (_, _) => UpdateSummary();
        ShutdownRadio.Checked += (_, _) => UpdateSummary();
        SleepRadio.Checked += (_, _) => UpdateSummary();
    }

    private void ApplyLocalization()
    {
        Title = _loc.T("Schedule_CustomTitle");
        HoursLabel.Text = _loc.T("Schedule_Hours");
        MinutesLabel.Text = _loc.T("Schedule_Minutes");
        ActionLabel.Text = _loc.T("Schedule_Action");
        ShutdownText.Text = _loc.T("Schedule_Shutdown");
        SleepText.Text = _loc.T("Schedule_Sleep");
        SummaryLabel.Text = _loc.T("Schedule_SummaryWaiting");
        CancelButton.Content = _loc.T("Common_Cancel");
        ConfirmButton.Content = _loc.T("Schedule_Confirm");
    }

    private void UpdateSummary()
    {
        ErrorLabel.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = true;

        if (!TryBuildDelay(HoursTextBox.Text, MinutesTextBox.Text, out TimeSpan delay, out ScheduleDelayError error))
        {
            ShowError(error switch
            {
                ScheduleDelayError.InvalidHours => _loc.T("Schedule_InvalidHours"),
                ScheduleDelayError.InvalidMinutes => _loc.T("Schedule_InvalidMinutes"),
                ScheduleDelayError.MinDelay => _loc.T(
                    "Schedule_MinDelay", $"{(int)ScheduledPowerActionService.MinDelay.TotalMinutes}"),
                ScheduleDelayError.MaxDelay => _loc.T(
                    "Schedule_MaxDelay", $"{(int)ScheduledPowerActionService.MaxDelay.TotalDays}"),
                _ => _loc.T("Schedule_InvalidHours"),
            });
            return;
        }

        SelectedAction = ShutdownRadio.IsChecked == true
            ? ScheduledPowerActionType.Shutdown
            : ScheduledPowerActionType.Sleep;

        SelectedDelay = delay;

        DateTime executeAt = DateTime.Now.Add(delay);
        string actionName = SelectedAction == ScheduledPowerActionType.Shutdown
            ? _loc.T("Schedule_Shutdown")
            : _loc.T("Schedule_Sleep");

        SummaryLabel.Text = $"{actionName} {_loc.T("Schedule_ScheduledAt")} {executeAt:HH:mm}";
    }

    internal static bool TryBuildDelay(
        string hoursText,
        string minutesText,
        out TimeSpan delay,
        out ScheduleDelayError error)
    {
        delay = default;
        error = ScheduleDelayError.None;

        if (!int.TryParse(hoursText, out int hours) || hours < 0)
        {
            error = ScheduleDelayError.InvalidHours;
            return false;
        }

        if (hours > ScheduledPowerActionService.MaxDelay.TotalHours)
        {
            error = ScheduleDelayError.MaxDelay;
            return false;
        }

        if (!int.TryParse(minutesText, out int minutes) || minutes < 0 || minutes > 59)
        {
            error = ScheduleDelayError.InvalidMinutes;
            return false;
        }

        delay = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);

        if (delay < ScheduledPowerActionService.MinDelay)
        {
            error = ScheduleDelayError.MinDelay;
            return false;
        }

        if (delay > ScheduledPowerActionService.MaxDelay)
        {
            error = ScheduleDelayError.MaxDelay;
            return false;
        }

        return true;
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
        ConfirmButton.IsEnabled = false;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
