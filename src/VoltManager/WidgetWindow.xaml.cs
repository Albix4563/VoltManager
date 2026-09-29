using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
    private static readonly IntPtr HtRight = new(0xB);
    private static readonly IntPtr HtBottom = new(0xF);

    // WS_EX_TOOLWINDOW excludes this window from the Alt+Tab switcher and taskbar
    // (in combination with WindowStyle=None + ShowInTaskbar=False in XAML).
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const int NativeBoundsTolerancePx = 2;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableBlurBehind = 3;
    private const int AccentEnableAcrylicBlurBehind = 4;

    private readonly WidgetRuntimeContext _context;
    private readonly WidgetManager _manager;
    private Task<CoreWebView2Environment> _envTask;
    private readonly string _type;
    private HostBridge? _bridge;
    private string _size;
    private string _orientation;
    private WidgetAppearance _appearance = new();
    private WidgetAppearance _requestedAppearance = new();
    private IntPtr _requestedAppearanceHwnd;
    private WidgetAppearance? _nativeAppearance;
    private IntPtr _nativeAppearanceHwnd;
    private HwndSource? _hwndSource;
    private bool _applyingPlacement;
    private bool _nativeResizeInProgress;
    private bool _relayoutPendingDuringNativeResize;
    private readonly WebViewFailureBudget _failureBudget = new();
    private readonly CancellationTokenSource _webViewLifetime = new();
    private Task? _browserRecoveryTask;
    private bool _hostEventsWired;
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
        _size = WidgetSettings.NormalizeSize(item.Type, item.Size);
        _orientation = WidgetSettings.IsLauncherType(item.Type)
            ? WidgetSettings.NormalizeOrientation(item.Orientation)
            : "horizontal";

        InitializeComponent();

        Width = size.Width;
        Height = size.Height;
        // Temporary DIP position; ApplyPlacement will set physical coords once HWND exists.
        Left = placement.FinalBounds.X;
        Top = placement.FinalBounds.Y;
        Topmost = item.Pinned;
        ConfigureResizeBounds(placement.EffectiveDisplay);

        Loaded += async (_, _) => await InitWebViewAsync();
        IsVisibleChanged += (_, _) =>
        {
            _visible = IsVisible;
            if (_visible) ApplyNativeAppearanceSafe();
            ApplyEffectiveWebViewVisibility();
            if (HasVisibleResourceSurface) _metricsPublisher.ResetCadence();
            _context.RefreshSamplingDemand(false);
        };
        SourceInitialized += (_, _) =>
        {
            ApplyToolWindowStyle();
            HookWndProc();
            ApplyPlacement(placement, item.Size, item.Orientation);
            ApplyRoundedRegion();
            ApplyNativeAppearanceSafe();
            _coverageHwnd = new WindowInteropHelper(this).Handle;
            _context.FullscreenCoverage.RegisterSurface(_coverageHwnd);
        };
        // Keep the rounded clip in sync while the user drags the resize grip.
        SizeChanged += (_, _) =>
        {
            if (_nativeResizeInProgress) ApplyRoundedRegion();
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
            await InitializeWebViewControlAsync(WebView, _envTask, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Error("Widget WebView2 initialization failed", ex);
            if (!_closed) Close();
        }
    }

    private async Task InitializeWebViewControlAsync(
        WebView2 webView,
        Task<CoreWebView2Environment> environmentTask,
        CancellationToken cancellationToken)
    {
        SetWebViewBackgroundForAppearance();
        CoreWebView2Environment environment = await environmentTask.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await webView.EnsureCoreWebView2Async(environment);
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed) return;

        var core = webView.CoreWebView2
            ?? throw new InvalidOperationException("Widget CoreWebView2 not ready.");
        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping("app.local", wwwroot,
            CoreWebView2HostResourceAccessKind.DenyCors);

#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        webView.AllowExternalDrop = true;
        // Widgets are tiny surfaces — keep the renderer on a low memory target.
        try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }

        _bridge?.Dispose();
        _bridge = _context.CreateBridge(webView, false, _type);
        _bridge.Attach();
        _bridge.WidgetDragRequested += BeginNativeDrag;
        _bridge.WidgetResizeRequested += BeginNativeResize;
        _bridge.WidgetTopmostRequested += SetTopmostFromWidget;
        _bridge.WidgetCloseRequested += () => _manager.SetEnabled(_type, false);
        _bridge.FreshStateRequested += PublishWidgetFreshState;

        if (!_hostEventsWired)
        {
            if (_type is "usage" or "temps") _context.Monitor.MetricsUpdated += OnMetricsUpdated;
            if (_type is "power" or "plans") _context.PowerRequests.ActivePlanChanged += OnActivePlanChanged;
            if (_type == "power") _context.PowerRequests.CpuAutomationStateChanged += OnCpuAutomationStateChanged;
            if (_type is "plans" or "actions") _context.Awake.StateChanged += OnKeepAwakeStateChanged;
            _hostEventsWired = true;
        }

        AttachWidgetCore(core);
        core.Navigate(WidgetUrl());
    }

    private void AttachWidgetCore(CoreWebView2 core)
    {
        core.ProcessFailed += OnWidgetProcessFailed;
        core.NavigationStarting += OnWidgetNavigationStarting;
        core.NewWindowRequested += OnWidgetNewWindowRequested;
        core.NavigationCompleted += OnWidgetNavigationCompleted;
    }

    private void DetachWidgetCore(CoreWebView2? core)
    {
        if (core == null) return;
        try { core.ProcessFailed -= OnWidgetProcessFailed; } catch { }
        try { core.NavigationStarting -= OnWidgetNavigationStarting; } catch { }
        try { core.NewWindowRequested -= OnWidgetNewWindowRequested; } catch { }
        try { core.NavigationCompleted -= OnWidgetNavigationCompleted; } catch { }
    }

    private void OnWidgetNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess) return;
        _metricsPublisher.ResetCadence();
        PublishWidgetFreshState();
        // Navigation resumes WebView2 even when coverage was detected before initialization.
        if (!HasVisibleResourceSurface) TrySuspendWebView();
    }

    private void PublishWidgetFreshState()
    {
        OnMetricsUpdated(_context.Monitor.Latest);
        if (_type is "power" or "plans") OnActivePlanChanged(_context.PowerRequests.ActivePlan);
        if (_type == "power") OnCpuAutomationStateChanged(_context.PowerRequests.CpuAutomationState);
        if (_type is "plans" or "actions") OnKeepAwakeStateChanged(_context.Awake.GetState());
        // Initialize this document only: broadcasting on every widget load was O(n²).
        _bridge?.PushEvent(BridgeEventNames.ThemeChanged, _context.Theme.GetWebTheme());
        _bridge?.PushEvent(BridgeEventNames.LanguageChanged, new { language = _context.Loc.CurrentLanguage, locale = _context.Loc.CurrentCulture.Name });
        _bridge?.PushEvent(BridgeEventNames.FontChanged, new { font = _context.Settings.Current.Font });
        PushAppearanceEvent();
        PushResourceProfile(_context.ResourcePressureState());
    }

    /// <summary>
    /// Widgets share one renderer with the dashboard (--process-per-site), so a renderer
    /// death would leave every widget blank instead of just one. Re-navigate to self-heal,
    /// capped so a renderer that keeps dying cannot spin forever.
    /// </summary>
    private void OnWidgetProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Logger.Warn($"Widget '{_type}' WebView2 process failed: {e.ProcessFailedKind} (reason: {e.Reason})");
        WebViewProcessFailureAction action = WebViewProcessFailureClassifier.Classify(e.ProcessFailedKind, e.Reason);
        switch (action)
        {
            case WebViewProcessFailureAction.RecoverBrowser:
                StartWidgetBrowserRecovery();
                break;
            case WebViewProcessFailureAction.ReloadRenderer:
                ReloadWidgetAfterRendererFailure(requireUnresponsiveBudget: false);
                break;
            case WebViewProcessFailureAction.ReloadRendererOnce:
                ReloadWidgetAfterRendererFailure(requireUnresponsiveBudget: true);
                break;
            case WebViewProcessFailureAction.LogOnly:
                break;
        }
    }

    private void ReloadWidgetAfterRendererFailure(bool requireUnresponsiveBudget)
    {
        if (requireUnresponsiveBudget && !_failureBudget.TryTakeUnresponsiveReload())
            return;
        if (!_failureBudget.TryTakeRendererReload())
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

    private void StartWidgetBrowserRecovery()
    {
        if (_closed || _browserRecoveryTask is { IsCompleted: false }) return;
        _browserRecoveryTask = RecoverWidgetBrowserAsync(_webViewLifetime.Token);
    }

    private async Task RecoverWidgetBrowserAsync(CancellationToken cancellationToken)
    {
        TimeSpan[] delays = [TimeSpan.Zero, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < delays.Length; attempt++)
        {
            try
            {
                if (delays[attempt] > TimeSpan.Zero)
                    await Task.Delay(delays[attempt], cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!Dispatcher.CheckAccess())
                {
                    await Dispatcher.InvokeAsync(() => ReplaceWidgetWebViewAsync(cancellationToken)).Task.Unwrap();
                }
                else
                {
                    await ReplaceWidgetWebViewAsync(cancellationToken);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                if (attempt + 1 < delays.Length)
                    Logger.Warn($"Widget '{_type}' browser recovery attempt {attempt + 1} failed: {ex.Message}");
            }
        }

        if (lastFailure != null)
            Logger.Error($"Widget '{_type}' browser recovery retry limit reached.", lastFailure);
    }

    private async Task ReplaceWidgetWebViewAsync(CancellationToken cancellationToken)
    {
        if (_closed) return;
        App app = Application.Current as App
            ?? throw new InvalidOperationException("VoltManager application is unavailable.");
        // _envTask keeps the crashed environment until recovery succeeds: retries reuse the
        // shared replacement instead of creating one environment per attempt.
        Task<CoreWebView2Environment> replacementEnvironment = app.RecoverWebViewEnvironment(_envTask);
        await replacementEnvironment.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        WebView2 previous = WebView;
        DetachWidgetCore(previous.CoreWebView2);
        _bridge?.Dispose();
        _bridge = null;
        RootGrid.Children.Remove(previous);
        try { previous.Dispose(); }
        catch (Exception ex) { Logger.Warn($"Widget '{_type}' old WebView disposal failed: {ex.Message}"); }

        var replacement = new WebView2
        {
            Visibility = HasVisibleResourceSurface ? Visibility.Visible : Visibility.Hidden,
        };
        RootGrid.Children.Add(replacement);
        WebView = replacement;
        await InitializeWebViewControlAsync(replacement, replacementEnvironment, cancellationToken);
        _envTask = replacementEnvironment;
    }

    private void OnWidgetNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (WebViewNavigationPolicy.IsAllowedTopLevelUri(e.Uri)) return;
        e.Cancel = true;
        if (WebViewNavigationPolicy.IsExternalHttpUri(e.Uri))
            WebViewNavigationPolicy.OpenExternal(e.Uri);
    }

    private void OnWidgetNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never let a popup replace the widget document; only external links leave the app.
        e.Handled = true;
        if (WebViewNavigationPolicy.IsExternalHttpUri(e.Uri))
            WebViewNavigationPolicy.OpenExternal(e.Uri);
    }

    public void PushEvent(string name, object data) => _bridge?.PushEvent(name, data);

    internal bool MatchesAppearance(WidgetAppearance appearance)
        => _requestedAppearance.ValueEquals(appearance);

    internal void ApplyAppearance(WidgetAppearance appearance)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => ApplyAppearance(appearance));
            return;
        }

        var normalized = (appearance ?? new WidgetAppearance()).CloneNormalized();
        var hwnd = (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? IntPtr.Zero;
        if (_requestedAppearanceHwnd == hwnd && _requestedAppearance.ValueEquals(normalized)) return;
        _requestedAppearance = normalized.CloneNormalized();
        _requestedAppearanceHwnd = IntPtr.Zero;
        _appearance = normalized;
        SetPresentationBackgrounds();
        ApplyNativeAppearanceSafe(pushFallbackToWeb: false);
        PushAppearanceEvent();
    }

    private void PushAppearanceEvent()
        => _bridge?.PushEvent(BridgeEventNames.WidgetAppearanceChanged, _appearance.CloneNormalized());

    private void SetWebViewBackgroundForAppearance()
    {
        WebView.DefaultBackgroundColor = string.Equals(_appearance.Material, "solid", StringComparison.OrdinalIgnoreCase)
            ? Drawing.Color.FromArgb(255, 14, 26, 46)
            : Drawing.Color.Transparent;
    }

    private void SetPresentationBackgrounds()
    {
        bool solid = string.Equals(_appearance.Material, "solid", StringComparison.OrdinalIgnoreCase);
        if (solid)
        {
            SetResourceReference(BackgroundProperty, "ThemeBackgroundBrush");
            RootGrid.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "ThemeBackgroundBrush");
        }
        else
        {
            Background = Brushes.Transparent;
            RootGrid.Background = Brushes.Transparent;
        }
        SetWebViewBackgroundForAppearance();
    }

    private void ApplyNativeAppearanceSafe(bool pushFallbackToWeb = true)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.Handle == IntPtr.Zero)
            return;
        if (_requestedAppearanceHwnd == source.Handle)
            return;

        var requested = _requestedAppearance.CloneNormalized();
        _requestedAppearanceHwnd = source.Handle;
        if (!_appearance.ValueEquals(requested))
        {
            _appearance = requested.CloneNormalized();
            SetPresentationBackgrounds();
        }

        try
        {
            if (!TryApplyNativeAppearance(source, requested.Material))
                throw new InvalidOperationException("Native material API returned failure.");
            _nativeAppearanceHwnd = source.Handle;
            _nativeAppearance = requested.CloneNormalized();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Widget '{_type}' appearance '{requested.Material}' failed; using solid: {ex.Message}");
            _appearance = new WidgetAppearance
            {
                Material = "solid",
                Tint = requested.Tint,
                Gradient = requested.Gradient,
                Intensity = requested.Intensity,
            };
            SetPresentationBackgrounds();
            try
            {
                if (TryApplyNativeAppearance(source, "solid"))
                {
                    _nativeAppearanceHwnd = source.Handle;
                    _nativeAppearance = _appearance.CloneNormalized();
                }
            }
            catch (Exception resetEx) { Logger.Warn($"Widget '{_type}' native appearance reset failed: {resetEx.Message}"); }
            if (pushFallbackToWeb) PushAppearanceEvent();
        }
    }

    private bool TryApplyNativeAppearance(HwndSource source, string material)
    {
        IntPtr hwnd = source.Handle;
        if (!TryClearNativeEffects(hwnd)) return false;

        if (string.Equals(material, "solid", StringComparison.OrdinalIgnoreCase))
        {
            source.CompositionTarget.BackgroundColor = Color.FromRgb(14, 26, 46);
            var opaqueMargins = new MARGINS();
            return DwmExtendFrameIntoClientArea(hwnd, ref opaqueMargins) == 0;
        }

        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var glassMargins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(hwnd, ref glassMargins) != 0) return false;
        // A borderless popup only gets per-pixel alpha from DWM once blur-behind is enabled;
        // an empty blur region keeps it see-through without blurring (same trick as winit/Tauri).
        // Without it transparent pixels (and the rounded corners) are composed as black.
        if (!TrySetBlurBehind(hwnd, enable: true)) return false;

        if (string.Equals(material, "transparent", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(material, "acrylic", StringComparison.OrdinalIgnoreCase))
            return false;

        int dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        // Near-zero native tint: the user-selected tint/gradient is painted by the page CSS.
        // Alpha must stay > 0, Windows 10 acrylic glitches with a fully transparent gradient.
        if (TrySetAccent(hwnd, AccentEnableAcrylicBlurBehind, 0x01000000)) return true;
        return TrySetAccent(hwnd, AccentEnableBlurBehind, 0);
    }

    private static bool TryClearNativeEffects(IntPtr hwnd)
    {
        bool accentCleared = TrySetAccent(hwnd, AccentDisabled, 0);
        return TrySetBlurBehind(hwnd, enable: false) && accentCleared;
    }

    private static bool TrySetBlurBehind(IntPtr hwnd, bool enable)
    {
        const int DwmBbEnable = 0x1;
        const int DwmBbBlurRegion = 0x2;
        IntPtr region = enable ? CreateRectRgn(0, 0, -1, -1) : IntPtr.Zero;
        try
        {
            var blur = new DWM_BLURBEHIND
            {
                dwFlags = enable ? DwmBbEnable | DwmBbBlurRegion : DwmBbEnable,
                fEnable = enable,
                hRgnBlur = region,
            };
            return DwmEnableBlurBehindWindow(hwnd, ref blur) == 0;
        }
        finally
        {
            if (region != IntPtr.Zero) DeleteObject(region);
        }
    }

    private static bool TrySetAccent(IntPtr hwnd, int state, uint gradientColor)
    {
        var policy = new ACCENT_POLICY
        {
            AccentState = state,
            AccentFlags = 2,
            GradientColor = gradientColor,
        };
        IntPtr dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
        try
        {
            Marshal.StructureToPtr(policy, dataPtr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WcaAccentPolicy,
                Data = dataPtr,
                SizeOfData = new IntPtr(Marshal.SizeOf<ACCENT_POLICY>()),
            };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
        }
    }

    public void ApplyPlacement(WidgetPlacement placement, string sizeKey, string? orientation)
    {
        string normalized = WidgetSettings.NormalizeSize(_type, sizeKey);
        bool sizeChanged = !string.Equals(_size, normalized, StringComparison.OrdinalIgnoreCase);
        string normalizedOrientation = WidgetSettings.IsLauncherType(_type)
            ? WidgetSettings.NormalizeOrientation(orientation)
            : "horizontal";
        bool orientationChanged = !string.Equals(_orientation, normalizedOrientation, StringComparison.OrdinalIgnoreCase);

        if (_nativeResizeInProgress)
        {
            _relayoutPendingDuringNativeResize = true;
            return;
        }

        _size = normalized;
        _orientation = normalizedOrientation;
        ConfigureResizeBounds(placement.EffectiveDisplay);

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
            if (sizeChanged || orientationChanged)
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
        "&o=" + Uri.EscapeDataString(_orientation) +
        "&v=" + DocumentVersion;

    private void ConfigureResizeBounds(DisplayInfo display)
    {
        if (!WidgetSettings.IsLauncherType(_type)) return;

        MinWidth = 0;
        MinHeight = 0;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;

        var range = WidgetManager.GetLauncherLengthRange(_size, _orientation, display);
        double thickness = WidgetSettings.LauncherThickness(_size);
        if (_orientation == "vertical")
        {
            MinWidth = thickness;
            MaxWidth = thickness;
            MinHeight = range.Min;
            MaxHeight = range.Max;
        }
        else
        {
            MinWidth = range.Min;
            MaxWidth = range.Max;
            MinHeight = thickness;
            MaxHeight = thickness;
        }
    }

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
            if (_type is "plans" or "actions") OnKeepAwakeStateChanged(_context.Awake.GetState());
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

    private void BeginNativeResize()
    {
        if (!WidgetSettings.IsLauncherType(_type)) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _nativeResizeInProgress = true;
        ResizeMode = ResizeMode.CanResize;
        try
        {
            ReleaseCapture();
            // Synchronous: returns when the native size loop ends (WM_EXITSIZEMOVE already handled).
            IntPtr hitTest = _orientation == "vertical" ? HtBottom : HtRight;
            SendMessage(hwnd, WmNcLButtonDown, hitTest, IntPtr.Zero);
        }
        finally
        {
            bool relayoutPending = _relayoutPendingDuringNativeResize;
            _nativeResizeInProgress = false;
            ResizeMode = ResizeMode.NoResize;
            if (relayoutPending)
            {
                _relayoutPendingDuringNativeResize = false;
                _manager.RequestRelayout();
            }
        }
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
            if (_nativeResizeInProgress)
            {
                _nativeResizeInProgress = false;
                ResizeMode = ResizeMode.NoResize;
                double sx = 1;
                double sy = 1;
                if (PresentationSource.FromVisual(this) is HwndSource source)
                {
                    sx = source.CompositionTarget.TransformToDevice.M11;
                    sy = source.CompositionTarget.TransformToDevice.M22;
                    if (sx <= 0) sx = 1;
                    if (sy <= 0) sy = 1;
                }
                _manager.SaveCustomSize(_type,
                    (rect.Right - rect.Left) / sx,
                    (rect.Bottom - rect.Top) / sy);
            }
            else
            {
                _manager.SaveDragOffset(_type,
                    new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            }
        }
        return IntPtr.Zero;
    }

    // WebView2 stays in a non-layered HWND. The region clips both opaque and native
    // backdrop materials to the card's 18px CSS border-radius.
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
        _webViewLifetime.Cancel();
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
        DetachWidgetCore(WebView.CoreWebView2);
        try { WebView.Dispose(); }
        catch (Exception ex) { Logger.Warn("Widget WebView disposal failed: " + ex.Message); }
        _webViewLifetime.Dispose();
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public int dwFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public IntPtr SizeOfData;
    }
}
