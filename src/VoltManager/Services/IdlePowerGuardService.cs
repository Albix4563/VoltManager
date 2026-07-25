using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

public record IdlePowerGuardDecision
{
    public PlanId? TargetPlan { get; init; }
    public bool BlocksLowerPriority { get; init; }
    public IdlePowerGuardState State { get; init; } = new();
}

/// <summary>
/// Switches to a frugal power plan after sustained user idle (GetLastInputInfo),
/// then restores the previous plan when input resumes. Pure evaluate + session state.
/// </summary>
public sealed class IdlePowerGuardService
{
    private readonly SettingsService _settings;
    private readonly Func<uint?> _idleMsReader;
    private readonly Func<bool?> _onBatteryReader;
    private readonly object _lock = new();

    private bool _sessionActive;
    private PlanId? _planBeforeSession;
    private IdlePowerGuardState _current = new();

    public event Action<IdlePowerGuardState>? StateChanged;

    public IdlePowerGuardService(
        SettingsService settings,
        Func<uint?>? idleMsReader = null,
        Func<bool?>? onBatteryReader = null)
    {
        _settings = settings;
        _idleMsReader = idleMsReader ?? ReadIdleMilliseconds;
        _onBatteryReader = onBatteryReader ?? ReadOnBattery;
        _current = BuildState(0, null, true, "init");
    }

    public IdlePowerGuardState Current
    {
        get { lock (_lock) return _current; }
    }

    public IdlePowerGuardDecision Evaluate(
        PlanId? activePlan,
        bool manualOverrideActive,
        bool masterAutomationEnabled,
        DateTime nowUtc)
    {
        lock (_lock)
        {
            var cfg = EnsureSettings();
            uint? idleMs = null;
            try { idleMs = _idleMsReader(); } catch { /* degrade */ }
            bool inputOk = idleMs != null;
            double idleSec = idleMs is uint ms ? ms / 1000.0 : 0;
            bool? onBattery = null;
            try { onBattery = _onBatteryReader(); } catch { }

            if (manualOverrideActive)
                return Decision(null, _sessionActive, idleSec, onBattery, inputOk, "manual_override");

            if (!masterAutomationEnabled || !cfg.Enabled)
            {
                if (_sessionActive)
                    return EndSession(activePlan, idleSec, onBattery, inputOk, "disabled");
                return Decision(null, false, idleSec, onBattery, inputOk, "disabled");
            }

            if (!inputOk)
            {
                if (_sessionActive)
                    return Decision(cfg.TargetPlan, true, idleSec, onBattery, false, "active_no_input");
                return Decision(null, false, idleSec, onBattery, false, "no_input");
            }

            // Battery-only mode: if plugged in (or unknown), do not engage; end session if needed.
            if (cfg.OnlyOnBattery && onBattery != true)
            {
                if (_sessionActive)
                    return EndSession(activePlan, idleSec, onBattery, true, "battery_skip");
                return Decision(null, false, idleSec, onBattery, true, "battery_skip");
            }

            double needSec = cfg.IdleMinutes * 60.0;
            bool isIdle = idleSec >= needSec;

            if (_sessionActive)
            {
                if (!isIdle)
                    return EndSession(activePlan, idleSec, onBattery, true, "resumed");

                var stay = activePlan == cfg.TargetPlan ? (PlanId?)null : cfg.TargetPlan;
                return Decision(stay, true, idleSec, onBattery, true, stay == null ? "active" : "active_switch");
            }

            if (isIdle)
            {
                _planBeforeSession = activePlan == cfg.TargetPlan ? PlanId.Balanced : activePlan;
                _sessionActive = true;
                var target = activePlan == cfg.TargetPlan ? (PlanId?)null : cfg.TargetPlan;
                return Decision(target, true, idleSec, onBattery, true, target == null ? "tripped_already" : "tripped");
            }

            return Decision(null, false, idleSec, onBattery, true, "waiting");
        }
    }

    public IdlePowerGuardState SetEnabled(bool enabled)
    {
        var cfg = EnsureSettings();
        cfg.Enabled = enabled;
        cfg.Normalize();
        _settings.Save();
        lock (_lock)
        {
            if (!enabled)
            {
                _sessionActive = false;
                _planBeforeSession = null;
            }
            var state = BuildState(0, null, true, enabled ? "enabled" : "disabled");
            Publish(state);
            return state;
        }
    }

    public IdlePowerGuardState ApplySettings(IdlePowerGuardSettings incoming)
    {
        incoming.Normalize();
        _settings.Current.IdlePowerGuard = incoming;
        _settings.Save();
        lock (_lock)
        {
            var state = BuildState(0, null, true, "settings");
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
            Publish(BuildState(0, null, true, "cleared"));
        }
    }

    private IdlePowerGuardDecision EndSession(
        PlanId? activePlan,
        double idleSec,
        bool? onBattery,
        bool inputOk,
        string message)
    {
        var previous = _planBeforeSession;
        _sessionActive = false;
        _planBeforeSession = null;
        PlanId? target = null;
        if (previous != null && activePlan != previous)
            target = previous;
        return Decision(target, target != null, idleSec, onBattery, inputOk, message);
    }

    private IdlePowerGuardDecision Decision(
        PlanId? target,
        bool blocks,
        double idleSec,
        bool? onBattery,
        bool inputOk,
        string message)
    {
        var state = BuildState(idleSec, onBattery, inputOk, message);
        Publish(state);
        return new IdlePowerGuardDecision
        {
            TargetPlan = target,
            BlocksLowerPriority = blocks,
            State = state,
        };
    }

    private IdlePowerGuardState BuildState(double idleSec, bool? onBattery, bool inputOk, string message)
    {
        var cfg = EnsureSettings();
        return new IdlePowerGuardState
        {
            Enabled = cfg.Enabled,
            Active = _sessionActive,
            IdleMinutes = cfg.IdleMinutes,
            TargetPlan = cfg.TargetPlan,
            OnlyOnBattery = cfg.OnlyOnBattery,
            IdleSeconds = Math.Round(idleSec, 1),
            InputAvailable = inputOk,
            OnBattery = onBattery,
            SavedPlan = _planBeforeSession,
            Message = message,
        };
    }

    private IdlePowerGuardSettings EnsureSettings()
    {
        _settings.Current.IdlePowerGuard ??= new IdlePowerGuardSettings();
        _settings.Current.IdlePowerGuard.Normalize();
        return _settings.Current.IdlePowerGuard;
    }

    private void Publish(IdlePowerGuardState state)
    {
        bool changed = !StatesEqual(_current, state);
        _current = state;
        if (changed)
            StateChanged?.Invoke(state);
    }

    private static bool StatesEqual(IdlePowerGuardState a, IdlePowerGuardState b)
        => a.Enabled == b.Enabled
           && a.Active == b.Active
           && a.IdleMinutes == b.IdleMinutes
           && a.TargetPlan == b.TargetPlan
           && a.OnlyOnBattery == b.OnlyOnBattery
           && a.InputAvailable == b.InputAvailable
           && a.OnBattery == b.OnBattery
           && a.SavedPlan == b.SavedPlan
           && a.Message == b.Message
           && Math.Abs(a.IdleSeconds - b.IdleSeconds) < 1.0;

    private static uint? ReadIdleMilliseconds()
    {
        var info = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return null;
        uint tick = (uint)Environment.TickCount;
        // TickCount wraps ~49 days; unsigned subtract handles wrap correctly.
        return tick - info.DwTime;
    }

    private static bool? ReadOnBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return null;
            return status.ACLineStatus switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus sps);
}
