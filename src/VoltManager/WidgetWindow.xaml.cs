using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Microsoft.Web.WebView2.Core;
using VoltManager.Bridge;
using VoltManager.Models;
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

    private readonly App _app;
    private readonly WidgetManager _manager;
    private readonly Task<CoreWebView2Environment> _envTask;
    private readonly string _type;
    private HostBridge? _bridge;
    private string _size;
    private HwndSource? _hwndSource;
    private bool _applyingPlacement;

    public WidgetWindow(App app, WidgetManager manager, WidgetItem item,
        Task<CoreWebView2Environment> envTask, Size size, WidgetPlacement placement)
    {
        _app = app;
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
        SourceInitialized += (_, _) =>
        {
            ApplyToolWindowStyle();
            HookWndProc();
            ApplyPlacement(placement, item.Size);
            ApplyRoundedRegion();
        };
        DpiChanged += (_, _) =>
        {
            ApplyRoundedRegion();
            _manager.RequestRelayout();
        };
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            WebView.DefaultBackgroundColor = Drawing.Color.FromArgb(255, 14, 26, 46);
            await WebView.EnsureCoreWebView2Async(await _envTask);
        }
        catch (Exception ex)
        {
            Logger.Error("Widget WebView2 initialization failed", ex);
            Close();
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

            _bridge = new HostBridge(WebView, _app.Hardware, _app.Power, _app.Settings,
                _app.Updates, _app.AutoStart, _app.Monitor, _app, subscribeGlobalEvents: false);
            _bridge.Attach();
            _bridge.WidgetDragRequested += BeginNativeDrag;
            _bridge.WidgetTopmostRequested += SetTopmostFromWidget;
            _bridge.WidgetCloseRequested += () => _manager.SetEnabled(_type, false);

            _app.Monitor.MetricsUpdated += OnMetricsUpdated;
            _app.ActivePlanChanged += OnActivePlanChanged;
            _app.CpuAutomationStateChanged += OnCpuAutomationStateChanged;
            _app.Awake.StateChanged += OnKeepAwakeStateChanged;

            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) return;
                _bridge?.PushEvent("metrics", _app.Monitor.Latest);
                OnActivePlanChanged(_app.ActivePlan);
                OnCpuAutomationStateChanged(_app.CpuAutomationState);
                _bridge?.PushEvent("keepAwakeChanged", _app.Awake.GetState());
                _manager.PushTheme();
                _manager.PushLanguage();
                _manager.PushFont();
            };

            core.Navigate(WidgetUrl());
        }
        catch (Exception ex)
        {
            Logger.Error("Widget WebView setup failed", ex);
            Close();
        }
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
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SwpNoActivate | SwpNoZOrder);
            ApplyRoundedRegion();
            if (sizeChanged)
                WebView.CoreWebView2?.Navigate(WidgetUrl());
        }
        finally
        {
            _applyingPlacement = false;
        }
    }

    private string WidgetUrl() =>
        "https://app.local/widgets.html?w=" + Uri.EscapeDataString(_type) +
        "&s=" + Uri.EscapeDataString(_size) +
        "&v=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void OnMetricsUpdated(MetricsSnapshot metrics) => _bridge?.PushEvent("metrics", metrics);

    private void OnCpuAutomationStateChanged(CpuAutomationState state)
        => _bridge?.PushEvent("cpuAutomationStateChanged", state);

    private void OnActivePlanChanged(PowerPlan? plan)
        => _bridge?.PushEvent("activePlanChanged", new { plan = plan?.PlanId, guid = plan?.Guid, name = plan?.Name });

    private void OnKeepAwakeStateChanged(KeepAwakeState state)
        => _bridge?.PushEvent("keepAwakeChanged", state);

    private void SetTopmostFromWidget(bool topmost)
    {
        Topmost = topmost;
        _manager.SetPinned(_type, topmost);
        _bridge?.PushEvent("widgetTopmostChanged", new { topmost });
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
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _app.Monitor.MetricsUpdated -= OnMetricsUpdated;
        _app.ActivePlanChanged -= OnActivePlanChanged;
        _app.CpuAutomationStateChanged -= OnCpuAutomationStateChanged;
        _app.Awake.StateChanged -= OnKeepAwakeStateChanged;
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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
