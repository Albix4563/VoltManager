using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private readonly Func<Guid?> _readActiveScheme;
    private readonly Func<string, string> _runPowercfg;
    private readonly ISystemClock _clock;
    private readonly object _sync = new();
    private PowerPlan? _lastObserved;
    private string? _pendingUnverifiedGuid;
    private bool _activeSchemeReadFaulted;

    public PlanHistoryService History { get; }

    public PowerPlanService(SettingsService settings)
        : this(settings, ReadActiveScheme, RunPowercfg, new PlanHistoryService(), new SystemClock())
    {
    }

    internal PowerPlanService(
        SettingsService settings,
        Func<Guid?> readActiveScheme,
        Func<string, string> runPowercfg)
        : this(settings, readActiveScheme, runPowercfg, new PlanHistoryService(), new SystemClock())
    {
    }

    internal PowerPlanService(
        SettingsService settings,
        Func<Guid?> readActiveScheme,
        Func<string, string> runPowercfg,
        PlanHistoryService history,
        ISystemClock clock)
    {
        _settings = settings;
        _readActiveScheme = readActiveScheme;
        _runPowercfg = runPowercfg;
        History = history;
        _clock = clock;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static Guid? ReadActiveScheme()
    {
        IntPtr pointer = IntPtr.Zero;
        uint error = PowerGetActiveScheme(IntPtr.Zero, out pointer);
        try
        {
            if (error != 0)
                throw new Win32Exception(unchecked((int)error));
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            if (pointer != IntPtr.Zero) LocalFree(pointer);
        }
    }

    public static string RunPowercfg(string args)
    {
        if (ValidationEnvironment.SuppressPowerChanges && IsMutatingPowercfg(args))
        {
            Logger.Info("Validation run suppressed powercfg mutation: " + args);
            return "";
        }
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

    internal static bool IsMutatingPowercfg(string args)
    {
        string normalized = (args ?? "").TrimStart();
        return normalized.StartsWith("/setactive", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("-setactive", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/change", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("-change", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/setacvalueindex", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("-setacvalueindex", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/setdcvalueindex", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("-setdcvalueindex", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/duplicatescheme", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("-duplicatescheme", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/delete", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("-delete", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/changename", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("-changename", StringComparison.OrdinalIgnoreCase);
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
    {
        lock (_sync)
            return ParseListOutput(_runPowercfg("/list"), _settings.Current.PlanGuidMap);
    }

    public PowerPlan? GetActivePlan()
    {
        var revisions = new List<long>(1);
        PowerPlan? current;
        lock (_sync)
            current = ObserveActivePlanLocked(revisions);
        PublishHistoryChanges(revisions);
        return current;
    }

    private PowerPlan? ReadActivePlanLocked()
    {
        try
        {
            string? guid = _readActiveScheme()?.ToString("D").ToLowerInvariant();
            if (guid == null) return null;
            _activeSchemeReadFaulted = false;
            PlanId? planId = ResolvePlanId(guid, _settings.Current.PlanGuidMap);
            string name = "";
            if (planId == null)
            {
                var match = GuidRegex.Match(_runPowercfg("/getactivescheme"));
                if (match.Success && match.Groups["guid"].Value.Equals(guid, StringComparison.OrdinalIgnoreCase))
                    name = match.Groups["name"].Success ? match.Groups["name"].Value.Trim() : "";
            }
            return new PowerPlan
            {
                Guid = guid,
                Name = name,
                IsActive = true,
                PlanId = planId,
            };
        }
        catch (Exception ex)
        {
            _activeSchemeReadFaulted = Logger.WarnOnce(
                _activeSchemeReadFaulted,
                "Native active power plan query failed",
                ex);
            return null;
        }
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

    public ExtraPlansReport FindExtraPlans()
    {
        lock (_sync)
            return FindExtraPlansLocked();
    }

    private ExtraPlansReport FindExtraPlansLocked()
    {
        AppSettings settings = _settings.Current;
        List<PowerPlan> plans = ParseListOutput(_runPowercfg("/list"), settings.PlanGuidMap);
        var installed = plans.ToDictionary(plan => plan.Guid, StringComparer.OrdinalIgnoreCase);
        var keep = new List<KeptPowerPlan>();
        var keepGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PlanId planId in Enum.GetValues<PlanId>())
        {
            PowerPlan? selected = null;
            string canonical = GuidFor(planId);
            if (installed.TryGetValue(canonical, out PowerPlan? canonicalPlan))
            {
                selected = canonicalPlan;
            }
            else if (settings.PlanGuidMap.TryGetValue(planId.ToString(), out string? mappedGuid)
                     && !string.IsNullOrWhiteSpace(mappedGuid)
                     && installed.TryGetValue(mappedGuid, out PowerPlan? mappedPlan))
            {
                selected = mappedPlan;
            }

            if (selected == null) continue;
            keepGuids.Add(selected.Guid);
            keep.Add(new KeptPowerPlan
            {
                PlanId = planId.ToString(),
                Guid = selected.Guid,
                Name = selected.Name,
            });
        }

        var mappedPlanIds = new Dictionary<string, PlanId>(StringComparer.OrdinalIgnoreCase);
        foreach (var (planText, guid) in settings.PlanGuidMap)
        {
            if (!string.IsNullOrWhiteSpace(guid) && Enum.TryParse(planText, true, out PlanId planId))
                mappedPlanIds[guid] = planId;
        }

        // Only repeated copies of the three main plans count as extras: OEM and
        // user-made plans are never listed, so they can never be deleted either.
        List<PowerPlan> extraPlans = plans.Where(plan => !keepGuids.Contains(plan.Guid)).ToList();
        var extras = new List<ExtraPowerPlan>(extraPlans.Count);
        foreach (PowerPlan plan in extraPlans)
        {
            PlanId? duplicateOf = null;
            if (mappedPlanIds.TryGetValue(plan.Guid, out PlanId mappedPlanId))
            {
                duplicateOf = mappedPlanId;
            }
            else if (!string.IsNullOrWhiteSpace(plan.Name))
            {
                KeptPowerPlan? sameName = keep.FirstOrDefault(kept =>
                    !string.IsNullOrWhiteSpace(kept.Name)
                    && kept.Name.Trim().Equals(plan.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (sameName != null && Enum.TryParse(sameName.PlanId, out PlanId keptPlanId))
                    duplicateOf = keptPlanId;
            }

            if (duplicateOf == null) continue;
            extras.Add(new ExtraPowerPlan
            {
                Guid = plan.Guid,
                Name = plan.Name,
                IsActive = plan.IsActive,
                IsDuplicate = true,
                DuplicateOf = duplicateOf.ToString(),
            });
        }

        var dismissed = settings.DismissedExtraPlanGuids
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasExtras = extras.Count > 0;
        return new ExtraPlansReport
        {
            HasExtras = hasExtras,
            ShouldPrompt = hasExtras && extras.Any(extra => !dismissed.Contains(extra.Guid)),
            Keep = keep,
            Extras = extras,
        };
    }

    public DeleteExtraPlansResult DeleteExtraPlans(IReadOnlyCollection<string> guids)
    {
        ArgumentNullException.ThrowIfNull(guids);

        var historyRevisions = new List<long>(2);
        DeleteExtraPlansResult result;
        lock (_sync)
        {
            List<string> requested = guids
                .Select(guid => (guid ?? "").Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ExtraPlansReport report = FindExtraPlansLocked();
            var extras = report.Extras.ToDictionary(extra => extra.Guid, StringComparer.OrdinalIgnoreCase);
            var canonicalGuids = new HashSet<string>(
                [SaverGuid, BalancedGuid, PerformanceGuid],
                StringComparer.OrdinalIgnoreCase);
            var eligible = requested
                .Where(guid => extras.ContainsKey(guid) && !canonicalGuids.Contains(guid))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failed = requested
                .Where(guid => !eligible.Contains(guid))
                .ToList();

            string? activeGuid = TryReadActiveGuid();
            if (activeGuid != null && eligible.Contains(activeGuid))
            {
                // Prefer the main plan the active duplicate was copied from, then Balanced.
                string? duplicateOf = extras[activeGuid].DuplicateOf;
                KeptPowerPlan? fallback = report.Keep.FirstOrDefault(kept =>
                    duplicateOf != null && kept.PlanId.Equals(duplicateOf, StringComparison.Ordinal))
                    ?? report.Keep.FirstOrDefault(kept =>
                    kept.PlanId.Equals(PlanId.Balanced.ToString(), StringComparison.Ordinal))
                    ?? report.Keep.FirstOrDefault();

                bool switched = false;
                if (fallback != null)
                {
                    PlanId? fallbackPlanId = Enum.TryParse(fallback.PlanId, out PlanId parsed) ? parsed : null;
                    switched = ApplyPlanLocked(
                        fallback.Guid,
                        fallbackPlanId,
                        new PlanChangeContext(
                            PlanHistoryCategory.Manual,
                            "manual",
                            "extra_plan_cleanup",
                            new Dictionary<string, string>()),
                        reapplyOnly: false,
                        historyRevisions);
                }

                if (!switched)
                {
                    eligible.Remove(activeGuid);
                    if (!failed.Contains(activeGuid, StringComparer.OrdinalIgnoreCase))
                        failed.Add(activeGuid);
                }
            }

            foreach (string guid in eligible)
                _runPowercfg($"-delete {guid}");

            var installedAfterDelete = ParseListOutput(_runPowercfg("/list"))
                .Select(plan => plan.Guid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deleted = new List<string>();
            foreach (string guid in eligible)
            {
                if (installedAfterDelete.Contains(guid))
                {
                    if (!failed.Contains(guid, StringComparer.OrdinalIgnoreCase))
                        failed.Add(guid);
                }
                else
                {
                    deleted.Add(guid);
                }
            }

            if (deleted.Count > 0)
            {
                var deletedSet = deleted.ToHashSet(StringComparer.OrdinalIgnoreCase);
                _settings.Update(state =>
                {
                    foreach (string key in state.PlanGuidMap
                                 .Where(entry => deletedSet.Contains(entry.Value))
                                 .Select(entry => entry.Key)
                                 .ToList())
                    {
                        state.PlanGuidMap.Remove(key);
                    }
                });
            }

            result = new DeleteExtraPlansResult
            {
                Success = failed.Count == 0,
                Deleted = deleted,
                Failed = failed,
            };
        }

        PublishHistoryChanges(historyRevisions);
        return result;
    }

    public void DismissExtraPlans(IEnumerable<string> guids)
    {
        ArgumentNullException.ThrowIfNull(guids);
        List<string> normalized = guids
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0) return;

        _settings.Update(state =>
        {
            state.DismissedExtraPlanGuids = state.DismissedExtraPlanGuids
                .Concat(normalized)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(guid => guid.ToLowerInvariant())
                .ToList();
        });
    }

    private string? TryReadActiveGuid()
    {
        try
        {
            return _readActiveScheme()?.ToString("D").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read active power plan during extra-plan cleanup: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Restores missing default plans via powercfg -duplicatescheme. Duplicate gets a NEW guid,
    /// which we persist in settings so the switcher targets the right plan.
    /// </summary>
    public bool RestoreDefaultPlans()
    {
        lock (_sync)
        {
            var plans = ParseListOutput(_runPowercfg("/list"), _settings.Current.PlanGuidMap);
            var present = plans.Where(p => p.PlanId != null).Select(p => p.PlanId!.Value).ToHashSet();
            var missing = Enum.GetValues<PlanId>().Where(pid => !present.Contains(pid)).ToList();
            var discoveredMappings = new Dictionary<string, string>();
            bool ok = true;
            foreach (var pid in missing)
            {
                string canonical = GuidFor(pid);
                var output = _runPowercfg($"-duplicatescheme {canonical}");
                var m = GuidRegex.Match(output);
                if (m.Success)
                    discoveredMappings[pid.ToString()] = m.Groups["guid"].Value.ToLowerInvariant();
                else
                    ok = false;
            }
            if (discoveredMappings.Count > 0)
            {
                _settings.Update(state =>
                {
                    foreach (var (plan, guid) in discoveredMappings)
                        state.PlanGuidMap[plan] = guid;
                });
            }
            return ok;
        }
    }

    public bool SetActivePlan(PlanId plan, PlanChangeContext? context = null)
    {
        var revisions = new List<long>(2);
        bool success;
        lock (_sync)
        {
            string guid = TargetGuidLocked(plan);
            success = ApplyPlanLocked(
                guid,
                plan,
                context ?? new PlanChangeContext(
                    PlanHistoryCategory.Manual,
                    "manual",
                    "manual_selection",
                    new Dictionary<string, string>()),
                reapplyOnly: false,
                revisions);
        }
        PublishHistoryChanges(revisions);
        return success;
    }

    internal bool ReapplyPlan(string guid, PlanChangeContext context)
    {
        var revisions = new List<long>(2);
        bool success;
        lock (_sync)
        {
            success = ApplyPlanLocked(
                guid.ToLowerInvariant(),
                ResolvePlanId(guid, _settings.Current.PlanGuidMap),
                context,
                reapplyOnly: true,
                revisions);
        }
        PublishHistoryChanges(revisions);
        return success;
    }

    internal string ExecutePowercfg(string args) => _runPowercfg(args);

    /// <summary>Actual GUID on this machine: mapped duplicate if present, else canonical.</summary>
    private string TargetGuidLocked(PlanId plan)
    {
        if (_settings.Current.PlanGuidMap.TryGetValue(plan.ToString(), out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            // Verify mapped guid still exists; fall back to canonical otherwise.
            var existing = ParseListOutput(_runPowercfg("/list"));
            if (existing.Any(p => p.Guid.Equals(mapped, StringComparison.OrdinalIgnoreCase)))
                return mapped;
        }
        return GuidFor(plan);
    }

    private bool ApplyPlanLocked(
        string requestedGuid,
        PlanId? requestedPlanId,
        PlanChangeContext context,
        bool reapplyOnly,
        List<long> revisions)
    {
        var current = ObserveActivePlanLocked(revisions);
        // Recheck under the same lock as /setactive: editing a plan must never
        // switch back to it after another command selected a different plan.
        if (reapplyOnly && (current == null || !SameGuid(current.Guid, requestedGuid)))
            return current != null;
        var previous = current ?? _lastObserved;
        var requested = new PlanHistoryPlan(requestedGuid, requestedPlanId?.ToString() ?? "", requestedPlanId);

        if (!reapplyOnly && current != null && SameGuid(current.Guid, requestedGuid))
        {
            History.EndProblemGroup();
            return true;
        }

        _runPowercfg($"/setactive {requestedGuid}");
        var observed = ReadActivePlanLocked();
        var timestamp = _clock.UtcNow;

        if (observed == null)
        {
            _pendingUnverifiedGuid = requestedGuid;
            revisions.Add(History.Record(
                timestamp,
                context,
                ToHistoryPlan(previous),
                requested,
                null,
                PlanHistoryOutcome.Unverifiable));
            return false;
        }

        _pendingUnverifiedGuid = null;
        _lastObserved = observed;
        if (SameGuid(observed.Guid, requestedGuid))
        {
            History.EndProblemGroup();
            if (previous == null || !SameGuid(previous.Guid, requestedGuid))
            {
                revisions.Add(History.Record(
                    timestamp,
                    context,
                    ToHistoryPlan(previous),
                    requested,
                    ToHistoryPlan(observed),
                    PlanHistoryOutcome.Applied));
            }
            return true;
        }

        revisions.Add(History.Record(
            timestamp,
            context,
            ToHistoryPlan(previous),
            requested,
            ToHistoryPlan(observed),
            PlanHistoryOutcome.Failed));
        return false;
    }

    private PowerPlan? ObserveActivePlanLocked(List<long> revisions)
    {
        var current = ReadActivePlanLocked();
        if (current == null)
            return null;

        bool explainedByUnverifiedRequest = _pendingUnverifiedGuid != null &&
            (SameGuid(_pendingUnverifiedGuid, current.Guid) ||
             (_lastObserved != null && SameGuid(_lastObserved.Guid, current.Guid)));
        _pendingUnverifiedGuid = null;

        if (!explainedByUnverifiedRequest && _lastObserved != null && !SameGuid(_lastObserved.Guid, current.Guid))
        {
            revisions.Add(History.Record(
                _clock.UtcNow,
                new PlanChangeContext(
                    PlanHistoryCategory.External,
                    "external",
                    "external_change_detected",
                    new Dictionary<string, string>()),
                ToHistoryPlan(_lastObserved),
                null,
                ToHistoryPlan(current),
                PlanHistoryOutcome.ExternalDetected));
        }

        _lastObserved = current;
        return current;
    }

    private void PublishHistoryChanges(IEnumerable<long> revisions)
    {
        foreach (var revision in revisions)
            History.PublishChanged(revision);
    }

    private static PlanHistoryPlan? ToHistoryPlan(PowerPlan? plan)
        => plan == null ? null : new PlanHistoryPlan(plan.Guid, plan.Name, plan.PlanId);

    private static bool SameGuid(string a, string b)
        => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    public static string GuidFor(PlanId plan) => plan switch
    {
        PlanId.PowerSaver => SaverGuid,
        PlanId.Balanced => BalancedGuid,
        PlanId.Performance => PerformanceGuid,
        _ => BalancedGuid,
    };
}
