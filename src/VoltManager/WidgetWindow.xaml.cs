using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Microsoft.Web.WebView2.Core;
using VoltManager.Bridge;
using VoltManager.Models;
using VoltManager.Performance;
using VoltManager.Services;

namespace VoltManager;

public partial class WidgetWindow : Window
{
    private const int WmNcLButtonDown = 0xA1;
    private const int WmExitSizeMove = 0x0232;
    private static readonly IntPtr HtCaption = new(0x2);

    // WS_EX_TOOLWINDOW excludes this window from the Alt+Tab switcher and taskbar
    // (in combination with WindowStyle=None + ShowInTaskbar=False in XAML).
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const int NativeBoundsTolerancePx = 2;

    private readonly WidgetRuntimeContext _context;
    private readonly WidgetManager _manager;
    private readonly Task<CoreWebView2Environment> _envTask;
    private readonly string _type;
    private HostBridge? _bridge;
    private string _size;
    private HwndSource? _hwndSource;
    private bool _applyingPlacement;
    private int _rendererReloadCount;
    private bool _initializing;
    private volatile bool _closed;
    private volatile bool _visible;
    private bool _fullscreenCovered;
    private IntPtr _coverageHwnd;
    private int _suspendRunning;
    private int _suspendGeneration;
    private readonly UiMetricsPublisher _metricsPublisher = new();
    private readonly WebViewResourceController _resourceController = new();
    private static readonly string DocumentVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    internal WidgetWindow(WidgetRuntimeContext context, WidgetManager manager, WidgetItem item,
        Task<CoreWebView2Environment> envTask, Size size, WidgetPlacement placement)
    {
        _context = context;
        _manager = manager;
        _envTask = envTask;
        _type = item.Type;
        _size = WidgetSettings.NormalizeSize(item.Size);

        InitializeComponent();

        Width = size.Width;
        Height = size.Height;
        // Temporary DIP position; ApplyPlacement will set physical coords once HWND exists.
        Left = placement.FinalBounds.X;
        Top = placement.FinalBounds.Y;
        Topmost = item.Pinned;

        Loaded += async (_, _) => await InitWebViewAsync();
        IsVisibleChanged += (_, _) =>
        {
            _visible = IsVisible;
            ApplyEffectiveWebViewVisibility();
            if (HasVisibleResourceSurface) _metricsPublisher.ResetCadence();
            _context.RefreshSamplingDemand(false);
        };
        SourceInitialized += (_, _) =>
        {
            ApplyToolWindowStyle();
            HookWndProc();
            ApplyPlacement(placement, item.Size);
            ApplyRoundedRegion();
            _coverageHwnd = new WindowInteropHelper(this).Handle;
            _context.FullscreenCoverage.RegisterSurface(_coverageHwnd);
        };
        DpiChanged += (_, _) =>
        {
            ApplyRoundedRegion();
            _manager.RequestRelayout();
        };
        _context.FullscreenCoverage.CoverageChanged += OnFullscreenCoverageChanged;
    }

    internal bool HasVisibleResourceSurface => _visible && !_fullscreenCovered && !_closed;

    private async Task InitWebViewAsync()
    {
        if (_closed || _initializing) return;
        _initializing = true;
        try
        {
            WebView.DefaultBackgroundColor = Drawing.Color.FromArgb(255, 14, 26, 46);
            await WebView.EnsureCoreWebView2Async(await _envTask);
            if (_closed) return;
        }
        catch (Exception ex)
        {
            Logger.Error("Widget WebView2 initialization failed", ex);
            if (!_closed) Close();
            return;
        }

        try
        {
            var core = WebView.CoreWebView2;
            string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping("app.local", wwwroot,
                CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // Widgets are tiny surfaces — keep the renderer on a low memory target.
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }

            _bridge = _context.CreateBridge(WebView, false);
            _bridge.Attach();
            _bridge.WidgetDragRequested += BeginNativeDrag;
            _bridge.WidgetTopmostRequested += SetTopmostFromWidget;
            _bridge.WidgetCloseRequested += () => _manager.SetEnabled(_type, false);

            if (_type is "usage" or "temps") _context.Monitor.MetricsUpdated += OnMetricsUpdated;
            if (_type is "power" or "plans") _context.PowerRequests.ActivePlanChanged += OnActivePlanChanged;
            if (_type == "power") _context.PowerRequests.CpuAutomationStateChanged += OnCpuAutomationStateChanged;
            if (_type == "plans") _context.Awake.StateChanged += OnKeepAwakeStateChanged;

            core.ProcessFailed += OnWidgetProcessFailed;

            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) return;
                _metricsPublisher.ResetCadence();
                OnMetricsUpdated(_context.Monitor.Latest);
                if (_type is "power" or "plans") OnActivePlanChanged(_context.PowerRequests.ActivePlan);
                if (_type == "power") OnCpuAutomationStateChanged(_context.PowerRequests.CpuAutomationState);
                if (_type == "plans") OnKeepAwakeStateChanged(_context.Awake.GetState());
                // Initialize this document only: broadcasting on every widget load was O(n²).
                _bridge?.PushEvent(BridgeEventNames.ThemeChanged, _context.Theme.GetWebTheme());
                _bridge?.PushEvent(BridgeEventNames.LanguageChanged, new { language = _context.Loc.CurrentLanguage, locale = _context.Loc.CurrentCulture.Name });
                _bridge?.PushEvent(BridgeEventNames.FontChanged, new { font = _context.Settings.Current.Font });
                PushResourceProfile(_context.ResourcePressureState());
                // Navigation resumes WebView2 even when coverage was detected before initialization.
                if (!HasVisibleResourceSurface) TrySuspendWebView();
            };

            core.Navigate(WidgetUrl());
        }
        catch (Exception ex)
        {
            Logger.Error("Widget WebView setup failed", ex);
            if (!_closed) Close();
        }
    }

    /// <summary>
    /// Widgets share one renderer with the dashboard (--process-per-site), so a renderer
    /// death would leave every widget blank instead of just one. Re-navigate to self-heal,
    /// capped so a renderer that keeps dying cannot spin forever.
    /// </summary>
    private void OnWidgetProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Logger.Warn($"Widget '{_type}' WebView2 process failed: {e.ProcessFailedKind} (reason: {e.Reason})");
        if (Interlocked.Increment(ref _rendererReloadCount) > 5)
        {
            Logger.Error($"Widget '{_type}' renderer kept failing; giving up auto-reload.");
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            try { WebView.CoreWebView2?.Navigate(WidgetUrl()); }
            catch (Exception ex) { Logger.Warn("Widget reload after process failure failed: " + ex.Message); }
        });
    }

    public void PushEvent(string name, object data) => _bridge?.PushEvent(name, data);

    public void ApplyPlacement(WidgetPlacement placement, string sizeKey)
    {
        string normalized = WidgetSettings.NormalizeSize(sizeKey);
        bool sizeChanged = !string.Equals(_size, normalized, StringComparison.OrdinalIgnoreCase);
        _size = normalized;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Width = placement.FinalBounds.Width;
            Height = placement.FinalBounds.Height;
            Left = placement.FinalBounds.X;
            Top = placement.FinalBounds.Y;
            return;
        }

        _applyingPlacement = true;
        try
        {
            int x = (int)Math.Round(placement.FinalBounds.X);
            int y = (int)Math.Round(placement.FinalBounds.Y);
            int w = (int)Math.Round(placement.FinalBounds.Width);
            int h = (int)Math.Round(placement.FinalBounds.Height);
            ApplyNativeBounds(hwnd, x, y, w, h);
            ApplyRoundedRegion();
            if (sizeChanged)
                WebView.CoreWebView2?.Navigate(WidgetUrl());
        }
        finally
        {
            _applyingPlacement = false;
        }
    }

    private void ApplyNativeBounds(IntPtr hwnd, int x, int y, int width, int height)
    {
        RECT actual = default;
        bool positioned = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            SwpNoActivate | SwpNoZOrder);
        int setWindowPosError = positioned ? 0 : Marshal.GetLastWin32Error();

        if (!positioned || !GetWindowRect(hwnd, out actual) ||
            !NativeBoundsMatch(actual, x, y, width, height))
        {
            // Some Windows/display-driver combinations can accept SetWindowPos without the
            // HWND ending up at the requested geometry. Verify the postcondition and use a
            // second Win32 path instead of silently keeping stale widget bounds.
            bool moved = MoveWindow(hwnd, x, y, width, height, true);
            int moveWindowError = moved ? 0 : Marshal.GetLastWin32Error();

            if (!moved || !GetWindowRect(hwnd, out actual) ||
                !NativeBoundsMatch(actual, x, y, width, height))
            {
                Logger.Warn(
                    $"Widget '{_type}' native placement failed. " +
                    $"Requested=({x},{y},{width},{height}), " +
                    $"Actual=({actual.Left},{actual.Top},{actual.Right - actual.Left},{actual.Bottom - actual.Top}), " +
                    $"SetWindowPosError={setWindowPosError}, MoveWindowError={moveWindowError}.");
            }
            else
            {
                Logger.Warn(
                    $"Widget '{_type}' placement required MoveWindow fallback " +
                    $"(SetWindowPosError={setWindowPosError}).");
            }
        }
    }

    private static bool NativeBoundsMatch(RECT rect, int x, int y, int width, int height)
        => Math.Abs(rect.Left - x) <= NativeBoundsTolerancePx
            && Math.Abs(rect.Top - y) <= NativeBoundsTolerancePx
            && Math.Abs((rect.Right - rect.Left) - width) <= NativeBoundsTolerancePx
            && Math.Abs((rect.Bottom - rect.Top) - height) <= NativeBoundsTolerancePx;

    private string WidgetUrl() =>
        "https://app.local/widgets.html?w=" + Uri.EscapeDataString(_type) +
        "&s=" + Uri.EscapeDataString(_size) +
        "&v=" + DocumentVersion;

    private void OnMetricsUpdated(MetricsSnapshot metrics)
    {
        if (_closed || !HasVisibleResourceSurface || _type is not ("usage" or "temps")) return;
        var plan = _resourceController.Resolve(
            _context.ResourcePressureState().Profile,
            visible: true,
            active: false);
        if (_metricsPublisher.TryTake(metrics, plan, DateTime.UtcNow, out var latest) && latest != null)
            _bridge?.PushEvent(BridgeEventNames.Metrics, MetricsPayload(_type, latest)!);
    }

    internal static object? MetricsPayload(string type, MetricsSnapshot metrics) => type switch
    {
        "usage" => new { cpu = metrics.Cpu, gpu = metrics.Gpu, gpuAvailable = metrics.GpuAvailable,
            ramPct = metrics.RamPct, disk = metrics.Disk },
        "temps" => new { cpuTemp = metrics.CpuTemp, gpuTemp = metrics.GpuTemp },
        _ => null,
    };

    internal void PushResourceProfile(ResourcePressureState state)
    {
        if (_closed) return;
        _metricsPublisher.ResetCadence();
        var plan = _resourceController.Resolve(state.Profile, HasVisibleResourceSurface, active: false);
        _bridge?.PushEvent(BridgeEventNames.ResourceProfileChanged, new
        {
            profile = state.Profile.ToString().ToLowerInvariant(),
            reason = state.Reason,
            protectedWorkloadActive = state.ProtectedWorkloadActive,
            reducedEffects = plan.ReducedEffects,
            metricsIntervalMs = plan.PublishMetrics ? (int)plan.MetricsInterval.TotalMilliseconds : 0,
        });
    }

    private void OnFullscreenCoverageChanged(IntPtr hwnd, bool covered)
    {
        if (hwnd != _coverageHwnd || _closed) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_closed || hwnd != _coverageHwnd) return;
            _fullscreenCovered = covered;
            ApplyEffectiveWebViewVisibility();
            _context.RefreshSamplingDemand(!covered);
        });
    }

    private void ApplyEffectiveWebViewVisibility()
    {
        if (_closed) return;
        bool active = HasVisibleResourceSurface;
        WebView.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        if (!active)
        {
            TrySuspendWebView();
            return;
        }
        ResumeWebView();
    }

    private async void TrySuspendWebView()
    {
        if (Interlocked.Exchange(ref _suspendRunning, 1) != 0) return;
        int generation = Interlocked.Increment(ref _suspendGeneration);
        try
        {
            CoreWebView2? core = WebView.CoreWebView2;
            if (core == null || HasVisibleResourceSurface) return;
            WebView.Visibility = Visibility.Hidden;
            bool suspended = await core.TrySuspendAsync();
            if (!suspended && !HasVisibleResourceSurface)
                Logger.Info($"WebView2 declined suspension for widget '{_type}'.");
            if (HasVisibleResourceSurface && generation != Volatile.Read(ref _suspendGeneration))
                core.Resume();
        }
        catch (Exception ex) { Logger.Warn($"Widget '{_type}' suspend failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _suspendRunning, 0); }
    }

    private void ResumeWebView()
    {
        if (!HasVisibleResourceSurface) return;
        Interlocked.Increment(ref _suspendGeneration);
        try
        {
            WebView.Visibility = Visibility.Visible;
            WebView.CoreWebView2?.Resume();
            _metricsPublisher.ResetCadence();
            OnMetricsUpdated(_context.Monitor.Latest);
            PushResourceProfile(_context.ResourcePressureState());
            if (_type is "power" or "plans") OnActivePlanChanged(_context.PowerRequests.ActivePlan);
            if (_type == "power") OnCpuAutomationStateChanged(_context.PowerRequests.CpuAutomationState);
            if (_type == "plans") OnKeepAwakeStateChanged(_context.Awake.GetState());
        }
        catch (Exception ex) { Logger.Warn($"Widget '{_type}' resume failed: " + ex.Message); }
    }

    private void OnCpuAutomationStateChanged(CpuAutomationState state)
    {
        if (HasVisibleResourceSurface) _bridge?.PushEvent(BridgeEventNames.CpuAutomationStateChanged, state);
    }

    private void OnActivePlanChanged(PowerPlan? plan)
        => _bridge?.PushEvent(BridgeEventNames.ActivePlanChanged, new { plan = plan?.PlanId, guid = plan?.Guid, name = plan?.Name });

    private void OnKeepAwakeStateChanged(KeepAwakeState state)
        => _bridge?.PushEvent(BridgeEventNames.KeepAwakeChanged, state);

    private void SetTopmostFromWidget(bool topmost)
    {
        Topmost = topmost;
        _manager.SetPinned(_type, topmost);
        _bridge?.PushEvent(BridgeEventNames.WidgetTopmostChanged, new { topmost });
    }

    private void BeginNativeDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    private void HookWndProc()
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmExitSizeMove && !_applyingPlacement && GetWindowRect(hwnd, out var rect))
        {
            _manager.SaveDragOffset(_type,
                new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        }
        return IntPtr.Zero;
    }

    // Rounded window corners without per-pixel transparency: WebView2 renders black
    // under a layered (AllowsTransparency) window, so we keep the window opaque and
    // clip it to a rounded region that matches the card's 18px CSS border-radius.
    private void ApplyRoundedRegion()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.Handle == IntPtr.Zero)
            return;

        var m = source.CompositionTarget.TransformToDevice;
        int w = (int)Math.Round(ActualWidth > 0 ? ActualWidth * m.M11 : Width * m.M11);
        int h = (int)Math.Round(ActualHeight > 0 ? ActualHeight * m.M22 : Height * m.M22);
        if (w <= 0 || h <= 0) return;
        int d = (int)Math.Round(18 * 2 * m.M11); // diameter = 2 × 18px radius
        SetWindowRgn(source.Handle, CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d), true);
    }

    private void ApplyToolWindowStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _visible = false;
        try { _context.FullscreenCoverage.CoverageChanged -= OnFullscreenCoverageChanged; } catch { }
        try { _context.FullscreenCoverage.UnregisterSurface(_coverageHwnd); } catch { }
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _context.Monitor.MetricsUpdated -= OnMetricsUpdated;
        _context.PowerRequests.ActivePlanChanged -= OnActivePlanChanged;
        _context.PowerRequests.CpuAutomationStateChanged -= OnCpuAutomationStateChanged;
        _context.Awake.StateChanged -= OnKeepAwakeStateChanged;
        _bridge?.Dispose();
        _bridge = null;
        try { WebView.Dispose(); }
        catch (Exception ex) { Logger.Warn("Widget WebView disposal failed: " + ex.Message); }
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y,
        int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
