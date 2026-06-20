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

    public WidgetWindow(App app, WidgetManager manager, WidgetItem item,
        Task<CoreWebView2Environment> envTask, Size size)
    {
        _app = app;
        _manager = manager;
        _envTask = envTask;
        _type = item.Type;

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
            WebView.DefaultBackgroundColor = Drawing.Color.FromArgb(0, 0, 0, 0);
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

            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) return;
                _bridge?.PushEvent("metrics", _app.Monitor.Latest);
                OnActivePlanChanged(_app.ActivePlan);
                _manager.PushTheme();
            };

            core.Navigate("https://app.local/widgets.html?w=" + Uri.EscapeDataString(_type) +
                "&v=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (Exception ex)
        {
            Logger.Error("Widget WebView setup failed", ex);
            Close();
        }
    }

    public void PushEvent(string name, object data) => _bridge?.PushEvent(name, data);

    private void OnMetricsUpdated(MetricsSnapshot metrics) => _bridge?.PushEvent("metrics", metrics);

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

    protected override void OnClosed(EventArgs e)
    {
        _saveLocationTimer.Stop();
        _manager.SavePosition(_type, Left, Top);
        _app.Monitor.MetricsUpdated -= OnMetricsUpdated;
        _app.ActivePlanChanged -= OnActivePlanChanged;
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
