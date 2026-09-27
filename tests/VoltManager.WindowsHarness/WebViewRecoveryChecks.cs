using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VoltManager.Services;

namespace VoltManager.WindowsHarness;

internal static class WebViewRecoveryChecks
{
    public static async Task RunAsync(
        HarnessReport report,
        HarnessOptions options,
        WebViewRendererVariant variant)
    {
        await RunScenarioAsync(
            report,
            "webview_renderer_process_recovery",
            () => RunRendererRecoveryAsync(options, variant));
        await RunScenarioAsync(
            report,
            "webview_browser_process_recovery",
            () => RunBrowserRecoveryAsync(options, variant));
        await RunScenarioAsync(
            report,
            "webview_gpu_process_no_reload",
            () => RunGpuRecoveryAsync(options, variant));
    }

    private static async Task RunRendererRecoveryAsync(HarnessOptions options, WebViewRendererVariant variant)
    {
        await using var surface = await RecoverySurface.CreateAsync(
            Path.Combine(options.OutputDirectory, "webview-recovery-renderer"),
            variant,
            "renderer");
        int navigationCount = surface.NavigationCount;
        CoreWebView2ProcessFailedEventArgs failure = await surface.KillAndWaitAsync(
            CoreWebView2ProcessKind.Renderer,
            CoreWebView2ProcessFailedKind.RenderProcessExited,
            args =>
            {
                if (WebViewProcessFailureClassifier.Classify(args.ProcessFailedKind, args.Reason)
                    == WebViewProcessFailureAction.ReloadRenderer)
                    surface.Reload();
            });

        await surface.WaitUntilResponsiveAsync();
        if (surface.ReloadCount != 1 || surface.NavigationCount <= navigationCount)
            throw new InvalidOperationException(
                $"renderer recovery did not issue exactly one reload; kind={failure.ProcessFailedKind}; " +
                $"reloads={surface.ReloadCount}; navigations={navigationCount}->{surface.NavigationCount}");
    }

    private static async Task RunBrowserRecoveryAsync(HarnessOptions options, WebViewRendererVariant variant)
    {
        await using var surface = await RecoverySurface.CreateAsync(
            Path.Combine(options.OutputDirectory, "webview-recovery-browser"),
            variant,
            "browser");
        CoreWebView2ProcessFailedEventArgs failure = await surface.KillAndWaitAsync(
            CoreWebView2ProcessKind.Browser,
            CoreWebView2ProcessFailedKind.BrowserProcessExited);
        if (WebViewProcessFailureClassifier.Classify(failure.ProcessFailedKind, failure.Reason)
            != WebViewProcessFailureAction.RecoverBrowser)
            throw new InvalidOperationException("browser failure was not classified for full recovery");

        await surface.RecoverBrowserAsync();
        await surface.WaitUntilResponsiveAsync();
    }

    private static async Task RunGpuRecoveryAsync(HarnessOptions options, WebViewRendererVariant variant)
    {
        await using var surface = await RecoverySurface.CreateAsync(
            Path.Combine(options.OutputDirectory, "webview-recovery-gpu"),
            variant,
            "gpu");
        int navigationCount = surface.NavigationCount;
        CoreWebView2ProcessFailedEventArgs failure = await surface.KillAndWaitAsync(
            CoreWebView2ProcessKind.Gpu,
            CoreWebView2ProcessFailedKind.GpuProcessExited);
        if (WebViewProcessFailureClassifier.Classify(failure.ProcessFailedKind, failure.Reason)
            != WebViewProcessFailureAction.LogOnly)
            throw new InvalidOperationException("GPU failure unexpectedly requested application recovery");

        await surface.WaitUntilResponsiveAsync();
        await Task.Delay(250);
        if (surface.ReloadCount != 0 || surface.NavigationCount != navigationCount)
            throw new InvalidOperationException(
                $"GPU process exit caused application navigation; reloads={surface.ReloadCount}; " +
                $"navigations={navigationCount}->{surface.NavigationCount}");
    }

    private static async Task RunScenarioAsync(
        HarnessReport report,
        string name,
        Func<Task> scenario)
    {
        try
        {
            await scenario();
            report.Checks.Add(new HarnessCheck(name, "passed", "surface became script-responsive after test-owned process termination"));
        }
        catch (ScenarioNotVerifiedException ex)
        {
            report.Checks.Add(new HarnessCheck(name, "not_verified", ex.Message));
        }
        catch (Exception ex)
        {
            report.Checks.Add(new HarnessCheck(name, "failed", ex.Message));
        }
    }

    private sealed class RecoverySurface : IAsyncDisposable
    {
        private readonly string _profileRoot;
        private readonly WebViewRendererVariant _variant;
        private readonly string _name;
        private readonly Window _window;
        private readonly SharedAsyncResourceProvider<CoreWebView2Environment> _environmentProvider;
        private Task<CoreWebView2Environment> _environmentTask;
        private WebView2 _webView;

        private RecoverySurface(string profileRoot, WebViewRendererVariant variant, string name)
        {
            _profileRoot = profileRoot;
            _variant = variant;
            _name = name;
            Directory.CreateDirectory(profileRoot);
            _environmentProvider = new SharedAsyncResourceProvider<CoreWebView2Environment>(CreateEnvironmentAsync);
            _environmentTask = _environmentProvider.GetCurrent();
            _webView = new WebView2();
            _window = new Window
            {
                Width = 360,
                Height = 220,
                ShowInTaskbar = false,
                Title = "VoltManager WebView recovery " + name,
                Content = _webView,
            };
        }

        public int NavigationCount { get; private set; }
        public int ReloadCount { get; private set; }

        public static async Task<RecoverySurface> CreateAsync(
            string profileRoot,
            WebViewRendererVariant variant,
            string name)
        {
            var surface = new RecoverySurface(profileRoot, variant, name);
            try
            {
                surface._window.Show();
                await surface.InitializeControlAsync(surface._webView, surface._environmentTask);
                return surface;
            }
            catch
            {
                await surface.DisposeAsync();
                throw;
            }
        }

        public void Reload()
        {
            ReloadCount++;
            _webView.CoreWebView2.Reload();
        }

        public async Task<CoreWebView2ProcessFailedEventArgs> KillAndWaitAsync(
            CoreWebView2ProcessKind processKind,
            CoreWebView2ProcessFailedKind expectedFailureKind,
            Action<CoreWebView2ProcessFailedEventArgs>? onFailure = null)
        {
            CoreWebView2 core = _webView.CoreWebView2;
            var failed = new TaskCompletionSource<CoreWebView2ProcessFailedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, CoreWebView2ProcessFailedEventArgs args)
            {
                if (args.ProcessFailedKind != expectedFailureKind) return;
                try { onFailure?.Invoke(args); }
                finally { failed.TrySetResult(args); }
            }

            core.ProcessFailed += Handler;
            try
            {
                CoreWebView2Environment environment = await _environmentTask;
                CoreWebView2ProcessInfo? info = environment.GetProcessInfos()
                    .FirstOrDefault(candidate => candidate.Kind == processKind);
                if (info == null)
                    throw new ScenarioNotVerifiedException(
                        $"WebView2 runtime exposed no {processKind} process for the isolated surface");

                try
                {
                    using Process process = Process.GetProcessById(info.ProcessId);
                    process.Kill(entireProcessTree: false);
                    if (!process.WaitForExit(5000))
                        throw new ScenarioNotVerifiedException($"{processKind} process did not exit within 5 seconds");
                }
                catch (ArgumentException ex)
                {
                    throw new ScenarioNotVerifiedException(
                        $"{processKind} process exited before the harness could terminate it: {ex.Message}");
                }

                return await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                try { core.ProcessFailed -= Handler; } catch { }
            }
        }

        public async Task RecoverBrowserAsync()
        {
            Task<CoreWebView2Environment> replacement =
                _environmentProvider.ReplaceAfterFailure(_environmentTask);
            _environmentTask = replacement;
            await replacement.WaitAsync(TimeSpan.FromSeconds(15));

            WebView2 previous = _webView;
            var next = new WebView2();
            _window.Content = next;
            _webView = next;
            try { previous.Dispose(); } catch { }
            await InitializeControlAsync(next, replacement);
        }

        public async Task WaitUntilResponsiveAsync()
        {
            var timeout = Stopwatch.StartNew();
            Exception? last = null;
            while (timeout.Elapsed < TimeSpan.FromSeconds(15))
            {
                try
                {
                    string value = await _webView.CoreWebView2.ExecuteScriptAsync("'responsive'")
                        .WaitAsync(TimeSpan.FromSeconds(2));
                    if (value.Contains("responsive", StringComparison.Ordinal))
                        return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException("WebView did not become script-responsive after recovery.", last);
        }

        private async Task InitializeControlAsync(
            WebView2 webView,
            Task<CoreWebView2Environment> environmentTask)
        {
            CoreWebView2Environment environment = await environmentTask;
            await webView.EnsureCoreWebView2Async(environment);
            webView.CoreWebView2.NavigationStarting += (_, _) => NavigationCount++;
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
                => navigation.TrySetResult(args.IsSuccess);
            webView.CoreWebView2.NavigationCompleted += Completed;
            webView.CoreWebView2.NavigateToString(
                $"<!doctype html><html><body>{_name}<script>window.__alive=1;</script></body></html>");
            bool success = await navigation.Task.WaitAsync(TimeSpan.FromSeconds(15));
            webView.CoreWebView2.NavigationCompleted -= Completed;
            if (!success)
                throw new InvalidOperationException("WebView recovery harness navigation failed: " + _name);
        }

        private Task<CoreWebView2Environment> CreateEnvironmentAsync()
            => CoreWebView2Environment.CreateAsync(
                null,
                _profileRoot,
                new CoreWebView2EnvironmentOptions(WebViewRuntimeOptions.BrowserArguments(_variant)));

        public ValueTask DisposeAsync()
        {
            try { _window.Close(); } catch { }
            try { _webView.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScenarioNotVerifiedException(string message) : Exception(message);
}
