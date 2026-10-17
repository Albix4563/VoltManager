using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using VoltManager.Services;

namespace VoltManager.WindowsHarness;

/// <summary>Measures launch-to-operational readiness through validation-only monotonic app markers.</summary>
internal static class StartupBenchmarkRunner
{
    private const string SingleBenchmarkMutex = @"Local\VoltManager_StartupBenchmark_SingleRun";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] RequiredStages =
    [
        "app_start", "host:MainWindow", "tray_ready", "webview_environment_requested",
        "webview_environment_ready", "webview_core_ready", "navigation_started",
        "navigation_completed", "first_frame", "dashboard_ready",
    ];

    internal static int Run(HarnessOptions options)
    {
        using var singleRun = new Mutex(initiallyOwned: true, SingleBenchmarkMutex, out bool ownsRun);
        if (!ownsRun)
            throw new InvalidOperationException("Another VoltManager startup benchmark is running in this Windows session.");
        try { return RunExclusive(options); }
        finally { singleRun.ReleaseMutex(); }
    }

    private static int RunExclusive(HarnessOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppPath) || !File.Exists(options.AppPath))
            throw new FileNotFoundException("--app must point to VoltManager.exe", options.AppPath);
        if (options.Renderer != "hardware")
            throw new ArgumentException("Startup benchmarks require the hardware renderer.");
        if (options.ProfileMode is not ("fresh" or "reused"))
            throw new ArgumentException("--profile-mode must be fresh or reused.");
        if (!Environment.UserInteractive)
            throw new InvalidOperationException("Startup benchmarks require an interactive Windows desktop.");

        string appPath = Path.GetFullPath(options.AppPath);
        string appDir = Path.GetDirectoryName(appPath)!;
        string rootPath = options.ProfileRoot ?? Path.Combine(options.OutputDirectory, "isolated-appdata");
        string validationRoot = Path.GetFullPath(rootPath);
        PrepareProfile(validationRoot, options.ProfileMode);
        string logPath = Path.Combine(validationRoot, "VoltManager", "startup-events.jsonl");
        string proxyState = Path.Combine(options.OutputDirectory, "app-proxy.json");
        // A reused profile keeps WebView2 user data, but never retains previous run markers.
        if (File.Exists(logPath)) File.Delete(logPath);
        if (File.Exists(proxyState)) File.Delete(proxyState);

        Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, validationRoot);
        Environment.SetEnvironmentVariable(ValidationEnvironment.SuppressPowerVariable, "1");
        Environment.SetEnvironmentVariable(ValidationEnvironment.RendererVariable, "hardware");
        string harnessExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Harness executable path unavailable.");
        string supervisorPath = AppBenchmarkRunner.ResolveSupervisorPath(appDir, options, harnessExe);
        bool usingSupervisor = !string.Equals(supervisorPath, harnessExe, StringComparison.OrdinalIgnoreCase);
        AppBenchmarkRunner.WriteBenchmarkSettings(validationRoot, options with { Scenario = "dashboard-active" }, harnessExe);

        long launchTicks = Stopwatch.GetTimestamp();
        var run = new StartupRun
        {
            Label = options.Label,
            Iteration = options.Iteration,
            ProfileMode = options.ProfileMode,
            Renderer = options.Renderer,
            AppPath = appPath,
            ProfileRoot = validationRoot,
            Revision = FileVersionInfo.GetVersionInfo(appPath).ProductVersion ?? "unknown",
            SupervisorPath = usingSupervisor ? supervisorPath : "harness-fallback",
            WebViewRuntime = ReadWebViewVersion(),
            Machine = Environment.MachineName,
            TimestampFrequency = Stopwatch.Frequency,
            LaunchTicks = launchTicks,
            LaunchMode = "supervisor/harness proxy -> dotnet VoltManager.dll --supervised",
        };
        Process? root = null;
        Process? app = null;
        try
        {
            root = AppBenchmarkRunner.StartVoltManager(appPath, appDir, validationRoot,
                options with { Scenario = "dashboard-active" }, harnessExe, supervisorPath, usingSupervisor);
            app = AppBenchmarkRunner.WaitForAppProcess(proxyState, root, TimeSpan.FromSeconds(20));
            run.AppPid = app.Id;

            var timeout = TimeSpan.FromSeconds(45);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                ReadMarkers(logPath, app.Id, launchTicks, run.StageLatenciesMs);
                if (RequiredStages.All(run.StageLatenciesMs.ContainsKey)) break;
                app.Refresh();
                if (app.HasExited) throw new InvalidOperationException("VoltManager exited before dashboard readiness.");
                Thread.Sleep(50);
            }
            string? validationError = ValidateStages(run.StageLatenciesMs);
            if (validationError is not null)
                throw new InvalidDataException(validationError);

            run.AppStartLatencyMs = run.StageLatenciesMs["app_start"];
            run.StartupLatencyMs = run.StageLatenciesMs["dashboard_ready"];
            run.FirstFrameLatencyMs = run.StageLatenciesMs["first_frame"];
            run.TrayLatencyMs = run.StageLatenciesMs["tray_ready"];
            run.Status = "passed";
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.FailureReason = ex.GetType().Name + ": " + ex.Message;
            if (app != null) ReadMarkers(logPath, app.Id, launchTicks, run.StageLatenciesMs);
        }
        finally
        {
            AppBenchmarkRunner.TrySignalShutdown();
            if (root != null)
            {
                if (!root.WaitForExit(8000))
                {
                    AppBenchmarkRunner.TryKill(root);
                    root.WaitForExit(5000);
                }
                root.Dispose();
            }
            if (app != null)
            {
                // Only the PID obtained from this run's private app-proxy may be terminated.
                AppBenchmarkRunner.TryKill(app);
                app.WaitForExit(5000);
                app.Dispose();
            }
            if (File.Exists(logPath))
                File.Copy(logPath, Path.Combine(options.OutputDirectory, "startup-events.jsonl"), true);
            File.WriteAllText(Path.Combine(options.OutputDirectory, "startup-run.json"),
                JsonSerializer.Serialize(run, JsonOptions));
            File.WriteAllText(Path.Combine(options.OutputDirectory, "startup-run.csv"), ToCsv(run));
        }
        return run.Status == "passed" ? 0 : 1;
    }

    internal static void ReadMarkers(string path, int pid, long launchTicks, Dictionary<string, double> stages)
    {
        if (!File.Exists(path)) return;
        try
        {
            // File.ReadAllLines opens with FileShare.Read, occasionally preventing
            // the UI process from appending an essential marker. Never lock writers.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (root.GetProperty("pid").GetInt32() != pid) continue;
                string? stage = root.GetProperty("stage").GetString();
                long ticks = root.GetProperty("ticks").GetInt64();
                if (stage is null || ticks < launchTicks || ticks > Stopwatch.GetTimestamp()) continue;
                double elapsed = Stopwatch.GetElapsedTime(launchTicks, ticks).TotalMilliseconds;
                if (!stages.TryAdd(stage, elapsed) &&
                    (stage is "navigation_started" or "webview_core_ready") &&
                    Math.Abs(stages[stage] - elapsed) > 0.01)
                    stages.TryAdd("startup_retry_detected", elapsed);
            }
        }
        catch (IOException) { /* Concurrent writes can be partially visible; retry. */ }
        catch (JsonException) { /* A partially written last line is retried. */ }
    }

    internal static string? ValidateStages(IReadOnlyDictionary<string, double> stages)
    {
        if (stages.ContainsKey("webview_recovery_during_startup") || stages.ContainsKey("startup_retry_detected"))
            return "WebView2 restarted or navigated again before dashboard readiness.";
        foreach (string stage in RequiredStages)
        {
            if (!stages.TryGetValue(stage, out double value)) return "Missing startup marker: " + stage;
            if (!double.IsFinite(value) || value < 0) return "Invalid timestamp for " + stage;
        }
        string[] sequence = ["app_start", "host:MainWindow", "tray_ready", "webview_core_ready",
            "navigation_started", "navigation_completed", "first_frame", "dashboard_ready"];
        for (int i = 1; i < sequence.Length; i++)
        {
            if (stages[sequence[i - 1]] > stages[sequence[i]])
                return "Out of order startup markers: " + sequence[i - 1] + "/" + sequence[i];
        }
        if (stages["webview_environment_requested"] > stages["webview_environment_ready"] ||
            stages["webview_environment_ready"] > stages["webview_core_ready"])
            return "WebView2 environment markers are out of order.";
        return null;
    }

    internal static void PrepareProfile(string root, string mode)
    {
        const string sentinel = ".voltmanager-startup-benchmark";
        string validationRoot = Path.GetFullPath(root);
        foreach (Environment.SpecialFolder folder in new[]
        {
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
        })
        {
            string personal = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(personal) &&
                (validationRoot.Equals(personal, StringComparison.OrdinalIgnoreCase) ||
                 validationRoot.StartsWith(personal + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Benchmark profiles must be outside personal AppData.");
        }
        string marker = Path.Combine(validationRoot, sentinel);
        // A benchmark is never allowed to overwrite unknown settings or personal WebView2
        // data. Distinct fresh samples require a genuinely unused test-owned directory.
        if (Directory.Exists(validationRoot))
        {
            if (mode == "fresh" && Directory.EnumerateFileSystemEntries(validationRoot).Any())
                throw new InvalidOperationException("Fresh benchmark profile is already initialized: " + validationRoot);
            if (mode == "reused" && !File.Exists(marker) &&
                Directory.EnumerateFileSystemEntries(validationRoot).Any())
                throw new InvalidOperationException("Refusing to overwrite an unowned benchmark profile: " + validationRoot);
        }
        Directory.CreateDirectory(validationRoot);
        if (!File.Exists(marker)) File.WriteAllText(marker, "VoltManager isolated startup benchmark profile");
    }

    internal static bool VerifyMarkerCalculation(string outputDirectory)
    {
        // Deterministic contract: missing/invalid markers never count as ready;
        // unrelated PIDs and a previous process session never enter the timeline.
        string path = Path.Combine(outputDirectory, "startup-marker-fixture.jsonl");
        try
        {
            var stages = RequiredStages.Select((stage, index) => (stage, ms: (double)(index + 1)))
                .ToDictionary(x => x.stage, x => x.ms, StringComparer.Ordinal);
            if (ValidateStages(stages) is not null) return false;
            stages.Remove("dashboard_ready");
            if (ValidateStages(stages) is null) return false;
            stages["dashboard_ready"] = 10;
            stages["first_frame"] = 11;
            if (ValidateStages(stages) is null) return false;

            long launch = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
            int pid = Environment.ProcessId;
            File.WriteAllLines(path,
            [
                JsonSerializer.Serialize(new { pid = pid + 1, ticks = launch + Stopwatch.Frequency / 2, stage = "dashboard_ready" }),
                JsonSerializer.Serialize(new { pid, ticks = launch - 1, stage = "dashboard_ready" }),
                JsonSerializer.Serialize(new { pid, ticks = launch + Stopwatch.Frequency / 2, stage = "app_start" }),
            ]);
            var accepted = new Dictionary<string, double>(StringComparer.Ordinal);
            ReadMarkers(path, pid, launch, accepted);
            if (accepted.Count != 1 || !accepted.ContainsKey("app_start") ||
                Math.Abs(accepted["app_start"] - 500) >= 1 || ValidateStages(accepted) is null) return false;

            string profile = Path.Combine(outputDirectory, "startup-profile-fixture");
            PrepareProfile(profile, "fresh");
            bool repeatRejected = false;
            try { PrepareProfile(profile, "fresh"); }
            catch (InvalidOperationException) { repeatRejected = true; }
            PrepareProfile(profile, "reused");
            return repeatRejected &&
                Math.Abs(accepted["app_start"] - 500) < 1 && ValidateStages(accepted) is not null;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string profile = Path.Combine(outputDirectory, "startup-profile-fixture");
            if (File.Exists(Path.Combine(profile, ".voltmanager-startup-benchmark")))
            {
                File.Delete(Path.Combine(profile, ".voltmanager-startup-benchmark"));
                Directory.Delete(profile);
            }
        }
    }

    private static string ReadWebViewVersion()
    {
        try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return "unavailable"; }
    }

    private static string ToCsv(StartupRun run)
    {
        static string Esc(object? item) => "\"" + Convert.ToString(item, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\"";
        return "Label,Iteration,ProfileMode,Status,FailureReason,StartupLatencyMs,FirstFrameLatencyMs,TrayLatencyMs,AppStartLatencyMs,AppPid,Revision,StageLatenciesMs" + Environment.NewLine +
            string.Join(",", new object?[] { run.Label, run.Iteration, run.ProfileMode, run.Status,
                run.FailureReason, run.StartupLatencyMs, run.FirstFrameLatencyMs, run.TrayLatencyMs,
                run.AppStartLatencyMs, run.AppPid, run.Revision, JsonSerializer.Serialize(run.StageLatenciesMs) }.Select(Esc)) + Environment.NewLine;
    }
}

internal sealed class StartupRun
{
    public string Label { get; set; } = "";
    public int Iteration { get; set; }
    public string ProfileMode { get; set; } = "";
    public string Renderer { get; set; } = "";
    public string Status { get; set; } = "failed";
    public string? FailureReason { get; set; }
    public string AppPath { get; set; } = "";
    public string ProfileRoot { get; set; } = "";
    public string Revision { get; set; } = "";
    public string SupervisorPath { get; set; } = "";
    public string WebViewRuntime { get; set; } = "";
    public string Machine { get; set; } = "";
    public string LaunchMode { get; set; } = "";
    public long LaunchTicks { get; set; }
    public long TimestampFrequency { get; set; }
    public int? AppPid { get; set; }
    public double? AppStartLatencyMs { get; set; }
    public double? StartupLatencyMs { get; set; }
    public double? FirstFrameLatencyMs { get; set; }
    public double? TrayLatencyMs { get; set; }
    public Dictionary<string, double> StageLatenciesMs { get; set; } = new(StringComparer.Ordinal);
}
