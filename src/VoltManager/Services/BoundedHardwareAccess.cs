namespace VoltManager.Services;

internal sealed class BoundedHardwareAccess : IHardwareAccess
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly object _stateGate = new();
    private readonly IHardwareAccess _inner;
    private readonly IBoundedOperationExecutor _executor;
    private readonly TimeSpan _operationTimeout;
    private SensorReport _last = SensorReport.Empty;
    private bool _operationFaulted;
    private int _disposed;

    public BoundedHardwareAccess(IHardwareAccess inner)
        : this(inner, new ThreadPoolBoundedOperationExecutor(1), DefaultOperationTimeout)
    {
    }

    internal BoundedHardwareAccess(
        IHardwareAccess inner,
        IBoundedOperationExecutor executor,
        TimeSpan operationTimeout)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _operationTimeout = operationTimeout;
    }

    public bool Available => Volatile.Read(ref _disposed) == 0 && _inner.Available;

    public SensorReport Read(bool force = false)
        => Read(HardwareSampleRequest.Full, force);

    public SensorReport Read(HardwareSampleRequest request, bool force = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Last;

        BoundedOperationResult<SensorReport> result = _executor.Run(
            () => _inner.Read(request, force),
            _operationTimeout);
        if (result.Completed && result.Value != null)
        {
            lock (_stateGate)
                _last = result.Value;
            _operationFaulted = false;
            return result.Value;
        }

        LogFailure(result, "Hardware sensor read", _operationTimeout);
        return Last;
    }

    public void Invalidate()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        BoundedOperationResult<bool> result = _executor.Run(() =>
        {
            _inner.Invalidate();
            return true;
        }, _operationTimeout);
        if (result.Completed)
            _operationFaulted = false;
        else
            LogFailure(result, "Hardware sensor invalidate", _operationTimeout);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        BoundedOperationResult<bool> result = _executor.Run(() =>
        {
            _inner.Dispose();
            return true;
        }, DisposeTimeout);
        if (!result.Completed)
            LogFailure(result, "Hardware sensor disposal", DisposeTimeout);
    }

    private SensorReport Last
    {
        get
        {
            lock (_stateGate)
                return _last;
        }
    }

    private void LogFailure<T>(BoundedOperationResult<T> result, string operation, TimeSpan timeout)
    {
        Exception? error = result.Error;
        if (result.TimedOut)
            error = new TimeoutException($"{operation} exceeded {timeout.TotalSeconds:F0}s.");
        _operationFaulted = Logger.WarnOnce(_operationFaulted, operation + " failed", error);
    }
}
