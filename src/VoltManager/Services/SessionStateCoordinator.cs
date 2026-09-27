using Microsoft.Win32;

namespace VoltManager.Services;

internal enum SessionStateTransition
{
    Ignored,
    Lock,
    Unlock,
    ConsoleConnect,
    ConsoleDisconnect,
    RemoteConnect,
    RemoteDisconnect,
    Logon,
    Logoff,
}

internal interface ISessionSwitchEventSource
{
    event SessionSwitchEventHandler? SessionSwitch;
}

internal sealed class SystemSessionSwitchEventSource : ISessionSwitchEventSource
{
    public event SessionSwitchEventHandler? SessionSwitch
    {
        add => SystemEvents.SessionSwitch += value;
        remove => SystemEvents.SessionSwitch -= value;
    }
}

internal sealed class SessionStateCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly ISessionSwitchEventSource _eventSource;
    private readonly Action<bool> _setSamplingInactive;
    private readonly Action _signalResumeRecovery;
    private bool _locked;
    private bool _disconnected;
    private bool _loggedOff;
    private bool _started;
    private bool _disposed;

    public SessionStateCoordinator(
        ISessionSwitchEventSource eventSource,
        Action<bool> setSamplingInactive,
        Action signalResumeRecovery)
    {
        _eventSource = eventSource;
        _setSamplingInactive = setSamplingInactive;
        _signalResumeRecovery = signalResumeRecovery;
    }

    public bool IsInactive
    {
        get
        {
            lock (_gate)
                return _locked || _disconnected || _loggedOff;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SessionStateCoordinator));
            if (_started) return;
            _eventSource.SessionSwitch += OnSessionSwitch;
            _started = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _eventSource.SessionSwitch -= OnSessionSwitch;
            _started = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }

    internal static SessionStateTransition Map(SessionSwitchReason reason) => reason switch
    {
        SessionSwitchReason.SessionLock => SessionStateTransition.Lock,
        SessionSwitchReason.SessionUnlock => SessionStateTransition.Unlock,
        SessionSwitchReason.ConsoleConnect => SessionStateTransition.ConsoleConnect,
        SessionSwitchReason.ConsoleDisconnect => SessionStateTransition.ConsoleDisconnect,
        SessionSwitchReason.RemoteConnect => SessionStateTransition.RemoteConnect,
        SessionSwitchReason.RemoteDisconnect => SessionStateTransition.RemoteDisconnect,
        SessionSwitchReason.SessionLogon => SessionStateTransition.Logon,
        SessionSwitchReason.SessionLogoff => SessionStateTransition.Logoff,
        _ => SessionStateTransition.Ignored,
    };

    internal void Handle(SessionSwitchReason reason)
    {
        SessionStateTransition transition = Map(reason);
        if (transition == SessionStateTransition.Ignored) return;

        bool wasInactive;
        bool isInactive;
        lock (_gate)
        {
            wasInactive = _locked || _disconnected || _loggedOff;
            switch (transition)
            {
                case SessionStateTransition.Lock:
                    _locked = true;
                    break;
                case SessionStateTransition.Unlock:
                    _locked = false;
                    break;
                case SessionStateTransition.ConsoleDisconnect:
                case SessionStateTransition.RemoteDisconnect:
                    _disconnected = true;
                    break;
                case SessionStateTransition.ConsoleConnect:
                case SessionStateTransition.RemoteConnect:
                    _disconnected = false;
                    break;
                case SessionStateTransition.Logoff:
                    _loggedOff = true;
                    break;
                case SessionStateTransition.Logon:
                    _loggedOff = false;
                    break;
            }
            isInactive = _locked || _disconnected || _loggedOff;
        }

        ApplyTransition(wasInactive, isInactive);
    }

    /// <summary>
    /// A window of this app was activated, so the session is certainly unlocked and
    /// connected. Heals a missed Unlock/Connect event that would otherwise keep visual
    /// sampling suspended until the next lock cycle.
    /// </summary>
    public void MarkInteractive()
    {
        bool wasInactive;
        lock (_gate)
        {
            wasInactive = _locked || _disconnected || _loggedOff;
            if (!wasInactive) return;
            _locked = false;
            _disconnected = false;
            _loggedOff = false;
        }

        Logger.Warn("Session was marked inactive while an app window was activated; restoring active state.");
        ApplyTransition(wasInactive, isInactive: false);
    }

    private void ApplyTransition(bool wasInactive, bool isInactive)
    {
        if (wasInactive == isInactive) return;
        try { _setSamplingInactive(isInactive); }
        catch (Exception ex) { Logger.Warn("Session sampling-state update failed: " + ex.Message); }

        if (!isInactive)
        {
            try { _signalResumeRecovery(); }
            catch (Exception ex) { Logger.Warn("Session resume recovery scheduling failed: " + ex.Message); }
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try { Handle(e.Reason); }
        catch (Exception ex) { Logger.Warn("Session-switch handling failed: " + ex.Message); }
    }
}
