using System.IO;
using System.Text.RegularExpressions;
using VoltManager.Reliability;

namespace VoltManager.Tests;

/// <summary>
/// Asserts the single coherent UI unhandled-exception policy and that the
/// shipped App sources do not re-register a conflicting keep-alive handler.
/// </summary>
public class UnhandledExceptionPolicyTests
{
    [Fact]
    public void UiThreadPolicy_is_fatal_with_diagnostic_not_keep_alive()
    {
        var action = UnhandledExceptionPolicy.UiThreadPolicy;

        Assert.Equal(UnhandledUiAction.FatalShutdownWithDiagnostic, action);
        Assert.False(UnhandledExceptionPolicy.KeepsProcessAlive(action));
        Assert.True(UnhandledExceptionPolicy.CapturesCrashDiagnostic(action));
        Assert.True(UnhandledExceptionPolicy.BeginsFatalShutdown(action));
        Assert.Equal(11, AppExitCodes.UnhandledUiException);
    }

    [Fact]
    public void Recover_and_fatal_are_mutually_exclusive_helpers()
    {
        Assert.True(UnhandledExceptionPolicy.KeepsProcessAlive(UnhandledUiAction.RecoverKeepAlive));
        Assert.False(UnhandledExceptionPolicy.BeginsFatalShutdown(UnhandledUiAction.RecoverKeepAlive));

        Assert.False(UnhandledExceptionPolicy.KeepsProcessAlive(UnhandledUiAction.FatalShutdownWithDiagnostic));
        Assert.True(UnhandledExceptionPolicy.BeginsFatalShutdown(UnhandledUiAction.FatalShutdownWithDiagnostic));
    }

    [Fact]
    public void App_xaml_cs_does_not_register_DispatcherUnhandledException_keep_alive()
    {
        string appXaml = LocateSource("App.xaml.cs");
        string reliability = LocateSource("App.Reliability.cs");

        // Legacy keep-alive handler must be gone.
        Assert.DoesNotContain("OnDispatcherUnhandledException", appXaml);
        Assert.DoesNotContain("DispatcherUnhandledException += OnDispatcherUnhandledException", appXaml);

        // Sole UI handler lives in reliability partial.
        Assert.Contains("DispatcherUnhandledException += OnReliabilityDispatcherUnhandledException", reliability);
        Assert.Contains("UnhandledExceptionPolicy.UiThreadPolicy", reliability);
        Assert.Contains("BeginBoundedFatalShutdown", reliability);

        // Only one DispatcherUnhandledException subscription in App sources.
        int total =
            Regex.Matches(appXaml + "\n" + reliability, @"DispatcherUnhandledException\s*\+=").Count;
        Assert.Equal(1, total);
    }

    [Fact]
    public void Bridge_js_still_forwards_global_errors_to_logError()
    {
        string bridgeJs = LocateWwwroot("js/bridge.js");
        Assert.Contains("addEventListener('error'", bridgeJs);
        Assert.Contains("addEventListener('unhandledrejection'", bridgeJs);
        Assert.Contains("logError", bridgeJs);
        // Shared user-action failure helper (Host.fail) must remain for status hooks.
        Assert.Contains("fail(err, show)", bridgeJs);
    }

    private static string LocateSource(string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "src", "VoltManager", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            // From bin/Debug/net8.0-windows/win-x64 → repo root is several levels up.
            candidate = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "src", "VoltManager", fileName));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = Directory.GetParent(dir)?.FullName;
        }

        // Workspace-relative fallback used when tests run from repo root.
        string workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(workspace, "src", "VoltManager", fileName);
        if (File.Exists(path))
            return File.ReadAllText(path);

        throw new FileNotFoundException("Could not locate " + fileName + " from " + AppContext.BaseDirectory);
    }

    private static string LocateWwwroot(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "wwwroot", relative);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            candidate = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "src", "VoltManager", "wwwroot", relative));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException("Could not locate wwwroot/" + relative);
    }
}
