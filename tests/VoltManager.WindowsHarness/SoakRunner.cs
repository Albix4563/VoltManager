using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VoltManager.Services;

namespace VoltManager.WindowsHarness;

internal static class SoakRunner
{
    private const uint WmNull = 0x0000;
    private const uint WmClose = 0x0010;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    public static int Run(HarnessOptions options)
    {
        DateTime startedAtUtc = DateTime.UtcNow;
        var samples = new List<SoakSample>();
        Process? root = null;
        Process? app = null;
        ChildExitTracker? childTracker = null;
        AppCrashLogProbe? crashLogProbe = null;

        try
        {
            if (string.IsNullOrWhiteSpace(options.AppPath) || !File.Exists(options.AppPath))
                throw new FileNotFoundException("--app must point to VoltManager.exe", options.AppPath);

            string appPath = Path.GetFullPath(options.AppPath);
            string appDir = Path.GetDirectoryName(appPath)!;
            string validationRoot = Path.Combine(options.OutputDirectory, "isolated-appdata");
            Directory.CreateDirectory(validationRoot);
            string appLogPath = Path.Combine(validationRoot, "VoltManager", "logs", "voltmanager.log");
            crashLogProbe = new AppCrashLogProbe(appLogPath);

            var launchOptions = options with { Scenario = "tray-widget" };
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, validationRoot);
            Environment.SetEnvironmentVariable(ValidationEnvironment.SuppressPowerVariable, "1");
            Environment.SetEnvironmentVariable(ValidationEnvironment.RendererVariable, launchOptions.Renderer);
            ValidationMetrics.Reset();

            string harnessExe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Harness executable path unavailable.");
            string supervisorPath = AppBenchmarkRunner.ResolveSupervisorPath(appDir, launchOptions, harnessExe);
            bool usingSupervisor = !string.Equals(supervisorPath, harnessExe, StringComparison.OrdinalIgnoreCase);
            AppBenchmarkRunner.WriteBenchmarkSettings(validationRoot, launchOptions, harnessExe);

            string proxyState = Path.Combine(options.OutputDirectory, "app-proxy.json");
            try { File.Delete(proxyState); } catch { }

            root = AppBenchmarkRunner.StartVoltManager(
                appPath,
                appDir,
                validationRoot,
                launchOptions,
                harnessExe,
                supervisorPath,
                usingSupervisor);
            app = AppBenchmarkRunner.WaitForAppProcess(proxyState, root, TimeSpan.FromSeconds(20));
            childTracker = new ChildExitTracker();

            AppBenchmarkRunner.SignalShowWindow();
            IntPtr mainWindow = AppBenchmarkRunner.WaitForMainWindow(app, TimeSpan.FromSeconds(20));
            AppBenchmarkRunner.WaitForResponsive(mainWindow, TimeSpan.FromSeconds(10));

            var stopwatch = Stopwatch.StartNew();
            int nextSampleIndex = 0;
            IntPtr widgetWindow = IntPtr.Zero;
            bool widgetUnavailableLogged = false;

            while (true)
            {
                double targetSeconds = nextSampleIndex * options.SoakSampleInterval.TotalSeconds;
                if (targetSeconds > options.SoakDuration.TotalSeconds)
                    break;

                if (nextSampleIndex > 0)
                {
                    int phase = nextSampleIndex % 4;
                    DriveScenario(
                        phase,
                        app,
                        ref mainWindow,
                        ref widgetWindow,
                        ref widgetUnavailableLogged);
                    WaitUntil(stopwatch, TimeSpan.FromSeconds(targetSeconds));
                }

                SoakSample sample = CaptureSample(
                    stopwatch.Elapsed.TotalSeconds,
                    root,
                    app,
                    mainWindow,
                    childTracker,
                    crashLogProbe);
                samples.Add(sample);
                LogSample(samples.Count, sample);

                if (!sample.RootAlive || !sample.HostAlive || sample.ChildCrashDetected)
                    break;

                nextSampleIndex++;
            }

            stopwatch.Stop();
            SoakAnalysis analysis = SoakAnalyzer.Analyze(samples, options.SoakWarmup, options.SoakThresholds);
            WriteSamples(options.OutputDirectory, startedAtUtc, samples);
            WriteReport(options, startedAtUtc, analysis, samples.Count);
            return analysis.Verdict == SoakVerdict.Failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            WriteSamples(options.OutputDirectory, startedAtUtc, samples);
            WriteFailureReport(options, startedAtUtc, ex, samples.Count);
            return 1;
        }
        finally
        {
            childTracker?.Dispose();
            AppBenchmarkRunner.TrySignalShutdown();

            if (root != null)
            {
                try
                {
                    if (!root.WaitForExit(8000))
                        AppBenchmarkRunner.TryKill(root);
                }
                catch
                {
                    AppBenchmarkRunner.TryKill(root);
                }
            }

            if (app != null)
                AppBenchmarkRunner.TryKill(app);
            app?.Dispose();
            root?.Dispose();
        }
    }

    private static void DriveScenario(
        int phase,
        Process app,
        ref IntPtr mainWindow,
        ref IntPtr widgetWindow,
        ref bool widgetUnavailableLogged)
    {
        switch (phase)
        {
            case 0:
                AppBenchmarkRunner.SignalShowWindow();
                mainWindow = AppBenchmarkRunner.WaitForMainWindow(app, TimeSpan.FromSeconds(5));
                AppBenchmarkRunner.WaitForResponsive(mainWindow, TimeSpan.FromSeconds(5));
                TrySetWidgetVisibility(app.Id, mainWindow, ref widgetWindow, visible: true, ref widgetUnavailableLogged);
                Console.WriteLine("soak cycle: dashboard visible, widget visible");
                break;

            case 1:
                TrySetWidgetVisibility(app.Id, mainWindow, ref widgetWindow, visible: false, ref widgetUnavailableLogged);
                Console.WriteLine("soak cycle: dashboard visible, widget hidden");
                break;

            case 2:
                _ = PostMessage(mainWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
                WaitForWindowVisibility(mainWindow, visible: false, TimeSpan.FromSeconds(3));
                TrySetWidgetVisibility(app.Id, mainWindow, ref widgetWindow, visible: true, ref widgetUnavailableLogged);
                Console.WriteLine("soak cycle: dashboard hidden to tray, widget visible");
                break;

            default:
                AppBenchmarkRunner.SignalShowWindow();
                mainWindow = AppBenchmarkRunner.WaitForMainWindow(app, TimeSpan.FromSeconds(5));
                AppBenchmarkRunner.WaitForResponsive(mainWindow, TimeSpan.FromSeconds(5));
                TrySetWidgetVisibility(app.Id, mainWindow, ref widgetWindow, visible: true, ref widgetUnavailableLogged);
                Console.WriteLine("soak cycle: dashboard restored from tray, widget visible");
                break;
        }
    }

    private static SoakSample CaptureSample(
        double elapsedSeconds,
        Process root,
        Process app,
        IntPtr mainWindow,
        ChildExitTracker childTracker,
        AppCrashLogProbe crashLogProbe)
    {
        bool rootAlive = IsAlive(root);
        bool hostAlive = IsAlive(app);
        HashSet<int> pids = rootAlive
            ? AppBenchmarkRunner.DescendantsAndSelf(root.Id)
            : new HashSet<int>();
        bool childCrash = childTracker.Observe(pids, root.Id, app.Id) || crashLogProbe.Observe();

        long groupPrivate = 0;
        long groupWorking = 0;
        int groupHandles = 0;
        int groupThreads = 0;
        int groupProcesses = 0;
        foreach (int pid in pids)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                process.Refresh();
                if (process.HasExited)
                    continue;
                groupProcesses++;
                groupPrivate += SafeLong(() => process.PrivateMemorySize64);
                groupWorking += SafeLong(() => process.WorkingSet64);
                groupHandles += SafeInt(() => process.HandleCount);
                groupThreads += SafeInt(() => process.Threads.Count);
            }
            catch { }
        }

        long hostPrivate = 0;
        long hostWorking = 0;
        int hostHandles = 0;
        int hostThreads = 0;
        int? gdi = null;
        int? user = null;
        if (hostAlive)
        {
            try
            {
                app.Refresh();
                hostPrivate = app.PrivateMemorySize64;
                hostWorking = app.WorkingSet64;
                hostHandles = app.HandleCount;
                hostThreads = app.Threads.Count;
                IntPtr handle = app.Handle;
                gdi = GetGuiResources(handle, 0);
                user = GetGuiResources(handle, 1);
            }
            catch { }
        }

        bool responsive = hostAlive && ProbeResponsive(mainWindow, TimeSpan.FromSeconds(2));
        return new SoakSample(
            elapsedSeconds,
            hostPrivate,
            groupPrivate,
            hostWorking,
            groupWorking,
            hostHandles,
            groupHandles,
            hostThreads,
            groupThreads,
            gdi,
            user,
            groupProcesses,
            responsive,
            rootAlive,
            hostAlive,
            childCrash);
    }

    private static void TrySetWidgetVisibility(
        int hostPid,
        IntPtr mainWindow,
        ref IntPtr widgetWindow,
        bool visible,
        ref bool unavailableLogged)
    {
        if (widgetWindow == IntPtr.Zero || !IsWindow(widgetWindow))
            widgetWindow = FindWidgetWindow(hostPid, mainWindow, TimeSpan.FromSeconds(2));

        if (widgetWindow == IntPtr.Zero)
        {
            if (!unavailableLogged)
            {
                Console.WriteLine("soak cycle: widget window unavailable; widget toggle skipped");
                unavailableLogged = true;
            }
            return;
        }

        _ = ShowWindow(widgetWindow, visible ? SwShowNoActivate : SwHide);
    }

    private static IntPtr FindWidgetWindow(int hostPid, IntPtr mainWindow, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            IntPtr candidate = IntPtr.Zero;
            _ = EnumWindows((hwnd, lParam) =>
            {
                if (hwnd == mainWindow || !IsWindowVisible(hwnd))
                    return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != hostPid)
                    return true;

                int exStyle = GetWindowLong(hwnd, GwlExStyle);
                if ((exStyle & WsExToolWindow) == 0)
                    return true;

                if (!GetWindowRect(hwnd, out Rect rect)
                    || rect.Right - rect.Left < 20
                    || rect.Bottom - rect.Top < 20)
                    return true;

                candidate = hwnd;
                return false;
            }, IntPtr.Zero);

            if (candidate != IntPtr.Zero)
                return candidate;
            Thread.Sleep(100);
        }
        return IntPtr.Zero;
    }

    private static bool ProbeResponsive(IntPtr hwnd, TimeSpan timeout)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;
        IntPtr result = SendMessageTimeout(
            hwnd,
            WmNull,
            IntPtr.Zero,
            IntPtr.Zero,
            SmtoBlock | SmtoAbortIfHung,
            (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue),
            out _);
        return result != IntPtr.Zero;
    }

    private static void WaitForWindowVisibility(IntPtr hwnd, bool visible, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (IsWindowVisible(hwnd) == visible)
                return;
            Thread.Sleep(50);
        }
    }

    private static void WaitUntil(Stopwatch stopwatch, TimeSpan target)
    {
        while (true)
        {
            TimeSpan remaining = target - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return;
            Thread.Sleep(remaining > TimeSpan.FromMilliseconds(250)
                ? TimeSpan.FromMilliseconds(250)
                : remaining);
        }
    }

    private static void WriteReport(
        HarnessOptions options,
        DateTime startedAtUtc,
        SoakAnalysis analysis,
        int totalSampleCount)
    {
        var report = NewReport(startedAtUtc);
        foreach (SoakCheckResult check in analysis.Checks)
        {
            report.Checks.Add(new HarnessCheck(
                check.Name,
                check.Verdict switch
                {
                    SoakVerdict.Passed => "passed",
                    SoakVerdict.Failed => "failed",
                    _ => "not_verified",
                },
                check.Detail));
        }

        foreach (var pair in analysis.SlopesPerHour)
            report.Metrics[pair.Key + "_per_hour"] = pair.Value;
        report.Metrics["totalSampleCount"] = totalSampleCount;
        report.Metrics["measurementSampleCount"] = analysis.MeasurementSampleCount;
        report.Metrics["durationMinutes"] = options.SoakDuration.TotalMinutes;
        report.Metrics["warmupMinutes"] = options.SoakWarmup.TotalMinutes;
        report.Metrics["sampleSeconds"] = options.SoakSampleInterval.TotalSeconds;
        report.CompletedAtUtc = DateTime.UtcNow;
        Program.WriteReport(report, options.OutputDirectory);
    }

    private static void WriteFailureReport(
        HarnessOptions options,
        DateTime startedAtUtc,
        Exception exception,
        int totalSampleCount)
    {
        var report = NewReport(startedAtUtc);
        report.Checks.Add(new HarnessCheck("soak_execution", "failed", exception.Message));
        report.Metrics["totalSampleCount"] = totalSampleCount;
        report.CompletedAtUtc = DateTime.UtcNow;
        Program.WriteReport(report, options.OutputDirectory);
    }

    private static HarnessReport NewReport(DateTime startedAtUtc)
        => new()
        {
            StartedAtUtc = startedAtUtc,
            Machine = Environment.MachineName,
            Os = Environment.OSVersion.VersionString,
            Runtime = Environment.Version.ToString(),
            Commit = Program.TryGitCommit(),
        };

    private static void WriteSamples(
        string outputDirectory,
        DateTime startedAtUtc,
        IReadOnlyList<SoakSample> samples)
    {
        var csv = new StringBuilder();
        csv.AppendLine(
            "timestamp_utc,elapsed_seconds,host_private_bytes,group_private_bytes,host_working_set_bytes,group_working_set_bytes,host_handles,group_handles,host_threads,group_threads,host_gdi,host_user,process_count,responsive,root_alive,host_alive,child_crash");
        foreach (SoakSample sample in samples)
        {
            DateTime timestamp = startedAtUtc + TimeSpan.FromSeconds(sample.ElapsedSeconds);
            csv.Append(timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(sample.HostPrivateBytes).Append(',')
                .Append(sample.GroupPrivateBytes).Append(',')
                .Append(sample.HostWorkingSetBytes).Append(',')
                .Append(sample.GroupWorkingSetBytes).Append(',')
                .Append(sample.HostHandleCount).Append(',')
                .Append(sample.GroupHandleCount).Append(',')
                .Append(sample.HostThreadCount).Append(',')
                .Append(sample.GroupThreadCount).Append(',')
                .Append(sample.HostGdiObjects?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(sample.HostUserObjects?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(sample.GroupProcessCount).Append(',')
                .Append(sample.MainWindowResponsive).Append(',')
                .Append(sample.RootAlive).Append(',')
                .Append(sample.HostAlive).Append(',')
                .Append(sample.ChildCrashDetected)
                .AppendLine();
        }
        File.WriteAllText(Path.Combine(outputDirectory, "samples.csv"), csv.ToString());
    }

    private static void LogSample(int index, SoakSample sample)
    {
        Console.WriteLine(
            $"soak sample {index}: t={sample.ElapsedSeconds:0.0}s " +
            $"host={sample.HostPrivateBytes / 1024d / 1024d:0.0}MiB " +
            $"group={sample.GroupPrivateBytes / 1024d / 1024d:0.0}MiB " +
            $"handles={sample.GroupHandleCount} threads={sample.GroupThreadCount} " +
            $"gdi={sample.HostGdiObjects?.ToString() ?? "n/a"} " +
            $"user={sample.HostUserObjects?.ToString() ?? "n/a"} " +
            $"proc={sample.GroupProcessCount} responsive={sample.MainWindowResponsive}");
    }

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch { return false; }
    }

    private static long SafeLong(Func<long> read)
    {
        try { return Math.Max(0, read()); }
        catch { return 0; }
    }

    private static int SafeInt(Func<int> read)
    {
        try { return Math.Max(0, read()); }
        catch { return 0; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class ChildExitTracker : IDisposable
    {
        private readonly Dictionary<int, Process> _tracked = new();
        private bool _crashDetected;

        public bool Observe(IReadOnlySet<int> currentPids, int rootPid, int hostPid)
        {
            foreach (var pair in _tracked.ToArray())
            {
                if (currentPids.Contains(pair.Key))
                    continue;

                Process process = pair.Value;
                try
                {
                    if (process.HasExited && process.ExitCode != 0 && pair.Key != rootPid && pair.Key != hostPid)
                    {
                        _crashDetected = true;
                        Console.WriteLine($"soak process failure: child pid {pair.Key} exited with code {process.ExitCode}");
                    }
                }
                catch { }
                process.Dispose();
                _tracked.Remove(pair.Key);
            }

            foreach (int pid in currentPids)
            {
                if (_tracked.ContainsKey(pid))
                    continue;
                try { _tracked[pid] = Process.GetProcessById(pid); }
                catch { }
            }
            return _crashDetected;
        }

        public void Dispose()
        {
            foreach (Process process in _tracked.Values)
                process.Dispose();
            _tracked.Clear();
        }
    }

    private sealed class AppCrashLogProbe
    {
        private readonly string _path;
        private int _offset;
        private bool _detected;

        public AppCrashLogProbe(string path)
        {
            _path = path;
            try { _offset = File.Exists(path) ? File.ReadAllText(path).Length : 0; }
            catch { _offset = 0; }
        }

        public bool Observe()
        {
            if (_detected)
                return true;

            try
            {
                if (!File.Exists(_path))
                    return false;

                string text = File.ReadAllText(_path);
                int start = _offset <= text.Length ? _offset : 0;
                _offset = text.Length;
                foreach (string line in text.AsSpan(start).ToString().Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Contains("WebView2 process failed:", StringComparison.OrdinalIgnoreCase)
                        && (line.Contains("reason: Crashed", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("reason: Unexpected", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("reason: LaunchFailed", StringComparison.OrdinalIgnoreCase)))
                    {
                        _detected = true;
                        Console.WriteLine("soak process failure: WebView2 child crash reported by isolated app log");
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }
    }
}
