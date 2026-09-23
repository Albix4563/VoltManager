namespace VoltManager.Services;

/// <summary>Shares an in-progress asynchronous operation while allowing later retries.</summary>
internal sealed class AsyncSingleFlight
{
    private readonly object _gate = new();
    private Task? _running;

    public Task Run(Func<Task> operation)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
                return _running;

            _running = operation();
            return _running;
        }
    }
}
