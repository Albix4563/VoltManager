using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class PlanHistoryServiceTests
{
    private static readonly PlanChangeContext ManualContext = new(
        PlanHistoryCategory.Manual,
        "manual",
        "manual_override",
        new Dictionary<string, string>());

    [Fact]
    public void SetActivePlan_RecordsSuccessButSkipsSameGuid()
    {
        var reads = new Queue<Guid?>([
            Guid.Parse(PowerPlanService.BalancedGuid),
            Guid.Parse(PowerPlanService.PerformanceGuid),
        ]);
        var commands = new List<string>();
        var service = CreateService(() => reads.Dequeue(), args => { commands.Add(args); return ""; });

        Assert.True(service.SetActivePlan(PlanId.Performance, ManualContext));

        var entry = Assert.Single(service.History.GetSnapshot().Entries);
        Assert.Equal(PlanHistoryOutcome.Applied, entry.Outcome);
        Assert.Equal(PlanId.Balanced, entry.PreviousPlan?.PlanId);
        Assert.Equal(PlanId.Performance, entry.RequestedPlan?.PlanId);
        Assert.Equal(PlanId.Performance, entry.ObservedPlan?.PlanId);
        Assert.Single(commands, $"/setactive {PowerPlanService.PerformanceGuid}");

        var sameCommands = new List<string>();
        var samePlan = CreateService(() => Guid.Parse(PowerPlanService.PerformanceGuid), args =>
        {
            sameCommands.Add(args);
            return "";
        });
        Assert.True(samePlan.SetActivePlan(PlanId.Performance, ManualContext));
        Assert.Empty(samePlan.History.GetSnapshot().Entries);
        Assert.DoesNotContain(sameCommands, command => command.StartsWith("/setactive ", StringComparison.Ordinal));
    }

    [Fact]
    public void SetActivePlan_RecordsFailedAndUnverifiableOutcomes()
    {
        var failedReads = new Queue<Guid?>([
            Guid.Parse(PowerPlanService.BalancedGuid),
            Guid.Parse(PowerPlanService.BalancedGuid),
        ]);
        var failed = CreateService(() => failedReads.Dequeue(), _ => "");
        Assert.False(failed.SetActivePlan(PlanId.Performance, ManualContext));
        Assert.Equal(PlanHistoryOutcome.Failed, Assert.Single(failed.History.GetSnapshot().Entries).Outcome);

        var unknownReads = new Queue<Guid?>([
            Guid.Parse(PowerPlanService.BalancedGuid),
            null,
            Guid.Parse(PowerPlanService.PerformanceGuid),
        ]);
        var unknown = CreateService(() => unknownReads.Dequeue(), _ => "");
        Assert.False(unknown.SetActivePlan(PlanId.Performance, ManualContext));
        var entry = Assert.Single(unknown.History.GetSnapshot().Entries);
        Assert.Equal(PlanHistoryOutcome.Unverifiable, entry.Outcome);
        Assert.Null(entry.ObservedPlan);
        Assert.Equal(PlanId.Performance, unknown.GetActivePlan()?.PlanId);
        Assert.Single(unknown.History.GetSnapshot().Entries);
    }

    [Fact]
    public void ExternalChangeThenRestore_IsOrderedAndNotDuplicated()
    {
        Guid? active = Guid.Parse(PowerPlanService.BalancedGuid);
        var service = CreateService(
            () => active,
            args =>
            {
                if (args.StartsWith("/setactive ", StringComparison.Ordinal))
                    active = Guid.Parse(args[11..]);
                return "";
            });

        Assert.Equal(PlanId.Balanced, service.GetActivePlan()?.PlanId);
        active = Guid.Parse(PowerPlanService.PerformanceGuid);
        Assert.Equal(PlanId.Performance, service.GetActivePlan()?.PlanId);

        var guardContext = new PlanChangeContext(
            PlanHistoryCategory.Automatic,
            "planGuard",
            "expected_plan_restored",
            new Dictionary<string, string>());
        Assert.True(service.SetActivePlan(PlanId.Balanced, guardContext));
        Assert.Equal(PlanId.Balanced, service.GetActivePlan()?.PlanId);

        var entries = service.History.GetSnapshot().Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal(PlanHistoryOutcome.Applied, entries[0].Outcome);
        Assert.Equal("planGuard", entries[0].Source);
        Assert.Equal(PlanHistoryOutcome.ExternalDetected, entries[1].Outcome);
    }

    [Fact]
    public void NullRead_DoesNotForgetLastObservedPlan()
    {
        Guid? active = Guid.Parse(PowerPlanService.BalancedGuid);
        var service = CreateService(() => active, _ => "");

        Assert.NotNull(service.GetActivePlan());
        active = null;
        Assert.Null(service.GetActivePlan());
        active = Guid.Parse(PowerPlanService.BalancedGuid);
        Assert.NotNull(service.GetActivePlan());

        Assert.Empty(service.History.GetSnapshot().Entries);
    }

    [Fact]
    public void ClearHistory_KeepsObservedPlanAndDoesNotInventExternalChange()
    {
        Guid? active = Guid.Parse(PowerPlanService.BalancedGuid);
        var service = CreateService(() => active, _ => "");

        Assert.NotNull(service.GetActivePlan());
        active = Guid.Parse(PowerPlanService.PerformanceGuid);
        Assert.NotNull(service.GetActivePlan());
        Assert.Single(service.History.GetSnapshot().Entries);

        service.History.Clear();
        Assert.NotNull(service.GetActivePlan());
        Assert.Empty(service.History.GetSnapshot().Entries);
    }

    [Fact]
    public void AdvancedParameterReapply_UsesSynchronizedPathWithoutSuccessNoise()
    {
        var reads = new Queue<Guid?>([
            Guid.Parse(PowerPlanService.BalancedGuid),
            Guid.Parse(PowerPlanService.BalancedGuid),
        ]);
        var commands = new List<string>();
        var service = CreateService(() => reads.Dequeue(), args => { commands.Add(args); return ""; });
        var context = new PlanChangeContext(
            PlanHistoryCategory.Manual,
            "advancedParameters",
            "parameters_reapply",
            new Dictionary<string, string> { ["setting"] = "processorMax" });

        Assert.True(service.ReapplyPlan(PowerPlanService.BalancedGuid, context));
        Assert.Contains($"/setactive {PowerPlanService.BalancedGuid}", commands);
        Assert.Empty(service.History.GetSnapshot().Entries);
    }

    [Fact]
    public async Task ConcurrentRequests_AreSerializedWithoutExternalAttribution()
    {
        Guid? active = Guid.Parse(PowerPlanService.BalancedGuid);
        var service = CreateService(
            () => active,
            args =>
            {
                if (args.StartsWith("/setactive ", StringComparison.Ordinal))
                    active = Guid.Parse(args[11..]);
                return "";
            });

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
            service.SetActivePlan(i % 2 == 0 ? PlanId.Performance : PlanId.Balanced, ManualContext))));

        Assert.DoesNotContain(service.History.GetSnapshot().Entries, entry => entry.Category == PlanHistoryCategory.External);
        Assert.DoesNotContain(service.History.GetSnapshot().Entries, entry => entry.Outcome != PlanHistoryOutcome.Applied);
    }

    [Fact]
    public void ProblemsGroupForThirtySeconds_AndDifferentCauseBreaksTheGroup()
    {
        var history = new PlanHistoryService();
        var start = new DateTime(2026, 9, 6, 8, 0, 0, DateTimeKind.Utc);
        var plan = new PlanHistoryPlan(PowerPlanService.PerformanceGuid, "", PlanId.Performance);

        history.Record(start, ManualContext, null, plan, null, PlanHistoryOutcome.Unverifiable);
        history.Record(start.AddSeconds(20), ManualContext, null, plan, null, PlanHistoryOutcome.Unverifiable);

        var grouped = Assert.Single(history.GetSnapshot().Entries);
        Assert.Equal(2, grouped.Attempts);
        Assert.Equal(start.AddSeconds(20), grouped.LastTimestampUtc);

        history.Record(start.AddSeconds(25), ManualContext with { ReasonCode = "different" }, null, plan, null, PlanHistoryOutcome.Unverifiable);
        Assert.Equal(2, history.GetSnapshot().Entries.Count);
    }

    [Fact]
    public void CapacityClearAndSnapshots_AreSessionLocalAndImmutable()
    {
        var history = new PlanHistoryService();
        var start = new DateTime(2026, 9, 6, 8, 0, 0, DateTimeKind.Utc);
        var details = new Dictionary<string, string> { ["appName"] = "Editor <beta>" };
        var context = new PlanChangeContext(PlanHistoryCategory.Automatic, "appProfile", "profile_applied", details);
        var plan = new PlanHistoryPlan(PowerPlanService.PerformanceGuid, "", PlanId.Performance);
        history.Record(start, context, null, plan, plan, PlanHistoryOutcome.Applied);
        details["appName"] = "Changed later";
        Assert.Equal("Editor <beta>", history.GetSnapshot().Entries[0].Details["appName"]);

        for (var i = 1; i <= PlanHistoryService.Capacity; i++)
            history.Record(start.AddSeconds(i), context with { ReasonCode = "entry_" + i }, null, plan, plan, PlanHistoryOutcome.Applied);

        Assert.Equal(PlanHistoryService.Capacity, history.GetSnapshot().Entries.Count);
        var revision = history.Clear();
        Assert.Empty(history.GetSnapshot().Entries);
        Assert.Equal(revision, history.GetSnapshot().Revision);
        Assert.Empty(new PlanHistoryService().GetSnapshot().Entries);
    }

    [Theory]
    [InlineData(PowerPlanService.PerformanceGuid)]
    [InlineData(null)]
    public void ReapplyPlan_NeverActivatesAnInactiveOrUnknownPlan(string? activeGuid)
    {
        var commands = new List<string>();
        var service = CreateService(() => activeGuid == null ? null : Guid.Parse(activeGuid), args =>
        {
            commands.Add(args);
            return "";
        });

        Assert.Equal(activeGuid != null, service.ReapplyPlan(PowerPlanService.BalancedGuid, ManualContext));
        Assert.Empty(commands);
        Assert.Empty(service.History.GetSnapshot().Entries);
    }

    [Fact]
    public void SetPlanParameter_ReportsUnverifiableReapplyEvenWhenIndexesWereWritten()
    {
        var reads = new Queue<Guid?>([Guid.Parse(PowerPlanService.BalancedGuid), null]);
        var service = CreateService(() => reads.Dequeue(), args => args == "/list"
            ? $"{PowerPlanService.BalancedGuid} (Balanced)"
            : "0x00000032 0x00000032");

        Assert.False(new PowerPlanParameterService(service).SetPlanParameter(
            PowerPlanService.BalancedGuid, "processorMax", 50, 50));
        Assert.Equal(PlanHistoryOutcome.Unverifiable, Assert.Single(service.History.GetSnapshot().Entries).Outcome);
    }

    [Fact]
    public void SuccessfulNoOp_EndsThePreviousProblemGroup()
    {
        var service = CreateService(() => Guid.Parse(PowerPlanService.BalancedGuid), _ => "");
        Assert.False(service.SetActivePlan(PlanId.Performance, ManualContext));
        Assert.True(service.SetActivePlan(PlanId.Balanced, ManualContext));
        Assert.False(service.SetActivePlan(PlanId.Performance, ManualContext));

        Assert.Equal(2, service.History.GetSnapshot().Entries.Count);
        Assert.All(service.History.GetSnapshot().Entries, entry => Assert.Equal(1, entry.Attempts));
    }

    [Fact]
    public void ChangingSensorSamples_DoNotSplitRetriesOfTheSameDecision()
    {
        var history = new PlanHistoryService();
        var start = new DateTime(2026, 9, 6, 8, 0, 0, DateTimeKind.Utc);
        var context = new PlanChangeContext(PlanHistoryCategory.Automatic, "thermal", "active_switch",
            new Dictionary<string, string> { ["peakTemp"] = "95", ["thresholdCelsius"] = "90" });
        history.Record(start, context, null, null, null, PlanHistoryOutcome.Failed);
        history.Record(start.AddSeconds(3), context with
        {
            Details = new Dictionary<string, string> { ["peakTemp"] = "96", ["thresholdCelsius"] = "90" }
        }, null, null, null, PlanHistoryOutcome.Failed);
        Assert.Equal(2, Assert.Single(history.GetSnapshot().Entries).Attempts);

        history.Record(start.AddSeconds(6), context with
        {
            Details = new Dictionary<string, string> { ["peakTemp"] = "96", ["thresholdCelsius"] = "85" }
        }, null, null, null, PlanHistoryOutcome.Failed);
        Assert.Equal(2, history.GetSnapshot().Entries.Count);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(-1)]
    public void ProblemGrouping_RejectsExpiredOrBackwardsTimestamps(int seconds)
    {
        var history = new PlanHistoryService();
        var start = new DateTime(2026, 9, 6, 8, 0, 0, DateTimeKind.Utc);
        history.Record(start, ManualContext, null, null, null, PlanHistoryOutcome.Failed);
        history.Record(start.AddSeconds(seconds), ManualContext, null, null, null, PlanHistoryOutcome.Failed);
        Assert.Equal(2, history.GetSnapshot().Entries.Count);
    }

    private static PowerPlanService CreateService(Func<Guid?> read, Func<string, string> run)
        => new(TestSettings.Create(), read, run, new PlanHistoryService(), new FixedClock());

    private sealed class FixedClock : ISystemClock
    {
        public DateTime UtcNow { get; } = new(2026, 9, 6, 8, 0, 0, DateTimeKind.Utc);
    }
}
