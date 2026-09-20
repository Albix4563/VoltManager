namespace VoltManager.Tests;

internal sealed class CallbackDisposable : IDisposable
{
    private readonly Action? _onDispose;
    private int _disposed;

    public CallbackDisposable(Action? onCreate = null, Action? onDispose = null)
    {
        _onDispose = onDispose;
        onCreate?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _onDispose?.Invoke();
    }
}
