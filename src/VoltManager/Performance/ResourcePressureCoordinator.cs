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
    {
        var now = nowUtc ?? DateTime.UtcNow;
        ResourcePressureState next;
        bool notify;

        lock (_gate)
        {
            if (gameActive)
                _lastGameActiveUtc = now;

            bool effectiveGameActive = gameActive ||
                (_lastGameActiveUtc is DateTime lastGame && now - lastGame < ResourcePressurePolicy.GameExitCooldown);

            var baseline = ResourcePressurePolicy.BaselineProfile(metrics.RamTotalGb, _logicalCores);
            bool memoryCritical = metrics.RamPct >= ResourcePressurePolicy.CriticalRamEnterPct;
            bool extremeGameLoad = ResourcePressurePolicy.IsExtremeGameLoad(metrics, effectiveGameActive);
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
                bool clear = metrics.RamPct <= ResourcePressurePolicy.CriticalRamExitPct && !extremeGameLoad;
                if (clear)
                {
                    _criticalClearSinceUtc ??= now;
                    if (now - _criticalClearSinceUtc >= ResourcePressurePolicy.CriticalExitDelay)
                    {
                        _criticalClearSinceUtc = null;
                        profile = effectiveGameActive ? ResourceProfile.Gaming : baseline;
                        reason = effectiveGameActive ? "game_active" : BaselineReason(baseline);
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
                    reason = extremeGameLoad ? "game_load" : "memory_pressure";
                }
            }
            else if (extremeGameLoad)
            {
                _criticalCandidateSinceUtc ??= now;
                if (now - _criticalCandidateSinceUtc >= ResourcePressurePolicy.CriticalEnterDelay)
                {
                    _criticalCandidateSinceUtc = null;
                    _criticalClearSinceUtc = null;
                    profile = ResourceProfile.Critical;
                    reason = "game_load";
                }
                else
                {
                    profile = ResourceProfile.Gaming;
                    reason = "game_active";
                }
            }
            else
            {
                _criticalCandidateSinceUtc = null;
                _criticalClearSinceUtc = null;
                profile = effectiveGameActive ? ResourceProfile.Gaming : baseline;
                reason = effectiveGameActive ? "game_active" : BaselineReason(baseline);
            }

            next = _current with
            {
                Profile = profile,
                GameActive = effectiveGameActive,
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
           previous.UiVisible != next.UiVisible ||
           !string.Equals(previous.Reason, next.Reason, StringComparison.Ordinal);

    private static string BaselineReason(ResourceProfile profile)
        => profile == ResourceProfile.Balanced ? "hardware_tier" : "normal";
}
