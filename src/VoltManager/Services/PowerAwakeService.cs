using System.Runtime.InteropServices;
using System.Threading;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Holds a Windows power request while enabled, preventing automatic system sleep
/// without permanently changing the timeout values of any power plan.
/// Optional safety: auto-disable on battery and/or after a max duration so the
/// feature cannot silently drain a laptop overnight.
/// </summary>
public sealed class PowerAwakeService : IDisposable
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 1;

    private readonly SettingsService _settings;
    private readonly Func<bool?> _onBatteryReader;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Timer _guardTimer;
    private IntPtr _requestHandle = IntPtr.Zero;
    private bool _systemRequestApplied;
    private bool _executionRequestApplied;
    private bool _automationRequested;
    private bool _disposed;
    private string? _lastAutoDisableReason;
    private bool _applyingSettings; // re-entrancy guard for SettingsChanged → Save

    public event Action<KeepAwakeState>? StateChanged;

    public PowerAwakeService(
        SettingsService settings,
        Func<bool?>? onBatteryReader = null,
        Func<DateTime>? utcNow = null)
    {
        _settings = settings;
        _onBatteryReader = onBatteryReader ?? ReadOnBattery;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _settings.SettingsChanged += OnSettingsChanged;
        // Guard tick every 20s — cheap, covers unplug and duration expiry while trayed.
        _guardTimer = new Timer(_ => SafeGuardTick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
        ApplyFromSettings();
    }

    public KeepAwakeState GetState()
    {
        lock (_lock)
        {
            return BuildStateLocked();
        }
    }

    public KeepAwakeState SetEnabled(bool enabled)
    {
        _settings.Current.KeepAwake ??= new KeepAwakeSettings();
        var cfg = _settings.Current.KeepAwake;
        cfg.Normalize();
        cfg.Enabled = enabled;
        cfg.LastChangedUtc = _utcNow();
        if (enabled)
            _lastAutoDisableReason = null;
        _settings.Save(); // triggers ApplyFromSettings via SettingsChanged
        // Immediate safety pass (e.g. user enables while already on battery).
        EvaluateSafetyAndApply(forceNotify: true);
        return GetState();
    }

    public KeepAwakeState SetSafetyOptions(bool autoDisableOnBattery, int maxMinutes)
    {
        _settings.Current.KeepAwake ??= new KeepAwakeSettings();
        var cfg = _settings.Current.KeepAwake;
        cfg.AutoDisableOnBattery = autoDisableOnBattery;
        cfg.MaxMinutes = maxMinutes;
        cfg.Normalize();
        _settings.Save();
        EvaluateSafetyAndApply(forceNotify: true);
        return GetState();
    }

    /// <summary>
    /// Runtime-only request used by app profiles. It never rewrites the user's
    /// KeepAwake.Enabled preference and disappears as soon as the profile ends.
    /// </summary>
    public KeepAwakeState SetAutomationRequest(bool enabled)
    {
        lock (_lock)
            _automationRequested = enabled;
        EvaluateSafetyAndApply(forceNotify: true);
        return GetState();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_applyingSettings) return;
        ApplyFromSettings();
    }

    private void SafeGuardTick()
    {
        try { EvaluateSafetyAndApply(forceNotify: false); }
        catch (Exception ex) { Logger.Warn("Keep-awake guard tick failed: " + ex.Message); }
    }

    /// <summary>
    /// Pure decision: given settings + battery + now, should keep-awake stay on?
    /// Returns disable reason or null if it may remain enabled.
    /// </summary>
    public static string? ShouldAutoDisable(
        KeepAwakeSettings cfg,
        bool? onBattery,
        DateTime nowUtc)
        => SafetyBlockReason(cfg, automationRequested: false, onBattery, nowUtc);

    internal static string? SafetyBlockReason(
        KeepAwakeSettings cfg,
        bool automationRequested,
        bool? onBattery,
        DateTime nowUtc)
    {
        if (!cfg.Enabled && !automationRequested) return null;

        if (cfg.AutoDisableOnBattery && onBattery == true)
            return "battery";

        if (cfg.Enabled && cfg.MaxMinutes > 0 && cfg.LastChangedUtc is DateTime started)
        {
            var elapsed = nowUtc - started;
            if (elapsed.TotalMinutes >= cfg.MaxMinutes)
                return "timeout";
        }

        return null;
    }

    public static long? RemainingSeconds(KeepAwakeSettings cfg, DateTime nowUtc)
    {
        if (!cfg.Enabled || cfg.MaxMinutes <= 0 || cfg.LastChangedUtc is not DateTime started)
            return null;
        double left = cfg.MaxMinutes * 60.0 - (nowUtc - started).TotalSeconds;
        return left <= 0 ? 0 : (long)Math.Ceiling(left);
    }

    private void EvaluateSafetyAndApply(bool forceNotify)
    {
        if (_disposed) return;

        var cfg = _settings.Current.KeepAwake ?? new KeepAwakeSettings();
        cfg.Normalize();
        var now = _utcNow();
        bool? onBattery = null;
        try { onBattery = _onBatteryReader(); }
        catch { /* unknown power source: skip battery guard this tick */ }

        bool automationRequested;
        lock (_lock) automationRequested = _automationRequested;
        string? reason = SafetyBlockReason(cfg, automationRequested, onBattery, now);
        bool changed = false;

        if (reason != null)
        {
            if (!cfg.Enabled)
            {
                lock (_lock)
                {
                    _lastAutoDisableReason = reason;
                    changed = ClearRequestLocked();
                }
                if (changed || forceNotify)
                    StateChanged?.Invoke(GetState());
                return;
            }

            _applyingSettings = true;
            try
            {
                lock (_lock)
                {
                    _lastAutoDisableReason = reason;
                    changed = ClearRequestLocked();
                }
                cfg.Enabled = false;
                cfg.LastChangedUtc = now;
                _settings.Current.KeepAwake = cfg;
                _settings.Save();
            }
            finally
            {
                _applyingSettings = false;
            }

            Logger.Warn($"Keep-awake auto-disabled ({reason}).");
            StateChanged?.Invoke(GetState());
            return;
        }

        lock (_lock)
            changed = (cfg.Enabled || _automationRequested) ? EnsureRequestLocked() : ClearRequestLocked();

        if (changed || forceNotify)
            StateChanged?.Invoke(GetState());
    }

    private void ApplyFromSettings()
    {
        if (_disposed || _applyingSettings) return;

        var cfg = _settings.Current.KeepAwake ?? new KeepAwakeSettings();
        cfg.Normalize();

        // If already past timeout or on battery, disable rather than re-applying.
        bool automationRequested;
        lock (_lock) automationRequested = _automationRequested;
        string? reason = SafetyBlockReason(cfg, automationRequested, SafeOnBattery(), _utcNow());
        if (reason != null)
        {
            EvaluateSafetyAndApply(forceNotify: true);
            return;
        }

        bool changed;
        lock (_lock)
        {
            changed = (cfg.Enabled || _automationRequested) ? EnsureRequestLocked() : ClearRequestLocked();
        }

        if (changed)
            StateChanged?.Invoke(GetState());
    }

    private bool? SafeOnBattery()
    {
        try { return _onBatteryReader(); }
        catch { return null; }
    }

    private KeepAwakeState BuildStateLocked()
    {
        var cfg = _settings.Current.KeepAwake ?? new KeepAwakeSettings();
        cfg.Normalize();
        var now = _utcNow();
        long? remaining = RemainingSeconds(cfg, now);

        string message;
        if (_systemRequestApplied)
            message = remaining is long s && s > 0
                ? "active_timed"
                : "active";
        else if (_lastAutoDisableReason == "battery")
            message = "auto_off_battery";
        else if (_lastAutoDisableReason == "timeout")
            message = "auto_off_timeout";
        else
            message = "inactive";

        return new KeepAwakeState
        {
            Enabled = cfg.Enabled,
            Applied = _systemRequestApplied,
            AutomationRequested = _automationRequested,
            LastChangedUtc = cfg.LastChangedUtc,
            Message = message,
            AutoDisableOnBattery = cfg.AutoDisableOnBattery,
            MaxMinutes = cfg.MaxMinutes,
            RemainingSeconds = remaining,
            LastAutoDisableReason = _lastAutoDisableReason,
        };
    }

    private bool EnsureRequestLocked()
    {
        ThrowIfDisposed();

        if (_requestHandle == IntPtr.Zero)
        {
            var context = new PowerRequestContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = "VoltManager keep-awake mode",
            };
            _requestHandle = PowerCreateRequest(ref context);
            if (_requestHandle == IntPtr.Zero || _requestHandle == new IntPtr(-1))
            {
                _requestHandle = IntPtr.Zero;
                _systemRequestApplied = false;
                _executionRequestApplied = false;
                return true;
            }
        }

        bool before = _systemRequestApplied;
        _systemRequestApplied = PowerSetRequest(_requestHandle, PowerRequestType.PowerRequestSystemRequired);

        // Best-effort for modern standby/connected-standby systems. Older Windows
        // builds may reject this request type; the system-required request above is
        // the required one for normal desktop sleep prevention.
        _executionRequestApplied = PowerSetRequest(_requestHandle, PowerRequestType.PowerRequestExecutionRequired);
        return before != _systemRequestApplied;
    }

    private bool ClearRequestLocked()
    {
        bool wasApplied = _systemRequestApplied || _executionRequestApplied || _requestHandle != IntPtr.Zero;

        if (_requestHandle != IntPtr.Zero)
        {
            if (_systemRequestApplied)
                PowerClearRequest(_requestHandle, PowerRequestType.PowerRequestSystemRequired);
            if (_executionRequestApplied)
                PowerClearRequest(_requestHandle, PowerRequestType.PowerRequestExecutionRequired);
            CloseHandle(_requestHandle);
            _requestHandle = IntPtr.Zero;
        }

        _systemRequestApplied = false;
        _executionRequestApplied = false;
        return wasApplied;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PowerAwakeService));
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        try { _guardTimer.Dispose(); } catch { }
        lock (_lock)
        {
            ClearRequestLocked();
            _disposed = true;
        }
    }

    /// <summary>true = running on battery (AC offline), false = plugged, null = unknown.</summary>
    private static bool? ReadOnBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return null;
            // ACLineStatus: 0 = offline (battery), 1 = online, 255 = unknown
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
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus sps);

    private enum PowerRequestType
    {
        PowerRequestDisplayRequired = 0,
        PowerRequestSystemRequired = 1,
        PowerRequestAwayModeRequired = 2,
        PowerRequestExecutionRequired = 3,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PowerRequestContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref PowerRequestContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr powerRequestHandle, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr powerRequestHandle, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
