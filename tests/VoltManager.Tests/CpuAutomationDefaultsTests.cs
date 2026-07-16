using Xunit;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class CpuAutomationDefaultsTests
{
    [Fact]
    public void DefaultRules_ReturnsExpectedValues()
    {
        var rules = AppSettings.DefaultRules();
        Assert.Equal(3, rules.Count);

        var saver = rules.Single(r => r.Id == "saver");
        Assert.Equal("lt", saver.Comparison);
        Assert.Equal(20, saver.ThresholdPct);
        Assert.Equal(2, saver.DurationMinutes);
        Assert.Equal(PlanId.PowerSaver, saver.TargetPlan);

        var balanced = rules.Single(r => r.Id == "balanced");
        Assert.Equal("gt", balanced.Comparison);
        Assert.Equal(30, balanced.ThresholdPct);
        Assert.Equal(2, balanced.DurationMinutes);
        Assert.Equal(PlanId.Balanced, balanced.TargetPlan);

        var perf = rules.Single(r => r.Id == "performance");
        Assert.Equal("gt", perf.Comparison);
        Assert.Equal(70, perf.ThresholdPct);
        Assert.Equal(2, perf.DurationMinutes);
        Assert.Equal(PlanId.Performance, perf.TargetPlan);
    }

    [Fact]
    public void PickRule_BehavesAsExpected()
    {
        var rules = AppSettings.DefaultRules();

        // 25% CPU: should not select any rule (saver is < 20%, balanced is > 30%)
        var ruleAt25 = AutomationEngine.PickRule(25, rules);
        Assert.Null(ruleAt25);

        // 50% CPU: should select balanced (> 30%)
        var ruleAt50 = AutomationEngine.PickRule(50, rules);
        Assert.NotNull(ruleAt50);
        Assert.Equal("balanced", ruleAt50.Id);

        // 75% CPU: should select performance (> 70%)
        var ruleAt75 = AutomationEngine.PickRule(75, rules);
        Assert.NotNull(ruleAt75);
        Assert.Equal("performance", ruleAt75.Id);
    }

    [Fact]
    public void Evaluate_RequiresTwoMinutesContinuous()
    {
        var engine = new AutomationEngine();
        var settings = new AppSettings
        {
            MasterAutomationEnabled = true,
            Rules = AppSettings.DefaultRules()
        };
        var activePlan = PlanId.Balanced;
        var now = DateTime.UtcNow;

        // CPU is 75%, so performance rule (>70% for 2 mins) is candidate
        // Initial evaluation (0 min): returns null, rule becomes candidate
        var result1 = engine.Evaluate(75, now, activePlan, settings);
        Assert.Null(result1);
        Assert.Equal("performance", engine.CandidateRuleId);

        // After 1 minute: returns null (not yet 2 minutes)
        var result2 = engine.Evaluate(75, now.AddMinutes(1), activePlan, settings);
        Assert.Null(result2);

        // After 2 minutes: returns TargetPlan (Performance)
        var result3 = engine.Evaluate(75, now.AddMinutes(2), activePlan, settings);
        Assert.Equal(PlanId.Performance, result3);
    }

    [Fact]
    public void SettingsService_MigratesOldDefaults()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var oldSettingsJson = @"
            {
              ""rules"": [
                { ""id"": ""saver"", ""enabled"": true, ""comparison"": ""lt"", ""thresholdPct"": 10, ""durationMinutes"": 1, ""targetPlan"": ""PowerSaver"" },
                { ""id"": ""balanced"", ""enabled"": true, ""comparison"": ""gt"", ""thresholdPct"": 10, ""durationMinutes"": 1, ""targetPlan"": ""Balanced"" },
                { ""id"": ""performance"", ""enabled"": true, ""comparison"": ""gt"", ""thresholdPct"": 50, ""durationMinutes"": 1, ""targetPlan"": ""Performance"" }
              ]
            }";
            File.WriteAllText(tempFile, oldSettingsJson);

            var settingsService = new SettingsService(tempFile);
            var rules = settingsService.Current.Rules;

            // Verify they were migrated to new defaults (20%, 30%, 70% and 2 mins)
            var saver = rules.Single(r => r.Id == "saver");
            Assert.Equal(20, saver.ThresholdPct);
            Assert.Equal(2, saver.DurationMinutes);

            var balanced = rules.Single(r => r.Id == "balanced");
            Assert.Equal(30, balanced.ThresholdPct);
            Assert.Equal(2, balanced.DurationMinutes);

            var perf = rules.Single(r => r.Id == "performance");
            Assert.Equal(70, perf.ThresholdPct);
            Assert.Equal(2, perf.DurationMinutes);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void SettingsService_DoesNotMigrateCustomSettings()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Custom saver rule (threshold is 15% instead of 10%)
            var customSettingsJson = @"
            {
              ""rules"": [
                { ""id"": ""saver"", ""enabled"": true, ""comparison"": ""lt"", ""thresholdPct"": 15, ""durationMinutes"": 1, ""targetPlan"": ""PowerSaver"" },
                { ""id"": ""balanced"", ""enabled"": true, ""comparison"": ""gt"", ""thresholdPct"": 10, ""durationMinutes"": 1, ""targetPlan"": ""Balanced"" },
                { ""id"": ""performance"", ""enabled"": true, ""comparison"": ""gt"", ""thresholdPct"": 50, ""durationMinutes"": 1, ""targetPlan"": ""Performance"" }
              ]
            }";
            File.WriteAllText(tempFile, customSettingsJson);

            var settingsService = new SettingsService(tempFile);
            var rules = settingsService.Current.Rules;

            // Verify they were NOT migrated
            var saver = rules.Single(r => r.Id == "saver");
            Assert.Equal(15, saver.ThresholdPct);
            Assert.Equal(1, saver.DurationMinutes);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
