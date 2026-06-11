using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class AutomationEngineTests
{
    private static AppSettings Settings(bool master = true) => new()
    {
        MasterAutomationEnabled = master,
        Rules = AppSettings.DefaultRules(),
    };

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HighCpu_FiresPerformance_AfterDuration()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        // First tick establishes candidate, no fire.
        Assert.Null(engine.Evaluate(80, T0, PlanId.Balanced, s));
        // Before duration elapses: no fire.
        Assert.Null(engine.Evaluate(80, T0.AddSeconds(30), PlanId.Balanced, s));
        // After 1 minute: fires Performance.
        Assert.Equal(PlanId.Performance, engine.Evaluate(80, T0.AddSeconds(61), PlanId.Balanced, s));
    }

    [Fact]
    public void LowCpu_FiresPowerSaver_AfterDuration()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        Assert.Null(engine.Evaluate(5, T0, PlanId.Balanced, s));
        Assert.Equal(PlanId.PowerSaver, engine.Evaluate(5, T0.AddSeconds(61), PlanId.Balanced, s));
    }

    [Fact]
    public void MidCpu_FiresBalanced_AfterDuration()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        Assert.Null(engine.Evaluate(30, T0, PlanId.PowerSaver, s));
        Assert.Equal(PlanId.Balanced, engine.Evaluate(30, T0.AddSeconds(61), PlanId.PowerSaver, s));
    }

    [Fact]
    public void OverlappingGtRules_HighestThresholdWins()
    {
        // CPU 80 matches both >10 (Balanced) and >50 (Performance): Performance wins.
        var rule = AutomationEngine.PickRule(80, AppSettings.DefaultRules());
        Assert.NotNull(rule);
        Assert.Equal(PlanId.Performance, rule!.TargetPlan);
    }

    [Fact]
    public void CandidateChange_ResetsHoldTimer()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        engine.Evaluate(80, T0, PlanId.Balanced, s);                      // candidate: performance
        engine.Evaluate(5, T0.AddSeconds(50), PlanId.Balanced, s);        // candidate switches: saver, timer reset
        // 61s after T0 but only 11s after switch: no fire.
        Assert.Null(engine.Evaluate(5, T0.AddSeconds(61), PlanId.Balanced, s));
        // 61s after the switch: fires saver.
        Assert.Equal(PlanId.PowerSaver, engine.Evaluate(5, T0.AddSeconds(50 + 61), PlanId.Balanced, s));
    }

    [Fact]
    public void TargetAlreadyActive_NoFire()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        engine.Evaluate(80, T0, PlanId.Performance, s);
        Assert.Null(engine.Evaluate(80, T0.AddSeconds(61), PlanId.Performance, s));
    }

    [Fact]
    public void Cooldown_SuppressesRapidSecondSwitch()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        // Make durations tiny so only cooldown is the gate.
        foreach (var r in s.Rules) r.DurationMinutes = 0.01; // 0.6s

        engine.Evaluate(80, T0, PlanId.Balanced, s);
        Assert.Equal(PlanId.Performance, engine.Evaluate(80, T0.AddSeconds(2), PlanId.Balanced, s));

        // Now drop CPU: hold satisfied at +5s but cooldown (15s) not elapsed.
        engine.Evaluate(5, T0.AddSeconds(3), PlanId.Performance, s);
        Assert.Null(engine.Evaluate(5, T0.AddSeconds(5), PlanId.Performance, s));
        // After cooldown: fires.
        Assert.Equal(PlanId.PowerSaver, engine.Evaluate(5, T0.AddSeconds(20), PlanId.Performance, s));
    }

    [Fact]
    public void DisabledRule_Skipped()
    {
        var s = Settings();
        s.Rules.First(r => r.Id == "performance").Enabled = false;
        // CPU 80 now falls through to >10 Balanced.
        var rule = AutomationEngine.PickRule(80, s.Rules);
        Assert.Equal(PlanId.Balanced, rule!.TargetPlan);
    }

    [Fact]
    public void MasterOff_NeverFires()
    {
        var engine = new AutomationEngine();
        var s = Settings(master: false);
        engine.Evaluate(80, T0, PlanId.Balanced, s);
        Assert.Null(engine.Evaluate(80, T0.AddMinutes(5), PlanId.Balanced, s));
    }

    [Fact]
    public void ActiveManualOverride_NeverFires()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        s.Override = new ManualOverride
        {
            Plan = "balanced",
            ExpiresAtUtc = T0.AddHours(1),
        };

        engine.Evaluate(80, T0, PlanId.Balanced, s);
        Assert.Null(engine.Evaluate(80, T0.AddMinutes(5), PlanId.Balanced, s));
    }

    [Fact]
    public void ExpiredManualOverride_DoesNotBlockAutomation()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        s.Override = new ManualOverride
        {
            Plan = "balanced",
            ExpiresAtUtc = T0.AddSeconds(-1),
        };

        engine.Evaluate(80, T0, PlanId.Balanced, s);
        Assert.Equal(PlanId.Performance, engine.Evaluate(80, T0.AddSeconds(61), PlanId.Balanced, s));
    }

    [Fact]
    public void NoRuleMatches_NoCandidate()
    {
        // CPU exactly 10: not <10, not >10.
        var rule = AutomationEngine.PickRule(10, AppSettings.DefaultRules());
        Assert.Null(rule);
    }

    [Fact]
    public void AddSample_MovingAverageOfLastFive()
    {
        var engine = new AutomationEngine();
        engine.AddSample(0);
        engine.AddSample(0);
        engine.AddSample(0);
        engine.AddSample(0);
        engine.AddSample(0);
        // Sixth sample evicts one zero: avg of (0,0,0,0,100) = 20.
        Assert.Equal(20, engine.AddSample(100), 3);
    }

    [Fact]
    public void CustomThresholds_Respected()
    {
        var engine = new AutomationEngine();
        var s = Settings();
        s.Rules.First(r => r.Id == "performance").ThresholdPct = 70;
        // CPU 60 with performance threshold raised to 70: Balanced wins.
        engine.Evaluate(60, T0, PlanId.PowerSaver, s);
        Assert.Equal(PlanId.Balanced, engine.Evaluate(60, T0.AddSeconds(61), PlanId.PowerSaver, s));
    }
}
