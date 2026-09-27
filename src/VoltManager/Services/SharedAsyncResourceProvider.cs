namespace VoltManager.Services;

internal sealed class SharedAsyncResourceProvider<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly Func<Task<T>> _factory;
    private Task<T>? _current;

    public SharedAsyncResourceProvider(Func<Task<T>> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public Task<T> GetCurrent()
    {
        lock (_gate)
            return _current ??= Create();
    }

    /// <summary>Current resource without creating one (diagnostics must not start it).</summary>
    public Task<T>? PeekCurrent()
    {
        lock (_gate)
            return _current;
    }

    public Task<T> ReplaceAfterFailure(Task<T> failedResource)
    {
        ArgumentNullException.ThrowIfNull(failedResource);
        lock (_gate)
        {
            _current ??= Create();
            // A replacement whose creation itself failed is replaced too; a healthy one is shared.
            if (ReferenceEquals(_current, failedResource) || _current.IsFaulted || _current.IsCanceled)
                _current = Create();
            return _current;
        }
    }

    private Task<T> Create()
    {
        try { return _factory(); }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }
}
