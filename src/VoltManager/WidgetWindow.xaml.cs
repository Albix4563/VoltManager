using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Microsoft.Web.WebView2.Core;
using VoltManager.Bridge;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager;

public partial class WidgetWindow : Window
{
    private const int WmNcLButtonDown = 0xA1;
    private static readonly IntPtr HtCaption = new(0x2);

    private readonly App _app;
    private readonly WidgetManager _manager;
    private readonly Task<CoreWebView2Environment> _envTask;
    private readonly string _type;
    private readonly DispatcherTimer _saveLocationTimer;
    private HostBridge? _bridge;
    private string _size;

    public WidgetWindow(App app, WidgetManager manager, WidgetItem item,
        Task<CoreWebView2Environment> envTask, Size size)
    {
        _app = app;
        _manager = manager;
        _envTask = envTask;
        _type = item.Type;
        _size = WidgetSettings.NormalizeSize(item.Size);

        InitializeComponent();

        Width = size.Width;
        Height = size.Height;
        Left = item.X ?? 80;
        Top = item.Y ?? 80;
        Topmost = item.Pinned;

        _saveLocationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveLocationTimer.Tick += (_, _) =>
        {
            _saveLocationTimer.Stop();
            _manager.SavePosition(_type, Left, Top);
        };

        Loaded += async (_, _) => await InitWebViewAsync();
        SourceInitialized += (_, _) => ApplyRoundedRegion();
        DpiChanged += (_, _) => ApplyRoundedRegion();
        LocationChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            _saveLocationTimer.Stop();
            _saveLocationTimer.Start();
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

            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) return;
                _bridge?.PushEvent("metrics", _app.Monitor.Latest);
                OnActivePlanChanged(_app.ActivePlan);
                OnCpuAutomationStateChanged(_app.CpuAutomationState);
                _manager.PushTheme();
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

    public void ApplyPresetSize(Size size, string sizeKey)
    {
        _size = WidgetSettings.NormalizeSize(sizeKey);
        Width = size.Width;
        Height = size.Height;
        var p = WidgetManager.ClampPosition(SystemParameters.WorkArea, Left, Top, size);
        Left = p.X;
        Top = p.Y;
        ApplyRoundedRegion();
        WebView.CoreWebView2?.Navigate(WidgetUrl());
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

    // Rounded window corners without per-pixel transparency: WebView2 renders black
    // under a layered (AllowsTransparency) window, so we keep the window opaque and
    // clip it to a rounded region that matches the card's 18px CSS border-radius.
    private void ApplyRoundedRegion()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.Handle == IntPtr.Zero)
            return;

        var m = source.CompositionTarget.TransformToDevice;
        int w = (int)Math.Round(Width * m.M11);
        int h = (int)Math.Round(Height * m.M22);
        int d = (int)Math.Round(18 * 2 * m.M11); // diameter = 2 × 18px radius
        SetWindowRgn(source.Handle, CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d), true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _saveLocationTimer.Stop();
        _manager.SavePosition(_type, Left, Top);
        _app.Monitor.MetricsUpdated -= OnMetricsUpdated;
        _app.ActivePlanChanged -= OnActivePlanChanged;
        _app.CpuAutomationStateChanged -= OnCpuAutomationStateChanged;
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
}
