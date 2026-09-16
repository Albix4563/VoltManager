using System.Diagnostics;
using System.Diagnostics.PerformanceData;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.WindowsHarness;

internal static class AppBenchmarkRunner
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static int Run(HarnessOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppPath) || !File.Exists(options.AppPath))
            throw new FileNotFoundException("--app must point to VoltManager.exe", options.AppPath);

        string appPath = Path.GetFullPath(options.AppPath);
        string appDir = Path.GetDirectoryName(appPath)!;
        string validationRoot = Path.Combine(options.OutputDirectory, "isolated-appdata");
        Directory.CreateDirectory(validationRoot);

        Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, validationRoot);
        Environment.SetEnvironmentVariable(ValidationEnvironment.SuppressPowerVariable, "1");
        Environment.SetEnvironmentVariable(ValidationEnvironment.RendererVariable, options.Renderer);
        ValidationMetrics.Reset();

        string harnessExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Harness executable path unavailable.");
        string supervisorPath = ResolveSupervisorPath(appDir, options, harnessExe);
        bool usingSupervisor = !string.Equals(supervisorPath, harnessExe, StringComparison.OrdinalIgnoreCase);
        WriteBenchmarkSettings(validationRoot, options, harnessExe);

        string proxyState = Path.Combine(options.OutputDirectory, "app-proxy.json");
        try { File.Delete(proxyState); } catch { }

        Process? synthetic = null;
        Process? focusHelper = null;
        Process root = StartVoltManager(
            appPath,
            appDir,
            validationRoot,
            options,
            harnessExe,
            supervisorPath,
            usingSupervisor);
        try
        {
            using Process app = WaitForAppProcess(proxyState, root, TimeSpan.FromSeconds(20));
            IntPtr hwnd = options.Scenario.StartsWith("dashboard", StringComparison.OrdinalIgnoreCase)
                || options.Scenario.Equals("restore", StringComparison.OrdinalIgnoreCase)
                ? WaitForMainWindow(app, TimeSpan.FromSeconds(20))
                : IntPtr.Zero;

            if (options.Scenario.Equals("dashboard-active", StringComparison.OrdinalIgnoreCase))
                Activate(hwnd);
            else if (options.Scenario.Equals("dashboard-inactive", StringComparison.OrdinalIgnoreCase))
            {
                focusHelper = StartFocusHelper();
                Activate(WaitForMainWindow(focusHelper, TimeSpan.FromSeconds(10)));
            }
            else if (options.Scenario.Equals("protected-cpu", StringComparison.OrdinalIgnoreCase))
            {
                string loadOutput = Path.Combine(options.OutputDirectory, "synthetic-load");
                Directory.CreateDirectory(loadOutput);
                synthetic = StartSyntheticLoad(harnessExe, loadOutput,
                    options.SettleDuration + options.MeasureDuration + TimeSpan.FromSeconds(15));
            }
            else if (options.Scenario.Equals("restore", StringComparison.OrdinalIgnoreCase))
            {
                PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(1000);
            }

            Thread.Sleep(options.SettleDuration);

            double? restoreMs = null;
            double? freshDataMs = null;
            if (options.Scenario.Equals("restore", StringComparison.OrdinalIgnoreCase))
            {
                string logPath = Path.Combine(validationRoot, "VoltManager", "logs", "voltmanager.log");
                int logOffset = ReadTextLength(logPath);
                var sw = Stopwatch.StartNew();
                using var show = EventWaitHandle.OpenExisting(ValidationEnvironment.NamedObject("VoltManager_ShowWindow_Event"));
                show.Set();
                IntPtr restored = WaitForMainWindow(app, TimeSpan.FromSeconds(10));
                WaitForResponsive(restored, TimeSpan.FromSeconds(10));
                restoreMs = sw.Elapsed.TotalMilliseconds;
                if (WaitForTextAfterOffset(logPath, logOffset,
                        "Validation marker: fresh adaptive state published after navigation.",
                        TimeSpan.FromSeconds(3)))
                    freshDataMs = sw.Elapsed.TotalMilliseconds;
                sw.Stop();
            }

            BenchmarkRun run = Measure(root.Id, options.MeasureDuration, out Dictionary<string, long> providerActivity);
            run.Label = options.Label;
            run.Scenario = options.Scenario;
            run.Iteration = options.Iteration;
            run.Renderer = options.Renderer;
            run.SettledSeconds = options.SettleDuration.TotalSeconds;
            run.AppPath = appPath;
            run.SupervisorPath = usingSupervisor ? supervisorPath : "";
            run.SupervisorCommit = usingSupervisor
                ? FileVersionInfo.GetVersionInfo(supervisorPath).ProductVersion ?? "unknown"
                : "harness-fallback";
            run.Machine = Environment.MachineName;
            run.Os = Environment.OSVersion.VersionString;
            run.Runtime = Environment.Version.ToString();
            run.WebViewRuntime = TryReadWebViewRuntimeVersion();
            run.RestoreLatencyMs = restoreMs;
            run.FreshDataLatencyMs = freshDataMs;
            run.Commit = FileVersionInfo.GetVersionInfo(appPath).ProductVersion ?? "unknown";
            run.ProtectedWorkloadObserved = options.Scenario.Equals("protected-cpu", StringComparison.OrdinalIgnoreCase)
                ? WaitForProtectedWorkload(validationRoot, TimeSpan.FromSeconds(1))
                : null;
            run.SyntheticOperationsPerSecond = ReadSyntheticThroughput(options.OutputDirectory);
            run.ProviderActivity = providerActivity;

            string jsonPath = Path.Combine(options.OutputDirectory, "benchmark-run.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(run, JsonOptions));
            File.WriteAllText(Path.Combine(options.OutputDirectory, "benchmark-run.csv"), ToCsv(run));
            return 0;
        }
        finally
        {
            TrySignalShutdown();
            if (!root.WaitForExit(8000)) TryKill(root);
            if (synthetic != null) TryKill(synthetic);
            if (focusHelper != null) TryKill(focusHelper);
            root.Dispose();
            synthetic?.Dispose();
            focusHelper?.Dispose();
        }
    }

    private static void WriteBenchmarkSettings(string validationRoot, HarnessOptions options, string harnessExe)
    {
        var settings = new AppSettings
        {
            MasterAutomationEnabled = false,
            CloseToTray = true,
            WelcomeCompleted = true,
            TourCompleted = true,
            AutostartTaskSchemaVersion = StartupService.CurrentTaskSchemaVersion,
        };
        settings.AutoUpdates.Enabled = false;
        settings.AppPowerProfiles.Enabled = false;
        settings.PowerSourcePlan.Enabled = false;
        settings.ThermalGuard.Enabled = false;
        settings.IdlePowerGuard.Enabled = false;
        settings.StandbyAutoCleaner.Enabled = false;
        settings.HeavyAppDetection.Enabled = false;
        settings.HeavyAppDetection.PriorityApplicationPaths = options.Scenario.Equals("protected-cpu", StringComparison.OrdinalIgnoreCase)
            ? new List<string> { Path.GetFullPath(harnessExe) }
            : new List<string>();
        settings.Widgets.Enabled = options.Scenario.Equals("tray-widget", StringComparison.OrdinalIgnoreCase);
        foreach (WidgetItem item in settings.Widgets.Items)
            item.Enabled = settings.Widgets.Enabled && item.Type == "usage";

        string directory = Path.Combine(validationRoot, "VoltManager");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static Process StartVoltManager(
        string appPath,
        string appDir,
        string root,
        HarnessOptions options,
        string harnessExe,
        string supervisorPath,
        bool usingSupervisor)
    {
        string appDll = Path.ChangeExtension(appPath, ".dll");
        if (!File.Exists(appDll))
            throw new FileNotFoundException("VoltManager.dll was not found next to the benchmark apphost.", appDll);

        var psi = new ProcessStartInfo
        {
            FileName = supervisorPath,
            WorkingDirectory = appDir,
            UseShellExecute = false,
        };
        if (usingSupervisor)
        {
            psi.ArgumentList.Add("--reset-state");
            psi.ArgumentList.Add("--child");
            psi.ArgumentList.Add(harnessExe);
            psi.ArgumentList.Add("--");
        }
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("app-proxy");
        psi.ArgumentList.Add("--app");
        psi.ArgumentList.Add(appDll);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(options.OutputDirectory);
        psi.ArgumentList.Add("--scenario");
        psi.ArgumentList.Add(options.Scenario);
        psi.Environment[ValidationEnvironment.RootVariable] = root;
        psi.Environment[ValidationEnvironment.SuppressPowerVariable] = "1";
        psi.Environment[ValidationEnvironment.RendererVariable] = options.Renderer;
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch VoltManager benchmark process.");
    }

    private static string ResolveSupervisorPath(string appDir, HarnessOptions options, string harnessExe)
    {
        string requested = !string.IsNullOrWhiteSpace(options.SupervisorPath)
            ? Path.GetFullPath(options.SupervisorPath)
            : Path.Combine(appDir, "VoltManager.Supervisor.exe");
        return File.Exists(requested) ? requested : harnessExe;
    }

    private static Process WaitForAppProcess(string proxyState, Process root, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (root.HasExited)
                throw new InvalidOperationException($"Benchmark root exited before VoltManager started (exit {root.ExitCode}).");
            try
            {
                if (File.Exists(proxyState))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(proxyState));
                    int pid = doc.RootElement.GetProperty("pid").GetInt32();
                    Process process = Process.GetProcessById(pid);
                    if (!process.HasExited) return process;
                    process.Dispose();
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            catch (ArgumentException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("VoltManager proxy did not publish a live app process.");
    }

    private static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            }
            catch { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("Expected benchmark window did not become available.");
    }

    private static void WaitForResponsive(IntPtr hwnd, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (IsWindowVisible(hwnd) && !IsHungAppWindow(hwnd)) return;
            Thread.Sleep(50);
        }
        throw new TimeoutException("Restored VoltManager window did not become responsive.");
    }

    private static void Activate(IntPtr hwnd)
    {
        ShowWindow(hwnd, SwRestore);
        _ = SetForegroundWindow(hwnd);
        Thread.Sleep(300);
    }

    private static Process StartFocusHelper()
    {
        Process process = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start focus helper.");
        process.WaitForInputIdle(5000);
        return process;
    }

    private static Process StartSyntheticLoad(string harnessExe, string output, TimeSpan duration)
    {
        var psi = new ProcessStartInfo(harnessExe)
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("synthetic-cpu");
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(output);
        psi.ArgumentList.Add("--duration-seconds");
        psi.ArgumentList.Add(duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start synthetic CPU load.");
    }

    private static BenchmarkRun Measure(int rootPid, TimeSpan duration, out Dictionary<string, long> providerActivity)
    {
        var cpuSamples = new List<double>();
        var gpuSamples = new List<double>();
        var groupVramSamples = new List<long>();
        var privateSamples = new List<long>();
        var workingSamples = new List<long>();
        var privateWorkingSamples = new List<long>();
        int maxChildren = 0;
        using var privateWorkingSet = new PrivateWorkingSetSampler();
        using var groupGpu = new GroupGpuSampler();
        using var groupVram = new GroupVramSampler();
        using var vram = new VramCounterProvider();
        HashSet<int> warmPids = DescendantsAndSelf(rootPid);
        privateWorkingSet.Warm(warmPids);
        groupGpu.Warm();
        groupVram.Warm();
        _ = vram.Read(force: true);
        IReadOnlyDictionary<string, long> providersBefore = ValidationMetrics.Snapshot();
        TimeSpan previousCpu = TotalCpu(rootPid, out int _);
        DateTime previousAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            Thread.Sleep(1000);
            DateTime now = DateTime.UtcNow;
            TimeSpan totalCpu = TotalCpu(rootPid, out List<Process> processes);
            double wallSeconds = Math.Max(0.001, (now - previousAt).TotalSeconds);
            double cpu = Math.Max(0, (totalCpu - previousCpu).TotalSeconds / wallSeconds / Environment.ProcessorCount * 100);
            cpuSamples.Add(cpu);
            previousCpu = totalCpu;
            previousAt = now;
            maxChildren = Math.Max(maxChildren, processes.Count);
            privateSamples.Add(processes.Sum(SafePrivateBytes));
            workingSamples.Add(processes.Sum(SafeWorkingSet));
            HashSet<int> pids = processes.Select(p => p.Id).ToHashSet();
            privateWorkingSamples.Add(privateWorkingSet.Read(pids));
            double? gpu = groupGpu.Read(pids);
            if (gpu.HasValue) gpuSamples.Add(gpu.Value);
            long? groupVramBytes = groupVram.Read(pids);
            if (groupVramBytes.HasValue) groupVramSamples.Add(groupVramBytes.Value);
            _ = vram.Read();
            foreach (Process process in processes) process.Dispose();
        }
        VramMemorySnapshot finalVram = vram.Read(force: true);
        IReadOnlyDictionary<string, long> providersAfter = ValidationMetrics.Snapshot();
        providerActivity = providersAfter.ToDictionary(
            pair => pair.Key,
            pair => Math.Max(0, pair.Value - (providersBefore.TryGetValue(pair.Key, out long before) ? before : 0)),
            StringComparer.Ordinal);
        return new BenchmarkRun
        {
            CpuAveragePercent = cpuSamples.DefaultIfEmpty().Average(),
            CpuP95Percent = Percentile(cpuSamples, 0.95),
            GpuAveragePercent = gpuSamples.Count == 0 ? null : gpuSamples.Average(),
            GpuP95Percent = gpuSamples.Count == 0 ? null : Percentile(gpuSamples, 0.95),
            GpuMeasurementStatus = gpuSamples.Count == 0 ? "not_verified" : "measured",
            VramPressurePercent = finalVram.Available ? finalVram.PressurePercent : null,
            VramMeasurementStatus = finalVram.Available ? "measured" : "not_verified",
            GroupVramBytesAverage = groupVramSamples.Count == 0 ? null : (long)groupVramSamples.Average(value => (double)value),
            GroupVramBytesMax = groupVramSamples.Count == 0 ? null : groupVramSamples.Max(),
            GroupVramMeasurementStatus = groupVramSamples.Count == 0 ? "not_verified" : "measured",
            PrivateBytesAverage = (long)privateSamples.DefaultIfEmpty().Average(x => (double)x),
            PrivateBytesMax = privateSamples.DefaultIfEmpty().Max(),
            WorkingSetAverage = (long)workingSamples.DefaultIfEmpty().Average(x => (double)x),
            PrivateWorkingSetAverage = (long)privateWorkingSamples.DefaultIfEmpty().Average(x => (double)x),
            MaxProcessCount = maxChildren,
            SampleCount = cpuSamples.Count,
            MeasuredSeconds = sw.Elapsed.TotalSeconds,
        };
    }

    private static TimeSpan TotalCpu(int rootPid, out List<Process> processes)
    {
        processes = new List<Process>();
        TimeSpan total = TimeSpan.Zero;
        foreach (int pid in DescendantsAndSelf(rootPid))
        {
            try
            {
                Process process = Process.GetProcessById(pid);
                process.Refresh();
                total += process.TotalProcessorTime;
                processes.Add(process);
            }
            catch { }
        }
        return total;
    }

    private static TimeSpan TotalCpu(int rootPid, out int count)
    {
        TimeSpan total = TotalCpu(rootPid, out List<Process> processes);
        count = processes.Count;
        foreach (Process process in processes) process.Dispose();
        return total;
    }

    private static HashSet<int> DescendantsAndSelf(int rootPid)
    {
        var result = new HashSet<int> { rootPid };
        var parentByPid = SnapshotParents();
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in parentByPid)
                if (result.Contains(pair.Value) && result.Add(pair.Key)) changed = true;
        } while (changed);
        return result;
    }

    private static Dictionary<int, int> SnapshotParents()
    {
        var result = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do { result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    private static long SafePrivateBytes(Process process) { try { return process.PrivateMemorySize64; } catch { return 0; } }
    private static long SafeWorkingSet(Process process) { try { return process.WorkingSet64; } catch { return 0; } }

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        double[] ordered = values.OrderBy(x => x).ToArray();
        return ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * p) - 1, 0, ordered.Length - 1)];
    }

    private static bool? WaitForProtectedWorkload(string root, TimeSpan timeout)
    {
        string log = Path.Combine(root, "VoltManager", "logs", "voltmanager.log");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(log) && File.ReadAllText(log).Contains("Resource profile: Workload", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            Thread.Sleep(100);
        }
        return false;
    }

    private static double? ReadSyntheticThroughput(string output)
    {
        string path = Path.Combine(output, "synthetic-load", "synthetic-cpu.json");
        if (!File.Exists(path)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("operationsPerSecond").GetDouble();
        }
        catch { return null; }
    }

    private static int ReadTextLength(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Length : 0; }
        catch { return 0; }
    }

    private static bool WaitForTextAfterOffset(string path, int offset, string marker, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path);
                    int start = Math.Clamp(offset, 0, text.Length);
                    if (text.AsSpan(start).Contains(marker, StringComparison.Ordinal)) return true;
                }
            }
            catch { }
            Thread.Sleep(50);
        }
        return false;
    }

    private static string ToCsv(BenchmarkRun run)
    {
        string Header = "label,scenario,iteration,renderer,commit,supervisor_path,supervisor_commit,machine,os,runtime,webview_runtime,cpu_avg_pct,cpu_p95_pct,gpu_avg_pct,gpu_p95_pct,gpu_status,vram_pct,vram_status,group_vram_bytes_avg,group_vram_bytes_max,group_vram_status,private_bytes_avg,private_bytes_max,working_set_avg,private_working_set_avg,max_process_count,sample_count,settled_seconds,measured_seconds,restore_latency_ms,fresh_data_latency_ms,synthetic_ops_per_sec,protected_workload_observed,provider_monitor_ticks,provider_process_snapshots,provider_gpu_samples,provider_vram_samples,provider_hardware_rpc_reads,provider_ui_metric_publications";
        string Row = string.Join(',', new[]
        {
            Csv(run.Label), Csv(run.Scenario), run.Iteration.ToString(), Csv(run.Renderer), Csv(run.Commit),
            Csv(run.SupervisorPath), Csv(run.SupervisorCommit),
            Csv(run.Machine), Csv(run.Os), Csv(run.Runtime), Csv(run.WebViewRuntime),
            run.CpuAveragePercent.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            run.CpuP95Percent.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            run.GpuAveragePercent?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            run.GpuP95Percent?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            run.GpuMeasurementStatus,
            run.VramPressurePercent?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            run.VramMeasurementStatus,
            run.GroupVramBytesAverage?.ToString() ?? "",
            run.GroupVramBytesMax?.ToString() ?? "",
            run.GroupVramMeasurementStatus,
            run.PrivateBytesAverage.ToString(), run.PrivateBytesMax.ToString(), run.WorkingSetAverage.ToString(),
            run.PrivateWorkingSetAverage.ToString(), run.MaxProcessCount.ToString(), run.SampleCount.ToString(),
            run.SettledSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            run.MeasuredSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            run.RestoreLatencyMs?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            run.FreshDataLatencyMs?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            run.SyntheticOperationsPerSecond?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            run.ProtectedWorkloadObserved?.ToString() ?? "",
            Provider(run, "MonitorTicks"), Provider(run, "ProcessSnapshots"), Provider(run, "GpuSamples"),
            Provider(run, "VramSamples"), Provider(run, "HardwareRpcReads"), Provider(run, "UiMetricPublications"),
        });
        return Header + Environment.NewLine + Row + Environment.NewLine;
    }

    private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

    private static string TryReadWebViewRuntimeVersion()
    {
        try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return "unavailable"; }
    }

    private static string Provider(BenchmarkRun run, string name)
        => run.ProviderActivity.TryGetValue(name, out long value) ? value.ToString() : "0";

    private static void TrySignalShutdown()
    {
        try
        {
            using EventWaitHandle evt = EventWaitHandle.OpenExisting(ValidationEnvironment.NamedObject("VoltManager_Uninstall_Shutdown_Event"));
            evt.Set();
        }
        catch { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
}

internal sealed class PrivateWorkingSetSampler : IDisposable
{
    private readonly Dictionary<int, System.Diagnostics.PerformanceCounter> _counters = new();

    public void Warm(IReadOnlySet<int> pids) => EnsureCounters(pids);

    public long Read(IReadOnlySet<int> pids)
    {
        EnsureCounters(pids);
        long total = 0;
        foreach (var counter in _counters.Values)
        {
            try { total += (long)Math.Max(0, counter.NextValue()); } catch { }
        }
        return total;
    }

    private void EnsureCounters(IReadOnlySet<int> pids)
    {
        foreach (int stale in _counters.Keys.Where(pid => !pids.Contains(pid)).ToArray())
        {
            _counters[stale].Dispose();
            _counters.Remove(stale);
        }
        HashSet<int> missing = pids.Where(pid => !_counters.ContainsKey(pid)).ToHashSet();
        if (missing.Count == 0) return;
        foreach (var pair in FindInstances(missing))
        {
            _counters[pair.Key] = new System.Diagnostics.PerformanceCounter(
                "Process", "Working Set - Private", pair.Value, true);
        }
    }

    private static Dictionary<int, string> FindInstances(IReadOnlySet<int> pids)
    {
        var result = new Dictionary<int, string>();
        try
        {
            var category = new System.Diagnostics.PerformanceCounterCategory("Process");
            foreach (string instance in category.GetInstanceNames())
            {
                using var id = new System.Diagnostics.PerformanceCounter("Process", "ID Process", instance, true);
                int pid = (int)id.NextValue();
                if (pids.Contains(pid))
                {
                    result[pid] = instance;
                    if (result.Count == pids.Count) break;
                }
            }
        }
        catch { }
        return result;
    }

    public void Dispose()
    {
        foreach (var counter in _counters.Values) counter.Dispose();
        _counters.Clear();
    }
}

internal sealed class GroupGpuSampler : IDisposable
{
    private readonly Dictionary<string, System.Diagnostics.PerformanceCounter> _counters =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _available;

    public void Warm()
    {
        Refresh();
        foreach (var counter in _counters.Values)
        {
            try { _ = counter.NextValue(); } catch { }
        }
    }

    public double? Read(IReadOnlySet<int> pids)
    {
        if (!_available) return null;
        double total = 0;
        bool sampled = false;
        foreach (var pair in _counters)
        {
            int pid = GpuCounterProvider.TryParsePidFromInstanceName(pair.Key);
            if (pid == 0 || !pids.Contains(pid)) continue;
            try
            {
                total += Math.Max(0, pair.Value.NextValue());
                sampled = true;
            }
            catch { }
        }
        return sampled ? Math.Min(100, total) : 0;
    }

    private void Refresh()
    {
        try
        {
            var category = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
            foreach (string instance in category.GetInstanceNames().Where(GpuCounterProvider.IsGpu3DEngine))
            {
                if (_counters.ContainsKey(instance)) continue;
                _counters[instance] = new System.Diagnostics.PerformanceCounter(
                    "GPU Engine", "Utilization Percentage", instance, readOnly: true);
            }
            _available = _counters.Count > 0;
        }
        catch { _available = false; }
    }

    public void Dispose()
    {
        foreach (var counter in _counters.Values) counter.Dispose();
        _counters.Clear();
    }
}

internal sealed class GroupVramSampler : IDisposable
{
    private readonly Dictionary<string, System.Diagnostics.PerformanceCounter> _counters =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _available;

    public void Warm()
    {
        Refresh();
        foreach (var counter in _counters.Values)
        {
            try { _ = counter.NextSample(); } catch { }
        }
    }

    public long? Read(IReadOnlySet<int> pids)
    {
        if (!_available) return null;
        long total = 0;
        bool sampled = false;
        foreach (var pair in _counters)
        {
            int pid = GpuCounterProvider.TryParsePidFromInstanceName(pair.Key);
            if (pid == 0 || !pids.Contains(pid)) continue;
            try
            {
                total += Math.Max(0, pair.Value.NextSample().RawValue);
                sampled = true;
            }
            catch { }
        }
        return sampled ? total : 0;
    }

    private void Refresh()
    {
        try
        {
            var category = new System.Diagnostics.PerformanceCounterCategory("GPU Process Memory");
            foreach (string instance in category.GetInstanceNames())
            {
                if (GpuCounterProvider.TryParsePidFromInstanceName(instance) == 0 || _counters.ContainsKey(instance))
                    continue;
                _counters[instance] = new System.Diagnostics.PerformanceCounter(
                    "GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
            }
            _available = _counters.Count > 0;
        }
        catch { _available = false; }
    }

    public void Dispose()
    {
        foreach (var counter in _counters.Values) counter.Dispose();
        _counters.Clear();
    }
}

internal sealed class BenchmarkRun
{
    public string Label { get; set; } = "";
    public string Scenario { get; set; } = "";
    public int Iteration { get; set; }
    public string Renderer { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string Commit { get; set; } = "";
    public string SupervisorPath { get; set; } = "";
    public string SupervisorCommit { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Os { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string WebViewRuntime { get; set; } = "";
    public double CpuAveragePercent { get; set; }
    public double CpuP95Percent { get; set; }
    public double? GpuAveragePercent { get; set; }
    public double? GpuP95Percent { get; set; }
    public string GpuMeasurementStatus { get; set; } = "not_verified";
    public double? VramPressurePercent { get; set; }
    public string VramMeasurementStatus { get; set; } = "not_verified";
    public long? GroupVramBytesAverage { get; set; }
    public long? GroupVramBytesMax { get; set; }
    public string GroupVramMeasurementStatus { get; set; } = "not_verified";
    public long PrivateBytesAverage { get; set; }
    public long PrivateBytesMax { get; set; }
    public long WorkingSetAverage { get; set; }
    public long PrivateWorkingSetAverage { get; set; }
    public int MaxProcessCount { get; set; }
    public int SampleCount { get; set; }
    public double SettledSeconds { get; set; }
    public double MeasuredSeconds { get; set; }
    public double? RestoreLatencyMs { get; set; }
    public double? FreshDataLatencyMs { get; set; }
    public double? SyntheticOperationsPerSecond { get; set; }
    public bool? ProtectedWorkloadObserved { get; set; }
    public Dictionary<string, long> ProviderActivity { get; set; } = new(StringComparer.Ordinal);
}
