using System.Diagnostics;
using System.Text.RegularExpressions;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Wraps powercfg. Parses output by GUID only — names are localized (Italian Windows).
/// </summary>
public class PowerPlanService
{
    public const string SaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string PerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static readonly Regex GuidRegex = new(
        @"(?<guid>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*(?:\((?<name>[^)]*)\))?",
        RegexOptions.Compiled);

    private readonly SettingsService _settings;

    public PowerPlanService(SettingsService settings)
    {
        _settings = settings;
    }

    public static string RunPowercfg(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p == null)
            {
                Logger.Warn($"powercfg {args}: process did not start");
                return "";
            }

            // Drain BOTH pipes concurrently: reading only stdout while powercfg
            // fills the stderr buffer would deadlock both processes forever.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(10000))
            {
                // Hung powercfg: kill it so we don't leak a zombie or block the
                // caller's thread indefinitely. Callers treat "" as "no data".
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                Logger.Warn($"powercfg {args}: timed out after 10s, killed");
                return "";
            }

            p.WaitForExit(); // flush the redirected async readers
            string output = stdout.GetAwaiter().GetResult();
            string err = stderr.GetAwaiter().GetResult();
            if (p.ExitCode != 0 && err.Trim().Length > 0)
                Logger.Warn($"powercfg {args}: exit {p.ExitCode}: {err.Trim()}");
            return output;
        }
        catch (Exception ex)
        {
            // powercfg missing, blocked by policy, or any I/O failure: degrade
            // gracefully instead of throwing — every caller treats "" as no data.
            Logger.Error($"powercfg {args} failed", ex);
            return "";
        }
        finally
        {
            p?.Dispose();
        }
    }

    public static List<PowerPlan> ParseListOutput(string output, Dictionary<string, string>? guidMap = null)
    {
        var plans = new List<PowerPlan>();
        foreach (var line in output.Split('\n'))
        {
            var m = GuidRegex.Match(line);
            if (!m.Success) continue;
            string guid = m.Groups["guid"].Value.ToLowerInvariant();
            plans.Add(new PowerPlan
            {
                Guid = guid,
                Name = m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : "",
                IsActive = line.Contains('*'),
                PlanId = ResolvePlanId(guid, guidMap),
            });
        }
        return plans;
    }

    public static PlanId? ResolvePlanId(string guid, Dictionary<string, string>? guidMap = null)
    {
        guid = guid.ToLowerInvariant();
        if (guid == SaverGuid) return PlanId.PowerSaver;
        if (guid == BalancedGuid) return PlanId.Balanced;
        if (guid == PerformanceGuid) return PlanId.Performance;
        if (guidMap != null)
        {
            foreach (var kv in guidMap)
                if (kv.Value.Equals(guid, StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse<PlanId>(kv.Key, out var pid))
                    return pid;
        }
        return null;
    }

    public List<PowerPlan> ListPlans()
        => ParseListOutput(RunPowercfg("/list"), _settings.Current.PlanGuidMap);

    public PowerPlan? GetActivePlan()
    {
        var output = RunPowercfg("/getactivescheme");
        var m = GuidRegex.Match(output);
        if (!m.Success) return null;
        string guid = m.Groups["guid"].Value.ToLowerInvariant();
        return new PowerPlan
        {
            Guid = guid,
            Name = m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : "",
            IsActive = true,
            PlanId = ResolvePlanId(guid, _settings.Current.PlanGuidMap),
        };
    }

    /// <summary>Checks all three canonical plans exist (directly or via guid map).</summary>
    public (bool allPresent, List<PlanId> missing) CheckDefaultPlans()
    {
        var plans = ListPlans();
        var present = plans.Where(p => p.PlanId != null).Select(p => p.PlanId!.Value).ToHashSet();
        var missing = new List<PlanId>();
        foreach (PlanId pid in Enum.GetValues<PlanId>())
            if (!present.Contains(pid)) missing.Add(pid);
        return (missing.Count == 0, missing);
    }

    /// <summary>
    /// Restores missing default plans via powercfg -duplicatescheme. Duplicate gets a NEW guid,
    /// which we persist in settings so the switcher targets the right plan.
    /// </summary>
    public bool RestoreDefaultPlans()
    {
        var (_, missing) = CheckDefaultPlans();
        bool ok = true;
        foreach (var pid in missing)
        {
            string canonical = GuidFor(pid);
            var output = RunPowercfg($"-duplicatescheme {canonical}");
            var m = GuidRegex.Match(output);
            if (m.Success)
            {
                _settings.Current.PlanGuidMap[pid.ToString()] = m.Groups["guid"].Value.ToLowerInvariant();
            }
            else
            {
                ok = false;
            }
        }
        if (ok) _settings.Save();
        return ok;
    }

    public bool SetActivePlan(PlanId plan)
    {
        string guid = TargetGuid(plan);
        RunPowercfg($"/setactive {guid}");
        var active = GetActivePlan();
        return active != null && active.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Actual GUID on this machine: mapped duplicate if present, else canonical.</summary>
    public string TargetGuid(PlanId plan)
    {
        if (_settings.Current.PlanGuidMap.TryGetValue(plan.ToString(), out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            // Verify mapped guid still exists; fall back to canonical otherwise.
            var existing = ParseListOutput(RunPowercfg("/list"));
            if (existing.Any(p => p.Guid.Equals(mapped, StringComparison.OrdinalIgnoreCase)))
                return mapped;
        }
        return GuidFor(plan);
    }

    public static string GuidFor(PlanId plan) => plan switch
    {
        PlanId.PowerSaver => SaverGuid,
        PlanId.Balanced => BalancedGuid,
        PlanId.Performance => PerformanceGuid,
        _ => BalancedGuid,
    };
}
