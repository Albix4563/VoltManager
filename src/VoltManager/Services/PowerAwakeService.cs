using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Holds a Windows power request while enabled, preventing automatic system sleep
/// without permanently changing the timeout values of any power plan.
/// </summary>
public sealed class PowerAwakeService : IDisposable
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 1;

    private readonly SettingsService _settings;
    private readonly object _lock = new();
    private IntPtr _requestHandle = IntPtr.Zero;
    private bool _systemRequestApplied;
    private bool _executionRequestApplied;
    private bool _disposed;

    public event Action<KeepAwakeState>? StateChanged;

    public PowerAwakeService(SettingsService settings)
    {
        _settings = settings;
        _settings.SettingsChanged += OnSettingsChanged;
        ApplyFromSettings();
    }

    public KeepAwakeState GetState()
    {
        lock (_lock)
        {
            var cfg = _settings.Current.KeepAwake ?? new KeepAwakeSettings();
            return new KeepAwakeState
            {
                Enabled = cfg.Enabled,
                Applied = _systemRequestApplied,
                LastChangedUtc = cfg.LastChangedUtc,
                Message = _systemRequestApplied
                    ? "Sospensione automatica bloccata"
                    : "Sospensione automatica normale",
            };
        }
    }

    public KeepAwakeState SetEnabled(bool enabled)
    {
        _settings.Current.KeepAwake ??= new KeepAwakeSettings();
        _settings.Current.KeepAwake.Enabled = enabled;
        _settings.Current.KeepAwake.LastChangedUtc = DateTime.UtcNow;
        _settings.Save();
        return GetState();
    }

    private void OnSettingsChanged(AppSettings settings) => ApplyFromSettings();

    private void ApplyFromSettings()
    {
        bool enabled = _settings.Current.KeepAwake?.Enabled == true;
        bool changed;

        lock (_lock)
        {
            changed = enabled ? EnsureRequestLocked() : ClearRequestLocked();
        }

        if (changed)
            StateChanged?.Invoke(GetState());
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
        lock (_lock)
        {
            ClearRequestLocked();
            _disposed = true;
        }
    }

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
