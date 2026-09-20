using System.Runtime.InteropServices;
using VoltManager.Services.GameDetection;

namespace VoltManager.Services;

/// <summary>
/// Tracks whether one of VoltManager's native surfaces is actually behind a protected
/// fullscreen window. EnumWindows already yields top-level windows in Z order, so the
/// decision can stay per-surface/per-monitor without treating focus loss as coverage.
/// </summary>
public sealed class ProtectedFullscreenCoverageService : IDisposable
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const int DwmwaCloaked = 14;
    private static readonly TimeSpan ScanFallbackInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EventCoalesceDelay = TimeSpan.FromMilliseconds(150);

    private readonly Func<IReadOnlySet<int>> _protectedPids;
    private readonly object _gate = new();
    private readonly HashSet<IntPtr> _surfaces = new();
    private readonly Dictionary<IntPtr, bool> _coverage = new();
    private readonly Timer _coalesceTimer;
    private readonly Timer _fallbackTimer;
    private readonly WinEventDelegate _winEventDelegate;
    private readonly List<IntPtr> _hooks = new();
    private int _scanQueued;
    private int _scanRunning;
    private bool _disposed;

    public event Action<IntPtr, bool>? CoverageChanged;

    public ProtectedFullscreenCoverageService(Func<IReadOnlySet<int>> protectedPids)
    {
        _protectedPids = protectedPids;
        _winEventDelegate = OnWinEvent;
        _coalesceTimer = new Timer(_ => RunQueuedScan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _fallbackTimer = new Timer(_ => QueueScan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_hooks.Count != 0) return;
            AddHook(EventSystemForeground);
            AddHook(EventObjectShow);
            AddHook(EventObjectHide);
            AddHook(EventObjectLocationChange);
            _fallbackTimer.Change(ScanFallbackInterval, ScanFallbackInterval);
        }
        QueueScan();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _coalesceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _fallbackTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            foreach (IntPtr hook in _hooks)
            {
                try { UnhookWinEvent(hook); } catch { }
            }
            _hooks.Clear();
            Interlocked.Exchange(ref _scanQueued, 0);
        }
    }

    public void RegisterSurface(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _disposed) return;
        lock (_gate)
        {
            _surfaces.Add(hwnd);
            _coverage.TryAdd(hwnd, false);
        }
        QueueScan();
    }

    public void UnregisterSurface(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (_gate)
        {
            _surfaces.Remove(hwnd);
            _coverage.Remove(hwnd);
        }
    }

    public bool IsCovered(IntPtr hwnd)
    {
        lock (_gate) return _coverage.TryGetValue(hwnd, out bool covered) && covered;
    }

    public void NotifyProtectedProcessesChanged() => QueueScan();

    public void QueueScan()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _scanQueued, 1) != 0) return;
            _coalesceTimer.Change(EventCoalesceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void RunQueuedScan()
    {
        Interlocked.Exchange(ref _scanQueued, 0);
        if (_disposed || Interlocked.Exchange(ref _scanRunning, 1) != 0) return;
        try { Scan(); }
        catch (Exception ex) { Logger.Warn("Protected fullscreen coverage scan failed: " + ex.Message); }
        finally { Volatile.Write(ref _scanRunning, 0); }
    }

    private void Scan()
    {
        IntPtr[] surfaces;
        lock (_gate) surfaces = _surfaces.ToArray();
        if (surfaces.Length == 0) return;

        IReadOnlySet<int> protectedPids;
        try { protectedPids = _protectedPids(); }
        catch { protectedPids = new HashSet<int>(); }

        var windows = protectedPids.Count == 0 ? new List<CoverageWindow>() : CaptureTopLevelWindows();
        foreach (IntPtr surface in surfaces)
        {
            bool covered = protectedPids.Count != 0 && IsSurfaceCovered(surface, windows, protectedPids);
            bool changed;
            lock (_gate)
            {
                if (!_surfaces.Contains(surface)) continue;
                changed = !_coverage.TryGetValue(surface, out bool old) || old != covered;
                _coverage[surface] = covered;
            }
            if (changed) CoverageChanged?.Invoke(surface, covered);
        }
    }

    internal static bool IsSurfaceCovered(
        IntPtr surface,
        IReadOnlyList<CoverageWindow> windows,
        IReadOnlySet<int> protectedPids)
    {
        CoverageWindow? target = windows.FirstOrDefault(window => window.Hwnd == surface);
        if (target == null || !target.Visible || target.Minimized || target.Cloaked || target.Monitor == IntPtr.Zero)
            return false;

        foreach (var window in windows)
        {
            if (window.ZOrder >= target.ZOrder) break;
            if (!protectedPids.Contains(window.ProcessId)) continue;
            if (!window.Visible || window.Minimized || window.Cloaked) continue;
            if (window.Monitor != target.Monitor) continue;
            // A surface spanning monitors may still have visible content on the other display.
            if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0 ||
                window.Bounds.X > target.Bounds.X || window.Bounds.Y > target.Bounds.Y ||
                window.Bounds.X + window.Bounds.Width < target.Bounds.X + target.Bounds.Width ||
                window.Bounds.Y + window.Bounds.Height < target.Bounds.Y + target.Bounds.Height)
                continue;
            if (!ForegroundProcessProbe.IsNearFullscreenRect(
                    (int)window.Bounds.X,
                    (int)window.Bounds.Y,
                    (int)(window.Bounds.X + window.Bounds.Width),
                    (int)(window.Bounds.Y + window.Bounds.Height),
                    (int)window.MonitorBounds.X,
                    (int)window.MonitorBounds.Y,
                    (int)(window.MonitorBounds.X + window.MonitorBounds.Width),
                    (int)(window.MonitorBounds.Y + window.MonitorBounds.Height)))
                continue;
            return true;
        }
        return false;
    }

    private static List<CoverageWindow> CaptureTopLevelWindows()
    {
        var windows = new List<CoverageWindow>();
        int z = 0;
        EnumWindows((hwnd, _) =>
        {
            int currentZ = z++;
            bool visible = IsWindowVisible(hwnd);
            bool minimized = IsIconic(hwnd);
            bool cloaked = IsCloaked(hwnd);
            GetWindowThreadProcessId(hwnd, out uint pid);

            PixelRect bounds = default;
            if (GetWindowRect(hwnd, out RECT rect))
                bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            PixelRect monitorBounds = default;
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                    monitorBounds = new PixelRect(
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top);
            }

            windows.Add(new CoverageWindow(
                hwnd,
                currentZ,
                unchecked((int)pid),
                monitor,
                bounds,
                monitorBounds,
                visible,
                minimized,
                cloaked));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private void AddHook(uint eventId)
    {
        IntPtr hook = SetWinEventHook(
            eventId,
            eventId,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (hook != IntPtr.Zero) _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Ignore child controls/caret events, which can arrive continuously during rendering.
        if (eventType == EventSystemForeground || (hwnd != IntPtr.Zero && idObject == 0 && idChild == 0))
            QueueScan();
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            int cloaked = 0;
            int hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(int));
            return hr == 0 && cloaked != 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _coalesceTimer.Dispose();
            _fallbackTimer.Dispose();
            _surfaces.Clear();
            _coverage.Clear();
        }
    }

    internal sealed record CoverageWindow(
        IntPtr Hwnd,
        int ZOrder,
        int ProcessId,
        IntPtr Monitor,
        PixelRect Bounds,
        PixelRect MonitorBounds,
        bool Visible,
        bool Minimized,
        bool Cloaked);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHook,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
