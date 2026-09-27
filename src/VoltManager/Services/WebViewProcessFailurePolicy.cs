using Microsoft.Web.WebView2.Core;

namespace VoltManager.Services;

internal enum WebViewProcessFailureAction
{
    RecoverBrowser,
    ReloadRenderer,
    ReloadRendererOnce,
    LogOnly,
}

internal static class WebViewProcessFailureClassifier
{
    public static WebViewProcessFailureAction Classify(
        CoreWebView2ProcessFailedKind kind,
        CoreWebView2ProcessFailedReason reason)
    {
        _ = reason;
        return kind switch
        {
            CoreWebView2ProcessFailedKind.BrowserProcessExited => WebViewProcessFailureAction.RecoverBrowser,
            CoreWebView2ProcessFailedKind.RenderProcessExited => WebViewProcessFailureAction.ReloadRenderer,
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => WebViewProcessFailureAction.ReloadRendererOnce,
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited => WebViewProcessFailureAction.LogOnly,
            CoreWebView2ProcessFailedKind.GpuProcessExited => WebViewProcessFailureAction.LogOnly,
            CoreWebView2ProcessFailedKind.UtilityProcessExited => WebViewProcessFailureAction.LogOnly,
            CoreWebView2ProcessFailedKind.SandboxHelperProcessExited => WebViewProcessFailureAction.LogOnly,
            CoreWebView2ProcessFailedKind.PpapiPluginProcessExited => WebViewProcessFailureAction.LogOnly,
            CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited => WebViewProcessFailureAction.LogOnly,
            CoreWebView2ProcessFailedKind.UnknownProcessExited => WebViewProcessFailureAction.LogOnly,
            _ => WebViewProcessFailureAction.LogOnly,
        };
    }
}

internal sealed class WebViewFailureBudget
{
    private readonly object _gate = new();
    private readonly Func<long> _nowMilliseconds;
    private readonly int _rendererReloadLimit;
    private readonly long _rendererFailureWindowMilliseconds;
    private int _rendererReloadCount;
    private long _lastRendererFailureMilliseconds = long.MinValue / 2;
    private bool _unresponsiveReloadUsed;

    public WebViewFailureBudget(
        int rendererReloadLimit = 5,
        TimeSpan? rendererFailureWindow = null,
        Func<long>? nowMilliseconds = null)
    {
        if (rendererReloadLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(rendererReloadLimit));
        _rendererReloadLimit = rendererReloadLimit;
        _rendererFailureWindowMilliseconds = (long)(rendererFailureWindow ?? TimeSpan.FromMinutes(2)).TotalMilliseconds;
        _nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);
    }

    public bool TryTakeRendererReload()
    {
        lock (_gate)
        {
            long now = _nowMilliseconds();
            if (now - _lastRendererFailureMilliseconds > _rendererFailureWindowMilliseconds)
                _rendererReloadCount = 0;
            _lastRendererFailureMilliseconds = now;
            _rendererReloadCount++;
            return _rendererReloadCount <= _rendererReloadLimit;
        }
    }

    public void ResetRendererReloadBudget()
    {
        lock (_gate)
        {
            _rendererReloadCount = 0;
            _lastRendererFailureMilliseconds = long.MinValue / 2;
        }
    }

    public bool TryTakeUnresponsiveReload()
    {
        lock (_gate)
        {
            if (_unresponsiveReloadUsed)
                return false;
            _unresponsiveReloadUsed = true;
            return true;
        }
    }
}

internal readonly record struct FailureWindowObservation(int Count, bool ThresholdReachedNow);

internal sealed class SlidingFailureWindow
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _window;
    private readonly int _threshold;
    private bool _thresholdReported;

    public SlidingFailureWindow(
        int threshold,
        TimeSpan window,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _threshold = threshold;
        _window = window;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public FailureWindowObservation Record()
    {
        lock (_gate)
        {
            DateTimeOffset now = _utcNow();
            DateTimeOffset cutoff = now - _window;
            while (_failures.Count > 0 && _failures.Peek() < cutoff)
                _failures.Dequeue();
            if (_failures.Count < _threshold)
                _thresholdReported = false;

            _failures.Enqueue(now);
            bool reachedNow = _failures.Count >= _threshold && !_thresholdReported;
            if (reachedNow) _thresholdReported = true;
            return new FailureWindowObservation(_failures.Count, reachedNow);
        }
    }
}

internal static class WebViewSoftwareRendererFallback
{
    private static int _requested;

    public static bool IsRequested => Volatile.Read(ref _requested) != 0;

    public static bool Request()
        => Interlocked.Exchange(ref _requested, 1) == 0;

    internal static void ResetForTests()
        => Volatile.Write(ref _requested, 0);
}
