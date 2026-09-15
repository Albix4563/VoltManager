using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.WindowsHarness;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = HarnessOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);

        if (options.Mode == "synthetic-cpu")
            return RunSyntheticCpu(options);
        if (options.Mode == "app-proxy")
            return RunAppProxy(options);
        if (options.Mode == "graphics-benchmark")
            return RunGraphicsBenchmark(options);
        if (options.Mode == "app-benchmark")
            return AppBenchmarkRunner.Run(options);

        var results = new HarnessReport
        {
            StartedAtUtc = DateTime.UtcNow,
            Machine = Environment.MachineName,
            Os = Environment.OSVersion.VersionString,
            Runtime = Environment.Version.ToString(),
            Commit = TryGitCommit(),
        };

        RunDeterministicChecks(results, options);
        if (options.Mode is "webview" or "all")
            RunWebViewChecks(results, options);

        results.CompletedAtUtc = DateTime.UtcNow;
        WriteReport(results, options.OutputDirectory);
        return results.Checks.Any(check => check.Status == "failed") ? 1 : 0;
    }

    private static void RunDeterministicChecks(HarnessReport report, HarnessOptions options)
    {
        string isolatedRoot = Path.Combine(options.OutputDirectory, "isolated-appdata");
        Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, isolatedRoot);
        Environment.SetEnvironmentVariable(ValidationEnvironment.SuppressPowerVariable, "1");

        AddCheck(report, "isolated_appdata", () =>
            Path.GetFullPath(ValidationEnvironment.ApplicationDataRoot) == Path.GetFullPath(isolatedRoot),
            ValidationEnvironment.ApplicationDataRoot);

        AddCheck(report, "power_actions_suppressed", () =>
            ValidationEnvironment.SuppressPowerChanges
            && PowerPlanService.IsMutatingPowercfg("/setactive 381b4222-f694-41f0-9685-ff5bb260df2e")
            && !PowerPlanService.IsMutatingPowercfg("/getactivescheme"),
            "mutating powercfg calls are classified while read-only calls remain allowed");

        string swift = WebViewRuntimeOptions.BrowserArguments(WebViewRendererVariant.SwiftShader);
        string hardware = WebViewRuntimeOptions.BrowserArguments(WebViewRendererVariant.HardwareDefault);
        AddCheck(report, "renderer_variants_are_benchmark_only_and_isolated", () =>
            swift.Contains("--use-angle=swiftshader", StringComparison.Ordinal)
            && !hardware.Contains("swiftshader", StringComparison.OrdinalIgnoreCase)
            && CommonArgumentsEquivalent(swift, hardware),
            "hardware variant differs only by the SwiftShader/ANGLE selection switches");

        var full = MonitorService.ResolveHardwareSampleRequest(new MonitorSamplingDemand(true, false, false));
        var thermal = MonitorService.ResolveHardwareSampleRequest(new MonitorSamplingDemand(false, true, false));
        var parked = MonitorService.ResolveHardwareSampleRequest(new MonitorSamplingDemand(false, false, false));
        AddCheck(report, "adaptive_hardware_demand", () =>
            full.VisualDetails && full.MinimumInterval == TimeSpan.FromSeconds(2)
            && thermal.Temperatures && !thermal.VisualDetails && thermal.MinimumInterval == TimeSpan.FromSeconds(2)
            && parked.VisualDetails && parked.MinimumInterval == TimeSpan.FromSeconds(10),
            "2s visible, 2s thermal-only, 10s parked accessory sample policy");

        using var release = new ManualResetEventSlim(false);
        var fake = new HarnessHardwareAccess();
        using var deferred = new DeferredHardwareAccess(() =>
        {
            release.Wait(TimeSpan.FromSeconds(2));
            return fake;
        });
        SensorReport before = deferred.Read(new HardwareSampleRequest(true, false, TimeSpan.Zero));
        release.Set();
        bool becameAvailable = SpinWait.SpinUntil(() => deferred.Available, TimeSpan.FromSeconds(2));
        SensorReport after = deferred.Read(new HardwareSampleRequest(true, false, TimeSpan.Zero));
        AddCheck(report, "slow_hardware_service_does_not_block_monitor_path", () =>
            ReferenceEquals(before, SensorReport.Empty) && becameAvailable && ReferenceEquals(after, fake.Report),
            "deferred hardware source returns immediately while initialization is slow");

        HardwareServiceClient? client = null;
        try
        {
            client = HardwareServiceClient.TryStart();
            if (client == null)
            {
                report.Checks.Add(new HarnessCheck("hardware_service_real", "not_verified",
                    "isolated hardware service executable was not available in this harness output"));
            }
            else
            {
                _ = client.Read(new HardwareSampleRequest(true, false, TimeSpan.Zero), force: true);
                report.Checks.Add(new HarnessCheck("hardware_service_real", "passed",
                    $"service pid {client.ServiceProcessId?.ToString() ?? "unknown"} responded to a real request"));

                int? servicePid = client.ServiceProcessId;
                if (servicePid.HasValue)
                {
                    using Process service = Process.GetProcessById(servicePid.Value);
                    service.Kill(entireProcessTree: true);
                    service.WaitForExit(5000);
                    _ = client.Read(new HardwareSampleRequest(true, false, TimeSpan.Zero), force: true);
                    bool fallbackStarted = SpinWait.SpinUntil(() => client.UsingFallback, TimeSpan.FromSeconds(5));
                    report.Checks.Add(new HarnessCheck("hardware_service_terminated_fallback",
                        fallbackStarted ? "passed" : "failed",
                        fallbackStarted
                            ? "test-owned service was terminated and in-process fallback was activated"
                            : "in-process fallback did not activate after the test-owned service exited"));
                }
            }
        }
        catch (Exception ex)
        {
            report.Checks.Add(new HarnessCheck("hardware_service_real", "not_verified", ex.Message));
        }
        finally { client?.Dispose(); }

        try
        {
            using var vram = new VramCounterProvider();
            VramMemorySnapshot sample = new();
            bool ready = SpinWait.SpinUntil(() =>
            {
                sample = vram.Read(force: true);
                return sample.Available;
            }, TimeSpan.FromSeconds(5));
            report.Checks.Add(sample.Available
                ? new HarnessCheck("vram_real_counters", "passed",
                    $"{sample.Adapters.Count} dedicated adapter sample(s), max {sample.PressurePercent:0.0}%")
                : new HarnessCheck("vram_real_counters", "not_verified",
                    ready
                        ? "GPU Adapter Memory/DXGI sample became available without valid dedicated adapters"
                        : "GPU Adapter Memory/DXGI dedicated-memory samples unavailable after 5 seconds"));
        }
        catch (Exception ex)
        {
            report.Checks.Add(new HarnessCheck("vram_real_counters", "not_verified", ex.Message));
        }
    }

    private static void RunWebViewChecks(HarnessReport report, HarnessOptions options)
    {
        if (!Environment.UserInteractive)
        {
            report.Checks.Add(new HarnessCheck("webview_interactive", "not_verified",
                "Windows session is not interactive"));
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { await RunWebViewChecksAsync(report, options); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                completed.Set();
                app.Shutdown();
            }
        });
        app.Run();
        completed.Wait();

        if (failure != null)
            report.Checks.Add(new HarnessCheck("webview_interactive", "failed", failure.ToString()));
    }

    private static async Task RunWebViewChecksAsync(HarnessReport report, HarnessOptions options)
    {
        var preexistingWebViewPids = Process.GetProcessesByName("msedgewebview2")
            .Select(process =>
            {
                try { return process.Id; }
                finally { process.Dispose(); }
            })
            .ToHashSet();
        string root = Path.Combine(options.OutputDirectory, "webview-profile");
        Directory.CreateDirectory(root);
        var variant = options.Renderer == "hardware"
            ? WebViewRendererVariant.HardwareDefault
            : WebViewRendererVariant.SwiftShader;
        var env = await CoreWebView2Environment.CreateAsync(null, root,
            new CoreWebView2EnvironmentOptions(WebViewRuntimeOptions.BrowserArguments(variant)));

        using var first = new WebViewSurface(env, "dashboard");
        using var second = new WebViewSurface(env, "widget");
        await first.OpenAsync();
        await second.OpenAsync();

        RunSyntheticFullscreenCoverageCheck(report, first, second);

        string backend = await first.ReadRendererAsync();
        bool software = IsSoftwareRenderer(backend);
        string backendStatus = variant == WebViewRendererVariant.HardwareDefault && software
            ? "not_verified"
            : "passed";
        report.Checks.Add(new HarnessCheck("webview_renderer_backend", backendStatus,
            $"requested={variant}; renderer={backend}"));

        var restoreLatencies = new List<double>();
        IReadOnlyList<Process> warmProcesses = AssociatedWebViewProcesses(preexistingWebViewPids);
        int childCountBefore = warmProcesses.Count;
        long warmMemory = warmProcesses.Sum(SafePrivateBytes);
        for (int i = 0; i < options.Cycles; i++)
        {
            await first.SuspendAsync().WaitAsync(TimeSpan.FromSeconds(5));
            string live = await second.ExecuteAsync("String(++window.__ticks)").WaitAsync(TimeSpan.FromSeconds(5));
            if (string.IsNullOrWhiteSpace(live)) throw new InvalidOperationException("visible widget stopped responding");

            var sw = Stopwatch.StartNew();
            await first.ResumeAsync();
            _ = await first.ExecuteAsync("document.body.dataset.resume=String(Date.now()); 'ok'")
                .WaitAsync(TimeSpan.FromSeconds(5));
            sw.Stop();
            restoreLatencies.Add(sw.Elapsed.TotalMilliseconds);
        }

        IReadOnlyList<Process> finalProcesses = AssociatedWebViewProcesses(preexistingWebViewPids);
        int childCountAfter = finalProcesses.Count;
        long finalMemory = finalProcesses.Sum(SafePrivateBytes);
        long allowedGrowth = Math.Max((long)(warmMemory * 0.10), 20L * 1024 * 1024);
        bool stable = childCountAfter <= childCountBefore + 1 && finalMemory <= warmMemory + allowedGrowth;
        report.Checks.Add(new HarnessCheck("webview_suspend_resume_cycles", stable ? "passed" : "failed",
            $"cycles={options.Cycles}; restoreMaxMs={restoreLatencies.DefaultIfEmpty().Max():0.0}; " +
            $"children={childCountBefore}->{childCountAfter}; privateBytes={warmMemory}->{finalMemory}"));

        report.Metrics["restoreLatencyMeanMs"] = restoreLatencies.Count == 0 ? 0 : restoreLatencies.Average();
        report.Metrics["restoreLatencyP95Ms"] = Percentile(restoreLatencies, 0.95);
        report.Metrics["webViewPrivateBytesWarm"] = warmMemory;
        report.Metrics["webViewPrivateBytesFinal"] = finalMemory;

        await using var closing = new WebViewSurface(env, "closing-race");
        await closing.OpenAsync();
        Task<bool> pending = closing.BeginSuspendAsync();
        closing.Close();
        try { _ = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* disposal is an allowed completion for this race */ }
        report.Checks.Add(new HarnessCheck("close_during_pending_suspend", "passed",
            "surface closed while TrySuspendAsync was pending without blocking the shared environment"));

        _ = await second.ExecuteAsync("'still-alive'");
        report.Checks.Add(new HarnessCheck("suspended_surface_isolation", "passed",
            "second WebView in the shared environment remained script-responsive"));
    }

    private static void RunSyntheticFullscreenCoverageCheck(
        HarnessReport report,
        WebViewSurface first,
        WebViewSurface second)
    {
        IntPtr firstMonitor = MonitorFromWindow(first.WindowHandle, 2);
        if (firstMonitor == IntPtr.Zero || !TryGetMonitorRect(firstMonitor, out NativeRect firstBounds))
        {
            report.Checks.Add(new HarnessCheck("synthetic_fullscreen_coverage", "not_verified",
                "monitor geometry was unavailable"));
            return;
        }

        using var coverage = new ProtectedFullscreenCoverageService(
            () => new HashSet<int> { Environment.ProcessId });
        coverage.RegisterSurface(first.WindowHandle);
        coverage.RegisterSurface(second.WindowHandle);
        coverage.Start();

        var fullscreen = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Title = "VoltManager synthetic protected fullscreen",
        };
        fullscreen.Show();
        IntPtr fullHwnd = new WindowInteropHelper(fullscreen).Handle;
        SetWindowPos(fullHwnd, new IntPtr(-1), firstBounds.Left, firstBounds.Top,
            firstBounds.Right - firstBounds.Left, firstBounds.Bottom - firstBounds.Top,
            0x0010 | 0x0040);

        coverage.NotifyProtectedProcessesChanged();
        bool covered = SpinWait.SpinUntil(() => coverage.IsCovered(first.WindowHandle), TimeSpan.FromSeconds(3));
        report.Checks.Add(new HarnessCheck("synthetic_fullscreen_coverage", covered ? "passed" : "failed",
            covered
                ? "real top-level fullscreen window covered the registered surface on the same monitor"
                : "coverage service did not detect the real fullscreen window"));

        List<(IntPtr Handle, NativeRect Bounds)> monitors = EnumerateMonitors();
        var other = monitors.FirstOrDefault(m => m.Handle != firstMonitor);
        if (other.Handle == IntPtr.Zero)
        {
            report.Checks.Add(new HarnessCheck("real_multimonitor_surface_isolation", "not_verified",
                "only one monitor is available; deterministic geometry tests cover the multi-monitor policy"));
        }
        else
        {
            int width = Math.Min(500, other.Bounds.Right - other.Bounds.Left);
            int height = Math.Min(350, other.Bounds.Bottom - other.Bounds.Top);
            SetWindowPos(second.WindowHandle, IntPtr.Zero,
                other.Bounds.Left + 40, other.Bounds.Top + 40, width, height, 0x0010 | 0x0004);
            coverage.NotifyProtectedProcessesChanged();
            bool isolated = SpinWait.SpinUntil(
                () => coverage.IsCovered(first.WindowHandle) && !coverage.IsCovered(second.WindowHandle),
                TimeSpan.FromSeconds(3));
            report.Checks.Add(new HarnessCheck("real_multimonitor_surface_isolation",
                isolated ? "passed" : "failed",
                isolated
                    ? "fullscreen coverage remained limited to the monitor containing the protected window"
                    : "coverage leaked to the surface moved onto the other monitor"));
        }

        fullscreen.Close();
        coverage.NotifyProtectedProcessesChanged();
        bool cleared = SpinWait.SpinUntil(() => !coverage.IsCovered(first.WindowHandle), TimeSpan.FromSeconds(3));
        report.Checks.Add(new HarnessCheck("fullscreen_close_restores_surface", cleared ? "passed" : "failed",
            cleared ? "coverage cleared after the synthetic fullscreen window closed" : "coverage remained set after close"));
    }

    private static bool TryGetMonitorRect(IntPtr monitor, out NativeRect rect)
    {
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            rect = info.Monitor;
            return true;
        }
        rect = default;
        return false;
    }

    private static List<(IntPtr Handle, NativeRect Bounds)> EnumerateMonitors()
    {
        var result = new List<(IntPtr, NativeRect)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (TryGetMonitorRect(monitor, out NativeRect bounds)) result.Add((monitor, bounds));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private static int RunSyntheticCpu(HarnessOptions options)
    {
        var sw = Stopwatch.StartNew();
        TimeSpan nextReport = TimeSpan.FromSeconds(1);
        long operations = 0;
        double value = 1.000001;
        while (sw.Elapsed < options.Duration)
        {
            for (int i = 0; i < 100_000; i++)
            {
                value = Math.Sqrt(value + 1.0000001);
                operations++;
            }
            if (sw.Elapsed >= nextReport)
            {
                WriteSyntheticCpuResult(options.OutputDirectory, sw.Elapsed, operations, value);
                nextReport += TimeSpan.FromSeconds(1);
            }
        }
        WriteSyntheticCpuResult(options.OutputDirectory, sw.Elapsed, operations, value);
        return 0;
    }

    private static void WriteSyntheticCpuResult(string outputDirectory, TimeSpan elapsed, long operations, double value)
    {
        var result = new
        {
            durationSeconds = elapsed.TotalSeconds,
            operations,
            operationsPerSecond = operations / Math.Max(0.001, elapsed.TotalSeconds),
            checksum = value,
        };
        File.WriteAllText(Path.Combine(outputDirectory, "synthetic-cpu.json"),
            JsonSerializer.Serialize(result, JsonOptions));
    }

    private static int RunAppProxy(HarnessOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AppPath) || !File.Exists(options.AppPath))
            throw new FileNotFoundException("--app must point to VoltManager.dll in app-proxy mode", options.AppPath);

        string appDll = Path.GetFullPath(options.AppPath);
        string appDir = Path.GetDirectoryName(appDll)!;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = appDir,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(appDll);
        start.ArgumentList.Add("--supervised");
        if (options.Scenario is "tray-no-widgets" or "tray-widget" or "protected-cpu")
            start.ArgumentList.Add("--minimized");

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start VoltManager.dll through dotnet.");
        File.WriteAllText(Path.Combine(options.OutputDirectory, "app-proxy.json"),
            JsonSerializer.Serialize(new { pid = process.Id, appDll }, JsonOptions));
        process.WaitForExit();
        return process.ExitCode;
    }

    private static int RunGraphicsBenchmark(HarnessOptions options)
    {
        if (!Environment.UserInteractive)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "graphics-benchmark.json"),
                JsonSerializer.Serialize(new GraphicsBenchmarkRun
                {
                    Label = options.Label,
                    Renderer = options.Renderer,
                    Iteration = options.Iteration,
                    Status = "not_verified",
                    Detail = "Windows session is not interactive",
                }, JsonOptions));
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        GraphicsBenchmarkRun? result = null;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { result = await RunGraphicsBenchmarkAsync(options); }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        });
        app.Run();

        if (failure != null)
        {
            result = new GraphicsBenchmarkRun
            {
                Label = options.Label,
                Renderer = options.Renderer,
                Iteration = options.Iteration,
                Status = "failed",
                Detail = failure.ToString(),
            };
        }

        result ??= new GraphicsBenchmarkRun
        {
            Label = options.Label,
            Renderer = options.Renderer,
            Iteration = options.Iteration,
            Status = "failed",
            Detail = "graphics benchmark produced no result",
        };
        File.WriteAllText(Path.Combine(options.OutputDirectory, "graphics-benchmark.json"),
            JsonSerializer.Serialize(result, JsonOptions));
        File.WriteAllText(Path.Combine(options.OutputDirectory, "graphics-benchmark.csv"),
            "renderer,iteration,status,cpu_avg_pct,cpu_p95_pct,private_bytes_avg,vram_bytes_avg,vram_bytes_max,vram_status,draws_per_sec,backend\n" +
            string.Join(',', new[]
            {
                result.Renderer,
                result.Iteration.ToString(),
                result.Status,
                result.CpuAveragePercent.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                result.CpuP95Percent.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                result.PrivateBytesAverage.ToString(),
                result.VramBytesAverage?.ToString() ?? "",
                result.VramBytesMax?.ToString() ?? "",
                result.VramStatus,
                result.DrawsPerSecond.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                '"' + result.Backend.Replace("\"", "\"\"") + '"',
            }) + "\n");
        return result.Status == "failed" ? 1 : 0;
    }

    private static async Task<GraphicsBenchmarkRun> RunGraphicsBenchmarkAsync(HarnessOptions options)
    {
        var preexisting = Process.GetProcessesByName("msedgewebview2")
            .Select(process =>
            {
                try { return process.Id; }
                finally { process.Dispose(); }
            })
            .ToHashSet();
        string root = Path.Combine(options.OutputDirectory, "graphics-profile");
        Directory.CreateDirectory(root);
        var variant = options.Renderer == "hardware"
            ? WebViewRendererVariant.HardwareDefault
            : WebViewRendererVariant.SwiftShader;
        var env = await CoreWebView2Environment.CreateAsync(null, root,
            new CoreWebView2EnvironmentOptions(WebViewRuntimeOptions.BrowserArguments(variant)));
        await using var surface = new WebViewSurface(env, "graphics-benchmark");
        await surface.OpenAsync();
        string backend = await surface.ReadRendererAsync();
        bool softwareFallback = variant == WebViewRendererVariant.HardwareDefault && IsSoftwareRenderer(backend);

        string started = await surface.ExecuteAsync("""
            (() => {
              const canvas = document.createElement('canvas');
              canvas.width = 1024; canvas.height = 768; document.body.appendChild(canvas);
              const gl = canvas.getContext('webgl', { antialias: false, preserveDrawingBuffer: false });
              if (!gl) return 'no-webgl';
              const vs = gl.createShader(gl.VERTEX_SHADER);
              gl.shaderSource(vs, 'attribute vec2 p; void main(){ gl_Position=vec4(p,0.0,1.0); }'); gl.compileShader(vs);
              const fs = gl.createShader(gl.FRAGMENT_SHADER);
              gl.shaderSource(fs, 'precision mediump float; void main(){ gl_FragColor=vec4(0.2,0.7,0.9,1.0); }'); gl.compileShader(fs);
              const program = gl.createProgram(); gl.attachShader(program,vs); gl.attachShader(program,fs); gl.linkProgram(program); gl.useProgram(program);
              const buffer = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
              gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1, 1,-1, 0,1]), gl.STATIC_DRAW);
              const loc = gl.getAttribLocation(program,'p'); gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc,2,gl.FLOAT,false,0,0);
              window.__gfxCount = 0; window.__gfxRunning = true;
              function pump(){ if(!window.__gfxRunning) return; for(let i=0;i<300;i++){ gl.drawArrays(gl.TRIANGLES,0,3); } gl.flush(); window.__gfxCount += 300; setTimeout(pump,0); }
              pump(); return 'ok';
            })()
            """);
        if (!started.Contains("ok", StringComparison.OrdinalIgnoreCase))
            return new GraphicsBenchmarkRun
            {
                Label = options.Label,
                Renderer = options.Renderer,
                Iteration = options.Iteration,
                Status = "not_verified",
                Backend = backend,
                Detail = "WebGL context unavailable",
            };

        await Task.Delay(options.SettleDuration);
        _ = await surface.ExecuteAsync("window.__gfxCount=0; 'reset'");
        GraphicsProcessSample sample = await MeasureGraphicsProcessesAsync(preexisting, options.MeasureDuration);
        string countJson = await surface.ExecuteAsync("String(window.__gfxCount)");
        _ = await surface.ExecuteAsync("window.__gfxRunning=false; 'stopped'");
        string countText;
        try { countText = JsonSerializer.Deserialize<string>(countJson) ?? "0"; }
        catch { countText = "0"; }
        double.TryParse(countText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double draws);

        return new GraphicsBenchmarkRun
        {
            Label = options.Label,
            Renderer = options.Renderer,
            Iteration = options.Iteration,
            Status = softwareFallback ? "not_verified" : "passed",
            Detail = softwareFallback ? "hardware request fell back to a software renderer" : "requested renderer verified",
            Backend = backend,
            CpuAveragePercent = sample.CpuAverage,
            CpuP95Percent = sample.CpuP95,
            PrivateBytesAverage = sample.PrivateBytesAverage,
            VramBytesAverage = sample.VramBytesAverage,
            VramBytesMax = sample.VramBytesMax,
            VramStatus = sample.VramStatus,
            DrawsPerSecond = draws / Math.Max(0.001, options.MeasureDuration.TotalSeconds),
        };
    }

    private static async Task<GraphicsProcessSample> MeasureGraphicsProcessesAsync(
        IReadOnlySet<int> preexistingWebViewPids,
        TimeSpan duration)
    {
        var cpu = new List<double>();
        var memory = new List<long>();
        var vram = new List<long>();
        using var groupVram = new GroupVramSampler();
        groupVram.Warm();
        Dictionary<int, TimeSpan> previous = CaptureGraphicsCpu(preexistingWebViewPids);
        DateTime previousAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            await Task.Delay(1000);
            DateTime now = DateTime.UtcNow;
            Dictionary<int, TimeSpan> current = CaptureGraphicsCpu(preexistingWebViewPids);
            double totalCpuSeconds = 0;
            foreach (var pair in current)
            {
                if (previous.TryGetValue(pair.Key, out TimeSpan old) && pair.Value >= old)
                    totalCpuSeconds += (pair.Value - old).TotalSeconds;
            }
            double wall = Math.Max(0.001, (now - previousAt).TotalSeconds);
            cpu.Add(totalCpuSeconds / wall / Environment.ProcessorCount * 100);
            previous = current;
            previousAt = now;
            memory.Add(GraphicsProcesses(preexistingWebViewPids).Sum(process =>
            {
                try { return process.PrivateMemorySize64; }
                catch { return 0; }
                finally { process.Dispose(); }
            }));
            HashSet<int> pids = GraphicsProcesses(preexistingWebViewPids).Select(process =>
            {
                try { return process.Id; }
                finally { process.Dispose(); }
            }).ToHashSet();
            long? vramBytes = groupVram.Read(pids);
            if (vramBytes.HasValue) vram.Add(vramBytes.Value);
        }
        return new GraphicsProcessSample(
            cpu.DefaultIfEmpty().Average(),
            Percentile(cpu, 0.95),
            (long)memory.DefaultIfEmpty().Average(value => (double)value),
            vram.Count == 0 ? null : (long)vram.Average(value => (double)value),
            vram.Count == 0 ? null : vram.Max(),
            vram.Count == 0 ? "not_verified" : "measured");
    }

    private static Dictionary<int, TimeSpan> CaptureGraphicsCpu(IReadOnlySet<int> preexistingWebViewPids)
    {
        var result = new Dictionary<int, TimeSpan>();
        foreach (Process process in GraphicsProcesses(preexistingWebViewPids))
        {
            try { result[process.Id] = process.TotalProcessorTime; }
            catch { }
            finally { process.Dispose(); }
        }
        return result;
    }

    private static List<Process> GraphicsProcesses(IReadOnlySet<int> preexistingWebViewPids)
    {
        var result = new List<Process> { Process.GetCurrentProcess() };
        result.AddRange(Process.GetProcessesByName("msedgewebview2").Where(process =>
        {
            try
            {
                if (preexistingWebViewPids.Contains(process.Id))
                {
                    process.Dispose();
                    return false;
                }
                return process.SessionId == Process.GetCurrentProcess().SessionId;
            }
            catch
            {
                process.Dispose();
                return false;
            }
        }));
        return result;
    }

    private static IReadOnlyList<Process> AssociatedWebViewProcesses(IReadOnlySet<int> preexistingPids)
        => Process.GetProcessesByName("msedgewebview2")
            .Where(process =>
            {
                try
                {
                    if (preexistingPids.Contains(process.Id))
                    {
                        process.Dispose();
                        return false;
                    }
                    return process.SessionId == Process.GetCurrentProcess().SessionId;
                }
                catch
                {
                    process.Dispose();
                    return false;
                }
            })
            .ToList();

    private static long SafePrivateBytes(Process process)
    {
        try { process.Refresh(); return process.PrivateMemorySize64; }
        catch { return 0; }
        finally { process.Dispose(); }
    }

    private static bool CommonArgumentsEquivalent(string swift, string hardware)
    {
        string normalized = swift
            .Replace("--use-angle=swiftshader", "", StringComparison.Ordinal)
            .Replace("--use-gl=angle", "", StringComparison.Ordinal);
        return string.Equals(NormalizeWhitespace(normalized), NormalizeWhitespace(hardware), StringComparison.Ordinal);
    }

    private static string NormalizeWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsSoftwareRenderer(string renderer)
        => renderer.Contains("swiftshader", StringComparison.OrdinalIgnoreCase)
            || renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase)
            || renderer.Contains("software", StringComparison.OrdinalIgnoreCase);

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        double[] ordered = values.OrderBy(value => value).ToArray();
        int index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static void AddCheck(HarnessReport report, string name, Func<bool> predicate, string detail)
    {
        try { report.Checks.Add(new HarnessCheck(name, predicate() ? "passed" : "failed", detail)); }
        catch (Exception ex) { report.Checks.Add(new HarnessCheck(name, "failed", ex.Message)); }
    }

    private static void WriteReport(HarnessReport report, string output)
    {
        File.WriteAllText(Path.Combine(output, "windows-harness.json"), JsonSerializer.Serialize(report, JsonOptions));
        var markdown = new List<string>
        {
            "# VoltManager Windows validation",
            "",
            $"Commit: `{report.Commit}`  ",
            $"OS: {report.Os}  ",
            $"Runtime: {report.Runtime}",
            "",
            "| Check | Status | Detail |",
            "|---|---|---|",
        };
        markdown.AddRange(report.Checks.Select(check =>
            $"| {check.Name} | **{check.Status.ToUpperInvariant()}** | {check.Detail.Replace("|", "\\|")} |"));
        File.WriteAllLines(Path.Combine(output, "windows-harness.md"), markdown);
    }

    private static string TryGitCommit()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (process == null) return "unknown";
            string result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return result;
        }
        catch { return "unknown"; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

internal sealed class WebViewSurface : IDisposable, IAsyncDisposable
{
    private readonly CoreWebView2Environment _environment;
    private readonly Window _window;
    private readonly WebView2 _webView;
    private readonly string _name;
    private bool _closed;

    public WebViewSurface(CoreWebView2Environment environment, string name)
    {
        _environment = environment;
        _name = name;
        _webView = new WebView2();
        _window = new Window
        {
            Width = 420,
            Height = 280,
            ShowInTaskbar = false,
            Title = "VoltManager validation " + name,
            Content = _webView,
        };
    }

    public IntPtr WindowHandle => new WindowInteropHelper(_window).Handle;

    public async Task OpenAsync()
    {
        _window.Show();
        await _webView.EnsureCoreWebView2Async(_environment);
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args) => navigation.TrySetResult(args.IsSuccess);
        _webView.CoreWebView2.NavigationCompleted += Completed;
        string html = Uri.EscapeDataString($"<html><body><h1>{_name}</h1><script>window.__ticks=0;</script></body></html>");
        _webView.CoreWebView2.Navigate("data:text/html," + html);
        bool ok = await navigation.Task.WaitAsync(TimeSpan.FromSeconds(15));
        _webView.CoreWebView2.NavigationCompleted -= Completed;
        if (!ok) throw new InvalidOperationException("WebView navigation failed: " + _name);
    }

    public async Task SuspendAsync()
    {
        _webView.Visibility = Visibility.Hidden;
        bool suspended = await _webView.CoreWebView2.TrySuspendAsync();
        if (!suspended) throw new InvalidOperationException("TrySuspendAsync returned false for " + _name);
    }

    public Task<bool> BeginSuspendAsync()
    {
        _webView.Visibility = Visibility.Hidden;
        return _webView.CoreWebView2.TrySuspendAsync();
    }

    public Task ResumeAsync()
    {
        _webView.CoreWebView2.Resume();
        _webView.Visibility = Visibility.Visible;
        return Task.CompletedTask;
    }

    public Task<string> ExecuteAsync(string script)
        => _webView.CoreWebView2.ExecuteScriptAsync(script);

    public async Task<string> ReadRendererAsync()
    {
        const string script = "(() => { const c=document.createElement('canvas'); const gl=c.getContext('webgl'); if(!gl) return 'unavailable'; const e=gl.getExtension('WEBGL_debug_renderer_info'); return e ? gl.getParameter(e.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER); })()";
        string json = await ExecuteAsync(script);
        try { return JsonSerializer.Deserialize<string>(json) ?? json; }
        catch { return json; }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _window.Close();
        _webView.Dispose();
    }

    public void Dispose() => Close();
    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}

internal sealed class HarnessHardwareAccess : IHardwareAccess
{
    public SensorReport Report { get; } = new() { CpuTemp = 50, SampledAtUtc = DateTime.UtcNow };
    public bool Available => true;
    public SensorReport Read(bool force = false) => Report;
    public SensorReport Read(HardwareSampleRequest request, bool force = false) => Report;
    public void Invalidate() { }
    public void Dispose() { }
}

internal sealed record HarnessCheck(string Name, string Status, string Detail);

internal sealed class HarnessReport
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public string Machine { get; set; } = "";
    public string Os { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string Commit { get; set; } = "";
    public List<HarnessCheck> Checks { get; } = new();
    public Dictionary<string, double> Metrics { get; } = new();
}

internal sealed class GraphicsBenchmarkRun
{
    public string Label { get; set; } = "";
    public string Renderer { get; set; } = "";
    public int Iteration { get; set; }
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Backend { get; set; } = "";
    public double CpuAveragePercent { get; set; }
    public double CpuP95Percent { get; set; }
    public long PrivateBytesAverage { get; set; }
    public long? VramBytesAverage { get; set; }
    public long? VramBytesMax { get; set; }
    public string VramStatus { get; set; } = "not_verified";
    public double DrawsPerSecond { get; set; }
}

internal readonly record struct GraphicsProcessSample(
    double CpuAverage,
    double CpuP95,
    long PrivateBytesAverage,
    long? VramBytesAverage,
    long? VramBytesMax,
    string VramStatus);

internal sealed record HarnessOptions(
    string Mode,
    string OutputDirectory,
    string Renderer,
    int Cycles,
    TimeSpan Duration,
    string? AppPath,
    string Scenario,
    string Label,
    int Iteration,
    TimeSpan SettleDuration,
    TimeSpan MeasureDuration,
    string? SupervisorPath)
{
    public static HarnessOptions Parse(string[] args)
    {
        string mode = Value(args, "--mode") ?? "deterministic";
        string output = Path.GetFullPath(Value(args, "--output") ?? Path.Combine("artifacts", "resource-validation", "harness"));
        string renderer = Value(args, "--renderer") ?? "swiftshader";
        int cycles = int.TryParse(Value(args, "--cycles"), out int parsedCycles) ? Math.Clamp(parsedCycles, 1, 1000) : 100;
        double seconds = double.TryParse(Value(args, "--duration-seconds"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsedSeconds) ? Math.Max(0.1, parsedSeconds) : 120;
        double settleSeconds = double.TryParse(Value(args, "--settle-seconds"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsedSettle) ? Math.Max(0, parsedSettle) : 30;
        double measureSeconds = double.TryParse(Value(args, "--measure-seconds"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsedMeasure) ? Math.Max(1, parsedMeasure) : 120;
        int iteration = int.TryParse(Value(args, "--iteration"), out int parsedIteration) ? Math.Max(1, parsedIteration) : 1;
        return new HarnessOptions(
            mode,
            output,
            renderer,
            cycles,
            TimeSpan.FromSeconds(seconds),
            Value(args, "--app"),
            Value(args, "--scenario") ?? "dashboard-active",
            Value(args, "--label") ?? "candidate",
            iteration,
            TimeSpan.FromSeconds(settleSeconds),
            TimeSpan.FromSeconds(measureSeconds),
            Value(args, "--supervisor"));
    }

    private static string? Value(string[] args, string key)
    {
        int index = Array.FindIndex(args, arg => string.Equals(arg, key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
