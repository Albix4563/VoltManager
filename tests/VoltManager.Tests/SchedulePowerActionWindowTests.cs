namespace VoltManager.Tests;

public sealed class SchedulePowerActionWindowTests
{
    [Fact]
    public void Delay_parser_reports_max_delay_for_huge_hours_without_throwing()
    {
        bool valid = SchedulePowerActionWindow.TryBuildDelay(
            "2147483647", "0", out TimeSpan delay, out ScheduleDelayError error);

        Assert.False(valid);
        Assert.Equal(default, delay);
        Assert.Equal(ScheduleDelayError.MaxDelay, error);
    }
}
