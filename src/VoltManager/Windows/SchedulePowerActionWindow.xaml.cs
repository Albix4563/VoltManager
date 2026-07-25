using System.Windows;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager;

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

        if (!int.TryParse(HoursTextBox.Text, out int hours) || hours < 0)
        {
            ShowError(_loc.T("Schedule_InvalidHours"));
            return;
        }

        if (!int.TryParse(MinutesTextBox.Text, out int minutes) || minutes < 0 || minutes > 59)
        {
            ShowError(_loc.T("Schedule_InvalidMinutes"));
            return;
        }

        var delay = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);

        if (delay < ScheduledPowerActionService.MinDelay)
        {
            ShowError(_loc.T("Schedule_MinDelay", $"{(int)ScheduledPowerActionService.MinDelay.TotalMinutes}"));
            return;
        }

        if (delay > ScheduledPowerActionService.MaxDelay)
        {
            ShowError(_loc.T("Schedule_MaxDelay", $"{(int)ScheduledPowerActionService.MaxDelay.TotalDays}"));
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
