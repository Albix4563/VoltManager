using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace VoltManager.Services;

/// <summary>
/// Listens for plan commands signalled by the non-elevated jump-list helper
/// (VoltManagerPlanSwitch.exe). One named auto-reset event per command key;
/// the DACL must grant Modify to authenticated users explicitly because this
/// process runs elevated while the helper does not.
/// </summary>
public sealed class RemoteCommandService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<(EventWaitHandle Event, RegisteredWaitHandle Wait)> _waits = new();
    private bool _disposed;

    /// <summary>Fired on a thread-pool thread with the received command key.</summary>
    public event Action<string>? CommandReceived;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RemoteCommandService));
            if (_waits.Count != 0)
                return;

        foreach (string key in RemoteCommandProtocol.AllKeys)
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                EventWaitHandleRights.FullControl,
                AccessControlType.Allow));

            var evt = EventWaitHandleAcl.Create(
                false, EventResetMode.AutoReset,
                ValidationEnvironment.NamedObject(RemoteCommandProtocol.EventName(key)), out _, security);

            string captured = key;
            var wait = ThreadPool.RegisterWaitForSingleObject(
                evt, (_, _) => CommandReceived?.Invoke(captured), null, -1, false);
            _waits.Add((evt, wait));
        }
        }
    }

    public void Stop()
    {
        (EventWaitHandle Event, RegisteredWaitHandle Wait)[] registrations;
        lock (_gate)
        {
            registrations = _waits.ToArray();
            _waits.Clear();
        }

        foreach (var (evt, wait) in registrations)
        {
            try { wait.Unregister(null); } catch { }
            evt.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
            _disposed = true;
    }
}
