using VoltManager.Models;

namespace VoltManager.Services;

public record ThermalGuardDecision
{
    public PlanId? TargetPlan { get; init; }
    public bool BlocksLowerPriority { get; init; }
    public ThermalGuardState State { get; init; } = new();
}

/// <summary>
/// Forces a cooler power plan when CPU (and optionally GPU) temperature stays
/// above a trip threshold for a hold period. Restores the previous plan once
/// temps fall below a cool threshold (hysteresis). Pure evaluate + light state.
/// </summary>
public sealed class ThermalGuardService
{
    private readonly SettingsService _settings;
    private readonly object _lock = new();

    private bool _sessionActive;
    private PlanId? _planBeforeSession;
    private DateTime? _hotSinceUtc;
    private double? _lastCpu;
    private double? _lastGpu;
    private ThermalGuardState _current = new();

    public event Action<ThermalGuardState>? StateChanged;

    public ThermalGuardService(SettingsService settings)
    {
        _settings = settings;
        _current = BuildState(null, null, false, "init");
    }

    public ThermalGuardState Current
    {
        get { lock (_lock) return _current; }
    }

    public ThermalGuardDecision Evaluate(
        double? cpuTemp,
        double? gpuTemp,
        PlanId? activePlan,
        bool manualOverrideActive,
        bool masterAutomationEnabled,
        DateTime nowUtc)
    {
        lock (_lock)
        {
            var cfg = EnsureSettings();
            _lastCpu = cpuTemp is > 0 and < 150 ? cpuTemp : null;
            _lastGpu = gpuTemp is > 0 and < 150 ? gpuTemp : null;

            if (manualOverrideActive)
            {
                // Do not clear an active thermal session mid-override; just don't fight the user.
                return Decision(null, _sessionActive, "manual_override");
            }

            if (!masterAutomationEnabled || !cfg.Enabled)
            {
                if (_sessionActive)
                    return EndSession(activePlan, "disabled");
                return Decision(null, false, sensorsOk() ? "disabled" : "disabled_no_sensors");
            }

            if (!sensorsOk())
            {
                // Without temps we cannot safely manage; leave any session as-is until data returns.
                if (_sessionActive)
                    return Decision(cfg.TargetPlan, true, "active_no_sensors");
                return Decision(null, false, "no_sensors");
            }

            double peak = PeakTemp(cfg);
            bool hot = peak >= cfg.ThresholdCelsius;
            bool cool = peak <= cfg.CoolThresholdCelsius;

            if (_sessionActive)
            {
                if (cool)
                    return EndSession(activePlan, "cooled");

                // Stay on cooler plan until hysteresis cool-down.
                var stayTarget = activePlan == cfg.TargetPlan ? (PlanId?)null : cfg.TargetPlan;
                return Decision(stayTarget, true, stayTarget == null ? "active" : "active_switch");
            }

            if (hot)
            {
                _hotSinceUtc ??= nowUtc;
                double held = (nowUtc - _hotSinceUtc.Value).TotalSeconds;
                if (held < cfg.HoldSeconds)
                    return Decision(null, false, "warming", held);

                // Trip: open session.
                _planBeforeSession = activePlan == cfg.TargetPlan ? PlanId.Balanced : activePlan;
                _sessionActive = true;
                _hotSinceUtc = nowUtc;
                var target = activePlan == cfg.TargetPlan ? (PlanId?)null : cfg.TargetPlan;
                return Decision(target, true, target == null ? "tripped_already" : "tripped");
            }

            _hotSinceUtc = null;
            return Decision(null, false, "idle");
        }

        bool sensorsOk() => _lastCpu != null || (EnsureSettings().WatchGpu && _lastGpu != null);
    }

    public ThermalGuardState SetEnabled(bool enabled)
    {
        var cfg = EnsureSettings();
        cfg.Enabled = enabled;
        cfg.Normalize();
        _settings.Save();
        lock (_lock)
        {
            if (!enabled && _sessionActive)
            {
                // Session ends on next Evaluate; mark inactive for UI immediately.
                _sessionActive = false;
                _planBeforeSession = null;
                _hotSinceUtc = null;
            }
            var state = BuildState(_lastCpu, _lastGpu, false, enabled ? "enabled" : "disabled");
            Publish(state);
            return state;
        }
    }

    public ThermalGuardState ApplySettings(ThermalGuardSettings incoming)
    {
        incoming.Normalize();
        _settings.Current.ThermalGuard = incoming;
        _settings.Save();
        lock (_lock)
        {
            var state = BuildState(_lastCpu, _lastGpu, _sessionActive, "settings");
            Publish(state);
            return state;
        }
    }

    public void ClearSession()
    {
        lock (_lock)
        {
            _sessionActive = false;
            _planBeforeSession = null;
            _hotSinceUtc = null;
            Publish(BuildState(_lastCpu, _lastGpu, false, "cleared"));
        }
    }

    private ThermalGuardDecision EndSession(PlanId? activePlan, string message)
    {
        var previous = _planBeforeSession;
        _sessionActive = false;
        _planBeforeSession = null;
        _hotSinceUtc = null;
        PlanId? target = null;
        if (previous != null && activePlan != previous)
            target = previous;
        return Decision(target, target != null, message);
    }

    private ThermalGuardDecision Decision(PlanId? target, bool blocks, string message, double? hotHold = null)
    {
        var state = BuildState(_lastCpu, _lastGpu, _sessionActive, message, hotHold);
        Publish(state);
        return new ThermalGuardDecision
        {
            TargetPlan = target,
            BlocksLowerPriority = blocks,
            State = state,
        };
    }

    private ThermalGuardState BuildState(
        double? cpu,
        double? gpu,
        bool active,
        string message,
        double? hotHold = null)
    {
        var cfg = EnsureSettings();
        double? peak = null;
        if (cpu != null || gpu != null)
        {
            peak = cpu ?? double.MinValue;
            if (cfg.WatchGpu && gpu != null)
                peak = Math.Max(peak.Value, gpu.Value);
            if (peak == double.MinValue) peak = null;
        }

        return new ThermalGuardState
        {
            Enabled = cfg.Enabled,
            Active = active,
            SensorsAvailable = cpu != null || (cfg.WatchGpu && gpu != null),
            CpuTemp = cpu,
            GpuTemp = gpu,
            PeakTemp = peak is double.MinValue ? null : peak,
            ThresholdCelsius = cfg.ThresholdCelsius,
            CoolThresholdCelsius = cfg.CoolThresholdCelsius,
            HoldSeconds = cfg.HoldSeconds,
            TargetPlan = cfg.TargetPlan,
            WatchGpu = cfg.WatchGpu,
            SavedPlan = _planBeforeSession,
            HotHoldSeconds = hotHold ?? (_hotSinceUtc is DateTime hs
                ? Math.Max(0, (DateTime.UtcNow - hs).TotalSeconds)
                : 0),
            Message = message,
        };
    }

    private double PeakTemp(ThermalGuardSettings cfg)
    {
        double peak = _lastCpu ?? double.MinValue;
        if (cfg.WatchGpu && _lastGpu != null)
            peak = Math.Max(peak, _lastGpu.Value);
        return peak == double.MinValue ? 0 : peak;
    }

    private ThermalGuardSettings EnsureSettings()
    {
        _settings.Current.ThermalGuard ??= new ThermalGuardSettings();
        _settings.Current.ThermalGuard.Normalize();
        return _settings.Current.ThermalGuard;
    }

    private void Publish(ThermalGuardState state)
    {
        bool changed = !StatesEqual(_current, state);
        _current = state;
        if (changed)
            StateChanged?.Invoke(state);
    }

    private static bool StatesEqual(ThermalGuardState a, ThermalGuardState b)
        => a.Enabled == b.Enabled
           && a.Active == b.Active
           && a.SensorsAvailable == b.SensorsAvailable
           && a.CpuTemp == b.CpuTemp
           && a.GpuTemp == b.GpuTemp
           && a.PeakTemp == b.PeakTemp
           && a.ThresholdCelsius == b.ThresholdCelsius
           && a.CoolThresholdCelsius == b.CoolThresholdCelsius
           && a.HoldSeconds == b.HoldSeconds
           && a.TargetPlan == b.TargetPlan
           && a.WatchGpu == b.WatchGpu
           && a.SavedPlan == b.SavedPlan
           && a.Message == b.Message
           && Math.Abs(a.HotHoldSeconds - b.HotHoldSeconds) < 0.5;
}
