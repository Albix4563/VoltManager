using VoltManager.Services;

namespace VoltManager.Tests;

public class GamingModeReminderServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Inactive_NeverPrompts()
    {
        var service = new GamingModeReminderService(
            idleDurationBeforeReminder: TimeSpan.FromMinutes(10),
            repeatReminderInterval: TimeSpan.FromMinutes(20));

        Assert.Equal(GamingModeReminderDecision.None, service.ObserveCpu(1, T0.AddHours(1)));
    }

    [Fact]
    public void LowCpu_PromptsAfterIdleDuration()
    {
        var service = new GamingModeReminderService(
            idleDurationBeforeReminder: TimeSpan.FromMinutes(10),
            repeatReminderInterval: TimeSpan.FromMinutes(20));
        service.Start(T0);

        Assert.Equal(GamingModeReminderDecision.None, service.ObserveCpu(5, T0));
        Assert.Equal(GamingModeReminderDecision.None, service.ObserveCpu(5, T0.AddMinutes(9).AddSeconds(59)));
        Assert.Equal(GamingModeReminderDecision.Prompt, service.ObserveCpu(5, T0.AddMinutes(10)));
    }

    [Fact]
    public void Prompt_RepeatsOnlyAfterRepeatInterval()
    {
        var service = new GamingModeReminderService(
            idleDurationBeforeReminder: TimeSpan.FromMinutes(10),
            repeatReminderInterval: TimeSpan.FromMinutes(20));
        service.Start(T0);

        service.ObserveCpu(5, T0);
        Assert.Equal(GamingModeReminderDecision.Prompt, service.ObserveCpu(5, T0.AddMinutes(10)));
        Assert.Equal(GamingModeReminderDecision.None, service.ObserveCpu(5, T0.AddMinutes(29)));
        Assert.Equal(GamingModeReminderDecision.Prompt, service.ObserveCpu(5, T0.AddMinutes(30)));
    }

    [Fact]
    public void HighCpu_ResetsIdleWindow()
    {
        var service = new GamingModeReminderService(
            idleDurationBeforeReminder: TimeSpan.FromMinutes(10),
            repeatReminderInterval: TimeSpan.FromMinutes(20));
        service.Start(T0);

        service.ObserveCpu(5, T0);
        service.ObserveCpu(25, T0.AddMinutes(9));

        Assert.Equal(GamingModeReminderDecision.None, service.ObserveCpu(5, T0.AddMinutes(18)));
        Assert.Equal(GamingModeReminderDecision.None, service.ObserveCpu(5, T0.AddMinutes(27).AddSeconds(59)));
        Assert.Equal(GamingModeReminderDecision.Prompt, service.ObserveCpu(5, T0.AddMinutes(28)));
    }

    [Fact]
    public void Stop_DisablesPrompting()
    {
        var service = new GamingModeReminderService(
            idleDurationBeforeReminder: TimeSpan.FromMinutes(10),
            repeatReminderInterval: TimeSpan.FromMinutes(20));
        service.Start(T0);
        service.Stop();

        Assert.Equal(GamingModeReminderDecision.None, service.ObserveCpu(5, T0.AddHours(1)));
    }
}
