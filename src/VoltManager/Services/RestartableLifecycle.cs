namespace VoltManager.Services;

internal sealed class RestartableLifecycle : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _epoch;
    private bool _disposed;

    public bool IsStarted
    {
        get
        {
            lock (_gate)
                return _epoch is { IsCancellationRequested: false };
        }
    }

    public CancellationToken Start()
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RestartableLifecycle));

            if (_epoch is { IsCancellationRequested: false })
                return _epoch.Token;

            _epoch?.Dispose();
            _epoch = new CancellationTokenSource();
            return _epoch.Token;
        }
    }

    public bool Stop()
    {
        CancellationTokenSource? epoch;
        lock (_gate)
        {
            if (_epoch is null || _epoch.IsCancellationRequested)
                return false;

            epoch = _epoch;
            _epoch = null;
        }

        epoch.Cancel();
        epoch.Dispose();
        return true;
    }

    public bool IsCurrent(CancellationToken token)
    {
        lock (_gate)
        {
            return !_disposed &&
                   _epoch is not null &&
                   !_epoch.IsCancellationRequested &&
                   _epoch.Token == token;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        Stop();
    }
}
