using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class PowerPlanGuardServiceTests
{
    [Fact]
    public void Expected_plan_does_not_require_reassertion()
    {
        DateTime now = Utc(12, 0);
        var guard = new PowerPlanGuardService(utcNow: () => now);
        guard.SetExpected(PlanId.Balanced, "automation", "rule-1");

        bool shouldReassert = guard.ShouldReassert(PlanId.Balanced, out PowerPlanConflictNotification? conflict);

        Assert.False(shouldReassert);
        Assert.Null(conflict);
    }

    [Fact]
    public void Conflict_notifications_are_rate_limited_by_injected_clock()
    {
        DateTime now = Utc(12, 0);
        var guard = new PowerPlanGuardService(TimeSpan.FromMinutes(2), () => now);
        guard.SetExpected(PlanId.Performance, "automation");

        Assert.True(guard.ShouldReassert(PlanId.Balanced, out PowerPlanConflictNotification? first));
        Assert.NotNull(first);
        Assert.True(first.ShouldNotifyUser);

        now = now.AddMinutes(1);
        Assert.True(guard.ShouldReassert(PlanId.Balanced, out PowerPlanConflictNotification? limited));
        Assert.NotNull(limited);
        Assert.False(limited.ShouldNotifyUser);

        now = now.AddMinutes(1);
        Assert.True(guard.ShouldReassert(PlanId.Balanced, out PowerPlanConflictNotification? allowed));
        Assert.NotNull(allowed);
        Assert.True(allowed.ShouldNotifyUser);
    }

    [Fact]
    public void Manual_override_expectation_expires_using_injected_clock()
    {
        DateTime now = Utc(12, 0);
        var guard = new PowerPlanGuardService(utcNow: () => now);
        var manualOverride = new ManualOverride
        {
            Plan = "powerSaver",
            ExpiresAtUtc = now.AddMinutes(5),
        };

        guard.RefreshManualOverride(manualOverride);
        Assert.Equal(PlanId.PowerSaver, guard.Expectation?.Plan);
        Assert.Equal("manualOverride", guard.Expectation?.Source);

        now = now.AddMinutes(5);
        guard.RefreshManualOverride(manualOverride);
        Assert.Null(guard.Expectation);
    }

    [Fact]
    public void Conflicting_active_plan_requests_reapply_with_expected_details()
    {
        DateTime now = Utc(12, 0);
        var guard = new PowerPlanGuardService(utcNow: () => now);
        guard.SetExpected(PlanId.Performance, "manualOverride", "performance");

        bool shouldReassert = guard.ShouldReassert(PlanId.PowerSaver, out PowerPlanConflictNotification? conflict);

        Assert.True(shouldReassert);
        Assert.NotNull(conflict);
        Assert.Equal(PlanId.Performance, conflict.ExpectedPlan);
        Assert.Equal(PlanId.PowerSaver, conflict.ActualPlan);
        Assert.Equal("manualOverride", conflict.Source);
        Assert.Equal("performance", conflict.Detail);
        Assert.Equal(now, conflict.DetectedAtUtc);
    }

    private static DateTime Utc(int hour, int minute)
        => new(2026, 9, 29, hour, minute, 0, DateTimeKind.Utc);
}
