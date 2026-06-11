using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Pure rule state machine, unit-testable. Feed Evaluate() once per tick (~1s) with the
/// smoothed CPU average; returns the plan to switch to, or null.
///
/// Semantics:
///  - Candidate rule = highest-priority enabled rule matching current CPU
///    (gt rules by descending threshold first, then lt rules).
///  - Candidate must hold continuously for its duration before firing.
///  - Candidate change resets the hold timer.
///  - After firing, a global cooldown suppresses further switches (anti-flap).
///  - Master toggle off → engine inert, state cleared.
/// </summary>
public class AutomationEngine
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(15);

    private readonly Queue<double> _samples = new();
    private const int SmoothingWindow = 5;

    private string? _candidateRuleId;
    private DateTime _candidateSince;
    private DateTime _lastFired = DateTime.MinValue;

    /// <summary>Adds a raw CPU sample, returns smoothed moving average.</summary>
    public double AddSample(double cpu)
    {
        _samples.Enqueue(cpu);
        while (_samples.Count > SmoothingWindow) _samples.Dequeue();
        return _samples.Average();
    }

    public PlanId? Evaluate(double cpuAvg, DateTime now, PlanId? activePlan, AppSettings settings)
    {
        if (!settings.MasterAutomationEnabled)
        {
            Reset();
            return null;
        }

        var rule = PickRule(cpuAvg, settings.Rules);
        if (rule == null)
        {
            _candidateRuleId = null;
            return null;
        }

        if (_candidateRuleId != rule.Id)
        {
            _candidateRuleId = rule.Id;
            _candidateSince = now;
            return null;
        }

        if (now - _candidateSince < TimeSpan.FromMinutes(rule.DurationMinutes)) return null;
        if (rule.TargetPlan == activePlan) return null;
        if (now - _lastFired < Cooldown) return null;

        _lastFired = now;
        _candidateSince = now; // require a fresh hold before any further switch
        return rule.TargetPlan;
    }

    public static AutomationRule? PickRule(double cpuAvg, List<AutomationRule> rules)
    {
        // gt rules: highest threshold wins (>50 beats >10). lt rules only if no gt matches.
        var gtMatch = rules
            .Where(r => r.Enabled && r.Comparison == "gt" && cpuAvg > r.ThresholdPct)
            .OrderByDescending(r => r.ThresholdPct)
            .FirstOrDefault();
        if (gtMatch != null) return gtMatch;

        return rules
            .Where(r => r.Enabled && r.Comparison == "lt" && cpuAvg < r.ThresholdPct)
            .OrderBy(r => r.ThresholdPct)
            .FirstOrDefault();
    }

    public void Reset()
    {
        _candidateRuleId = null;
        _samples.Clear();
    }
}
