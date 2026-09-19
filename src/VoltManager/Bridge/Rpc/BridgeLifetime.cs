namespace VoltManager.Bridge.Rpc;

public sealed class BridgeLifetime : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly List<Action> _detachCallbacks = [];
    private bool _attachBegun;
    private bool _disposed;

    public BridgeLifetime()
    {
        _token = _cancellation.Token;
    }

    public CancellationToken Token => _token;

    public bool TryBeginAttach()
    {
        lock (_gate)
        {
            if (_disposed || _attachBegun)
                return false;

            _attachBegun = true;
            return true;
        }
    }

    public void RegisterDetach(Action detach)
    {
        ArgumentNullException.ThrowIfNull(detach);

        bool runImmediately;
        lock (_gate)
        {
            runImmediately = _disposed;
            if (!runImmediately)
                _detachCallbacks.Add(detach);
        }

        if (runImmediately)
            SafeDetach(detach);
    }

    public void Dispose()
    {
        Action[] callbacks;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            callbacks = _detachCallbacks.ToArray();
            _detachCallbacks.Clear();
        }

        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        for (int i = callbacks.Length - 1; i >= 0; i--)
            SafeDetach(callbacks[i]);

        _cancellation.Dispose();
    }

    private static void SafeDetach(Action detach)
    {
        try { detach(); }
        catch { /* teardown is best-effort */ }
    }
}
