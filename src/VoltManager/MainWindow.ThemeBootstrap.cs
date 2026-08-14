using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VoltManager.Models;

namespace VoltManager;

/// <summary>
/// Keeps the WebView's very first paint on the same theme that SettingsService
/// already restored for the native shell. The main page intentionally keeps the
/// heavy power/settings bundle lazy, so theme bootstrap must happen before HTML
/// parsing instead of depending on power.js to eventually call getSettings.
/// </summary>
public partial class MainWindow
{
    private CoreWebView2? _themeBootstrapCore;
    private Task<string>? _themeBootstrapRegistration;
    private string? _themeBootstrapScriptId;
    private bool _themeBootstrapResumeScheduled;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        // OnInitialized runs during InitializeComponent, before the constructor's
        // Loaded handler starts EnsureCoreWebView2Async. Register our Loaded hook
        // first so the CoreWebView2 initialization event is observed from boot.
        Loaded += OnThemeBootstrapLoaded;
        Closed += OnThemeBootstrapClosed;
        _app.Theme.ThemeChanged += OnThemeBootstrapThemeChanged;
    }

    private void OnThemeBootstrapLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnThemeBootstrapLoaded;
        WebView.CoreWebView2InitializationCompleted += OnThemeBootstrapCoreInitialized;
    }

    private void OnThemeBootstrapCoreInitialized(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess || WebView.CoreWebView2 is not { } core)
            return;

        if (_themeBootstrapCore is not null)
            _themeBootstrapCore.NavigationStarting -= OnThemeBootstrapNavigationStarting;

        _themeBootstrapCore = core;
        _themeBootstrapScriptId = null;
        core.NavigationStarting += OnThemeBootstrapNavigationStarting;

        // AddScriptToExecuteOnDocumentCreatedAsync is asynchronous. MainWindow's
        // normal initialization may request navigation immediately afterwards;
        // NavigationStarting below gates index.html until this task completes.
        _themeBootstrapRegistration = RegisterCurrentThemeBootstrapAsync(core);
        _ = ObserveThemeBootstrapRegistrationAsync(_themeBootstrapRegistration);
    }

    private async Task<string> RegisterCurrentThemeBootstrapAsync(CoreWebView2 core)
    {
        if (!ReferenceEquals(_themeBootstrapCore, core))
            return string.Empty;

        if (!string.IsNullOrEmpty(_themeBootstrapScriptId))
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(_themeBootstrapScriptId); }
            catch { /* stale script id after a renderer/browser replacement */ }
            _themeBootstrapScriptId = null;
        }

        // ThemeWebState uses explicit JsonPropertyName attributes, so the object
        // shape consumed by theme.js is identical to HostBridge's theme payload.
        string stateJson = JsonSerializer.Serialize(_app.Theme.GetWebTheme());
        string script = "window.__voltThemeState = " + stateJson + ";";
        string scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);

        if (ReferenceEquals(_themeBootstrapCore, core))
            _themeBootstrapScriptId = scriptId;

        return scriptId;
    }

    private static async Task ObserveThemeBootstrapRegistrationAsync(Task<string> registration)
    {
        try
        {
            await registration;
        }
        catch (Exception ex)
        {
            // Failing the bootstrap must not prevent the app from opening. The
            // normal lazy settings path can still reconcile the theme afterwards.
            Logger.Warn("WebView theme bootstrap registration failed: " + ex.Message);
        }
    }

    private void OnThemeBootstrapNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("https://app.local/index.html", StringComparison.OrdinalIgnoreCase))
            return;

        Task<string>? registration = _themeBootstrapRegistration;
        if (registration is null || registration.IsCompletedSuccessfully)
            return;

        // WebView2 requires awaiting AddScriptToExecuteOnDocumentCreatedAsync
        // before relying on it for a future navigation. Cancel only the app
        // document, then replay it once the bootstrap registration is ready.
        e.Cancel = true;
        if (_themeBootstrapResumeScheduled)
            return;

        _themeBootstrapResumeScheduled = true;
        _ = ResumeAppNavigationAfterThemeBootstrapAsync(
            _themeBootstrapCore!, e.Uri, registration);
    }

    private async Task ResumeAppNavigationAfterThemeBootstrapAsync(
        CoreWebView2 core,
        string uri,
        Task<string> registration)
    {
        try
        {
            await registration;
        }
        catch
        {
            // The observer above logs the failure. Allow the page to open with
            // its defensive Blue fallback instead of trapping navigation.
            if (ReferenceEquals(_themeBootstrapRegistration, registration))
                _themeBootstrapRegistration = null;
        }
        finally
        {
            _themeBootstrapResumeScheduled = false;
            if (ReferenceEquals(_themeBootstrapCore, core) &&
                ReferenceEquals(WebView.CoreWebView2, core))
            {
                core.Navigate(uri);
            }
        }
    }

    private void OnThemeBootstrapThemeChanged(AppThemeColor _)
    {
        // Keep the document-created script current too. This matters when the
        // window is parked to about:blank in the tray and later reloads index.html.
        _ = Dispatcher.InvokeAsync(() =>
        {
            CoreWebView2? core = _themeBootstrapCore;
            if (core is null || !ReferenceEquals(WebView.CoreWebView2, core))
                return;

            _themeBootstrapRegistration = RegisterCurrentThemeBootstrapAsync(core);
            _ = ObserveThemeBootstrapRegistrationAsync(_themeBootstrapRegistration);
        });
    }

    private void OnThemeBootstrapClosed(object? sender, EventArgs e)
    {
        WebView.CoreWebView2InitializationCompleted -= OnThemeBootstrapCoreInitialized;
        if (_themeBootstrapCore is not null)
            _themeBootstrapCore.NavigationStarting -= OnThemeBootstrapNavigationStarting;
        _app.Theme.ThemeChanged -= OnThemeBootstrapThemeChanged;
        Closed -= OnThemeBootstrapClosed;
    }
}
