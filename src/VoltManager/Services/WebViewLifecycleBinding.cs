namespace VoltManager.Services;

internal sealed class WebViewLifecycleBinding<TSource> : IDisposable
    where TSource : class
{
    private readonly Action<TSource> _attach;
    private readonly Action<TSource> _detach;
    private TSource? _source;
    private bool _disposed;

    public WebViewLifecycleBinding(Action<TSource> attach, Action<TSource> detach)
    {
        _attach = attach;
        _detach = detach;
    }

    public void Attach(TSource source)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WebViewLifecycleBinding<TSource>));
        if (ReferenceEquals(_source, source)) return;
        if (_source is not null) _detach(_source);
        _source = source;
        _attach(source);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source is not null) _detach(_source);
        _source = null;
    }
}
