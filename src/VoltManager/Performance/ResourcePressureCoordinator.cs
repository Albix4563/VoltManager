using VoltManager.Models;

namespace VoltManager.Performance;

/// <summary>
/// Converts system/game signals into one stable operational profile. The coordinator
/// only governs elastic work; it never changes MonitorService's safety sampling cadence.
/// </summary>
public sealed class ResourcePressureCoordinator
{
    private readonly object _gate = new();
    private readonly int _logicalCores;
    private DateTime? _criticalCandidateSinceUtc;
    private DateTime? _criticalClearSinceUtc;
    private DateTime? _lastGameActiveUtc;
    private DateTime? _lastWorkloadActiveUtc;
    private ResourcePressureState _current = new();

    public ResourcePressureCoordinator(int? logicalCores = null)
    {
        _logicalCores = Math.Max(1, logicalCores ?? Environment.ProcessorCount);
    }

    public ResourcePressureState Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<ResourcePressureState>? StateChanged;

    public ResourcePressureState Observe(MetricsSnapshot metrics, bool gameActive, DateTime? nowUtc = null)
        => Observe(metrics, gameActive, workloadActive: false, nowUtc);

    public ResourcePressureState Observe(
        MetricsSnapshot metrics,
        bool gameActive,
        bool workloadActive,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        ResourcePressureState next;
        bool notify;

        lock (_gate)
        {
            if (gameActive)
                _lastGameActiveUtc = now;
            if (workloadActive)
                _lastWorkloadActiveUtc = now;

            bool effectiveGameActive = gameActive ||
                (_lastGameActiveUtc is DateTime lastGame && now - lastGame < ResourcePressurePolicy.GameExitCooldown);
            bool effectiveWorkloadActive = !effectiveGameActive && (workloadActive ||
                (_lastWorkloadActiveUtc is DateTime lastWorkload &&
                 now - lastWorkload < ResourcePressurePolicy.ProtectedWorkloadExitCooldown));

            var baseline = ResourcePressurePolicy.BaselineProfile(metrics.RamTotalGb, _logicalCores);
            bool memoryCritical = metrics.RamPct >= ResourcePressurePolicy.CriticalRamEnterPct;
            bool extremeSystemLoad = ResourcePressurePolicy.IsExtremeSystemLoad(metrics);
            ResourceProfile profile;
            string reason;

            if (memoryCritical)
            {
                _criticalCandidateSinceUtc = null;
                _criticalClearSinceUtc = null;
                profile = ResourceProfile.Critical;
                reason = "memory_pressure";
            }
            else if (_current.Profile == ResourceProfile.Critical)
            {
                bool clear = metrics.RamPct <= ResourcePressurePolicy.CriticalRamExitPct && !extremeSystemLoad;
                if (clear)
                {
                    _criticalClearSinceUtc ??= now;
                    if (now - _criticalClearSinceUtc >= ResourcePressurePolicy.CriticalExitDelay)
                    {
                        _criticalClearSinceUtc = null;
                        profile = ActiveProfile(effectiveGameActive, effectiveWorkloadActive, baseline);
                        reason = ActiveReason(effectiveGameActive, effectiveWorkloadActive, baseline);
                    }
                    else
                    {
                        profile = ResourceProfile.Critical;
                        reason = "pressure_cooldown";
                    }
                }
                else
                {
                    _criticalClearSinceUtc = null;
                    profile = ResourceProfile.Critical;
                    reason = extremeSystemLoad
                        ? LoadReason(effectiveGameActive, effectiveWorkloadActive)
                        : "memory_pressure";
                }
            }
            else if (extremeSystemLoad)
            {
                _criticalCandidateSinceUtc ??= now;
                if (now - _criticalCandidateSinceUtc >= ResourcePressurePolicy.CriticalEnterDelay)
                {
                    _criticalCandidateSinceUtc = null;
                    _criticalClearSinceUtc = null;
                    profile = ResourceProfile.Critical;
                    reason = LoadReason(effectiveGameActive, effectiveWorkloadActive);
                }
                else
                {
                    profile = ActiveProfile(effectiveGameActive, effectiveWorkloadActive, baseline);
                    reason = ActiveReason(effectiveGameActive, effectiveWorkloadActive, baseline);
                }
            }
            else
            {
                _criticalCandidateSinceUtc = null;
                _criticalClearSinceUtc = null;
                profile = ActiveProfile(effectiveGameActive, effectiveWorkloadActive, baseline);
                reason = ActiveReason(effectiveGameActive, effectiveWorkloadActive, baseline);
            }

            next = _current with
            {
                Profile = profile,
                GameActive = effectiveGameActive,
                WorkloadActive = effectiveWorkloadActive,
                ProtectedWorkloadActive = effectiveGameActive || effectiveWorkloadActive,
                CpuPercent = metrics.Cpu,
                GpuPercent = metrics.Gpu,
                RamPercent = metrics.RamPct,
                Reason = reason,
                EvaluatedAtUtc = now,
            };

            notify = HasOperationalChange(_current, next);
            _current = next;
        }

        if (notify) StateChanged?.Invoke(next);
        return next;
    }

    public ResourcePressureState SetUiVisible(bool visible, DateTime? nowUtc = null)
    {
        ResourcePressureState next;
        bool notify;
        lock (_gate)
        {
            if (_current.UiVisible == visible) return _current;
            next = _current with
            {
                UiVisible = visible,
                EvaluatedAtUtc = nowUtc ?? DateTime.UtcNow,
            };
            notify = true;
            _current = next;
        }
        if (notify) StateChanged?.Invoke(next);
        return next;
    }

    private static bool HasOperationalChange(ResourcePressureState previous, ResourcePressureState next)
        => previous.Profile != next.Profile ||
           previous.GameActive != next.GameActive ||
           previous.WorkloadActive != next.WorkloadActive ||
           previous.ProtectedWorkloadActive != next.ProtectedWorkloadActive ||
           previous.UiVisible != next.UiVisible ||
           !string.Equals(previous.Reason, next.Reason, StringComparison.Ordinal);

    private static ResourceProfile ActiveProfile(bool gameActive, bool workloadActive, ResourceProfile baseline)
        => gameActive ? ResourceProfile.Gaming : workloadActive ? ResourceProfile.Workload : baseline;

    private static string ActiveReason(bool gameActive, bool workloadActive, ResourceProfile baseline)
        => gameActive ? "game_active" : workloadActive ? "workload_active" : BaselineReason(baseline);

    private static string LoadReason(bool gameActive, bool workloadActive)
        => gameActive ? "game_load" : workloadActive ? "workload_load" : "system_load";

    private static string BaselineReason(ResourceProfile profile)
        => profile == ResourceProfile.Balanced ? "hardware_tier" : "normal";
}
