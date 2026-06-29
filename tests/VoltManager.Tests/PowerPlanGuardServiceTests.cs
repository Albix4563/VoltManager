using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class PowerPlanGuardServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldReassert_WhenExpectedPlanDiffersFromActivePlan()
    {
        var guard = new PowerPlanGuardService(TimeSpan.FromMinutes(2));
        guard.SetExpected(PlanId.Performance, "heavyApp", "Game.exe");

        Assert.True(guard.ShouldReassert(PlanId.Balanced, T0, out var conflict));
        Assert.NotNull(conflict);
        Assert.Equal(PlanId.Performance, conflict.ExpectedPlan);
        Assert.Equal(PlanId.Balanced, conflict.ActualPlan);
        Assert.Equal("heavyApp", conflict.Source);
    }

    [Fact]
    public void ShouldReassert_ThrottlesRepeatedConflictNotifications()
    {
        var guard = new PowerPlanGuardService(TimeSpan.FromMinutes(2));
        guard.SetExpected(PlanId.Performance, "manualOverride", "Prestazioni");

        Assert.True(guard.ShouldReassert(PlanId.PowerSaver, T0, out var first));
        Assert.True(first!.ShouldNotifyUser);

        Assert.True(guard.ShouldReassert(PlanId.PowerSaver, T0.AddSeconds(30), out var second));
        Assert.False(second!.ShouldNotifyUser);
    }

    [Fact]
    public void FindLikelyInterferingProcesses_FindsKnownAndProbablePowerManagers()
    {
        var processes = new[]
        {
            new PowerPlanProcessSnapshot(101, "ProcessLasso", @"C:\Program Files\Process Lasso\ProcessLasso.exe"),
            new PowerPlanProcessSnapshot(102, "BatteryOptimizer", @"C:\Tools\BatteryOptimizer.exe"),
            new PowerPlanProcessSnapshot(103, "notepad", @"C:\Windows\notepad.exe"),
        };

        var suspects = PowerPlanGuardService.FindLikelyInterferingProcesses(processes);

        Assert.Contains(suspects, s => s.ProcessId == 101 && s.Confidence == PowerPlanInterferenceConfidence.Known);
        Assert.Contains(suspects, s => s.ProcessId == 102 && s.Confidence == PowerPlanInterferenceConfidence.Probable);
        Assert.DoesNotContain(suspects, s => s.ProcessId == 103);
    }

    [Fact]
    public void RefreshManualOverride_RehydratesPersistedActiveOverride()
    {
        var guard = new PowerPlanGuardService(TimeSpan.FromMinutes(2));
        var manual = new ManualOverride
        {
            Plan = "performance",
            ExpiresAtUtc = T0.AddHours(1),
        };

        guard.RefreshManualOverride(manual, T0);

        Assert.True(guard.ShouldReassert(PlanId.Balanced, T0, out var conflict));
        Assert.NotNull(conflict);
        Assert.Equal(PlanId.Performance, conflict.ExpectedPlan);
        Assert.Equal("manualOverride", conflict.Source);
    }
}
