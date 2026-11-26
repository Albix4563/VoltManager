using System.Collections.Concurrent;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Models;
using VoltManager.Performance;
using VoltManager.Services;
using VoltManager.Services.LanRemote;
using HardwareHost = VoltManager.HardwareService.HardwareHost;
using HardwareSampleRequestDto = VoltManager.HardwareService.HardwareSampleRequestDto;
using SensorReportDto = VoltManager.HardwareService.SensorReportDto;

namespace VoltManager.Tests;

public sealed class BackgroundResourceOptimizationTests
{
    [Theory]
    [InlineData("metrics", true, true)]
    [InlineData("metrics", false, false)]
    [InlineData("", true, false)]
    [InlineData("   ", true, false)]
    [InlineData(null, true, false)]
    [InlineData(null, false, false)]
    public void HostBridge_ShouldDispatchEvent_RequiresVisibleSurfaceAndNonBlankEventName(
        string? eventName,
        bool surfaceVisible,
        bool expected)
    {
        Assert.Equal(expected, HostBridge.ShouldDispatchEvent(eventName!, surfaceVisible));
    }

    [Fact]
    public void HostBridge_SetSurfaceVisible_TogglesStateAndShortCircuitsHiddenEvents()
    {
        var bridge = (HostBridge)RuntimeHelpers.GetUninitializedObject(typeof(HostBridge));
        using var lifetime = new VoltManager.Bridge.Rpc.BridgeLifetime();
        typeof(HostBridge).GetField("_lifetime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(bridge, lifetime);
        typeof(HostBridge).GetField("_metricsPublisher", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(bridge, new VoltManager.Performance.UiMetricsPublisher());

        Assert.False(bridge.IsSurfaceVisible);

        bridge.SetSurfaceVisible(true);
        Assert.True(bridge.IsSurfaceVisible);

        bridge.SetSurfaceVisible(false);
        Assert.False(bridge.IsSurfaceVisible);

        // With surface hidden, PushEvent and PostEvent must return before formatting or touching _webView.
        MethodInfo postEvent = typeof(HostBridge).GetMethod("PostEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Exception? metricsPushError = Record.Exception(() => bridge.PushEvent(BridgeEventNames.Metrics, new { cpu = 1 }));
        Exception? themePushError = Record.Exception(() => bridge.PushEvent(BridgeEventNames.ThemeChanged, new { theme = "dark" }));
        Exception? directPostError = Record.Exception(() => postEvent.Invoke(bridge, [BridgeEventNames.Metrics, new { cpu = 1 }]));
        Assert.Null(metricsPushError);
        Assert.Null(themePushError);
        Assert.Null(directPostError);

        bridge.Dispose();
        Assert.False(bridge.IsSurfaceVisible);
    }

    [Fact]
    public void HardwareAccessCoordinator_BuildSensorReport_RespectsRequestedFlagsAndPopulatesFields()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var empty = HardwareAccessCoordinator.BuildSensorReport(
            new HardwareSampleRequest(Temperatures: true, VisualDetails: true, TimeSpan.Zero),
            [],
            now);
        Assert.Null(empty.CpuTemp);
        Assert.Null(empty.GpuTemp);
        Assert.Null(empty.CpuClock);
        Assert.Null(empty.RamClock);
        Assert.Empty(empty.CpuCoreTemps);
        Assert.Empty(empty.Readings);
        Assert.True(empty.DetailsAvailable);

        var readings = new List<SensorReading>
        {
            new() { Hardware = "CPU", Category = "cpu", Name = "CPU Package", Type = "temp", Value = 55 },
            new() { Hardware = "CPU", Category = "cpu", Name = "CPU Core #1", Type = "temp", Value = 52 },
            new() { Hardware = "CPU", Category = "cpu", Name = "CPU Core #1", Type = "clock", Value = 4200 },
            new() { Hardware = "GPU", Category = "gpu", Name = "GPU Core", Type = "temp", Value = 48 },
            new() { Hardware = "RAM", Category = "memory", Name = "Memory Clock", Type = "clock", Value = 1600 },
        };

        var tempsOnly = HardwareAccessCoordinator.BuildSensorReport(
            new HardwareSampleRequest(Temperatures: true, VisualDetails: false, TimeSpan.Zero),
            readings,
            now);
        Assert.Equal(55, tempsOnly.CpuTemp);
        Assert.Equal(48, tempsOnly.GpuTemp);
        Assert.Null(tempsOnly.CpuClock);
        Assert.Null(tempsOnly.RamClock);
        Assert.Empty(tempsOnly.CpuCoreTemps);
        Assert.False(tempsOnly.DetailsAvailable);

        var full = HardwareAccessCoordinator.BuildSensorReport(
            new HardwareSampleRequest(Temperatures: true, VisualDetails: true, TimeSpan.Zero),
            readings,
            now);
        Assert.Equal(55, full.CpuTemp);
        Assert.Equal(48, full.GpuTemp);
        Assert.Equal(4200, full.CpuClock);
        Assert.Equal(1600, full.RamClock);
        Assert.Single(full.CpuCoreTemps);
        Assert.NotEmpty(full.Readings);
        Assert.True(full.DetailsAvailable);
    }

    [Fact]
    public void WidgetWindow_NativeAppearance_TransitionsBetweenAcrylicAndSolidUnderPressure()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var settings = TestSettings.Create();
                var theme = new ThemeService();
                var loc = new VoltManager.Localization.LocalizationService();
                loc.Initialize(settings.Current);
                using var coverage = new ProtectedFullscreenCoverageService(() => new HashSet<int>());
                var currentPressure = new ResourcePressureState();

                var context = new WidgetRuntimeContext(
                    Hardware: null!,
                    Power: null!,
                    Settings: settings,
                    Updates: null!,
                    AutoStart: null!,
                    Monitor: (MonitorService)RuntimeHelpers.GetUninitializedObject(typeof(MonitorService)),
                    Theme: theme,
                    Loc: loc,
                    Awake: (PowerAwakeService)RuntimeHelpers.GetUninitializedObject(typeof(PowerAwakeService)),
                    FullscreenCoverage: coverage,
                    PowerRequests: (PowerRequestCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(PowerRequestCoordinator)),
                    ResourcePressureState: () => currentPressure,
                    RefreshSamplingDemand: _ => { },
                    CreateBridge: (_, _, _) => throw new InvalidOperationException("No WebView bridge in unit test"));

                using var manager = new WidgetManager(
                    settings,
                    theme,
                    loc,
                    environmentFactory: () => new TaskCompletionSource<Microsoft.Web.WebView2.Core.CoreWebView2Environment>().Task,
                    refreshSamplingDemand: _ => { },
                    windowFactory: (_, _, _, _, _) => throw new InvalidOperationException());

                var item = new WidgetItem { Type = "clock", Size = "medium", Enabled = true };
                var display = new DisplayInfo("test", 1, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1040), 1.0, 1.0, true);
                var rect = new PixelRect(40, 40, 260, 160);
                var placement = new WidgetPlacement(
                    "clock",
                    display,
                    display,
                    "top-left",
                    "top-left",
                    rect,
                    rect,
                    0,
                    0,
                    UsesFallbackDisplay: false);
                var envTcs = new TaskCompletionSource<Microsoft.Web.WebView2.Core.CoreWebView2Environment>();

                var window = new WidgetWindow(context, manager, item, envTcs.Task, new System.Windows.Size(260, 160), placement);
                try
                {
                    IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                    Assert.NotEqual(IntPtr.Zero, hwnd);

                    var appearanceField = typeof(WidgetWindow).GetField("_appearance", BindingFlags.NonPublic | BindingFlags.Instance)!;

                    window.ApplyAppearance(new WidgetAppearance { Material = "acrylic", Tint = "theme", Gradient = "vertical", Intensity = 60 });
                    var applied = (WidgetAppearance)appearanceField.GetValue(window)!;
                    Assert.Equal("acrylic", applied.Material);

                    currentPressure = new ResourcePressureState { Profile = ResourceProfile.Gaming, GameActive = true, Reason = "test-gaming" };
                    window.PushResourceProfile(currentPressure);
                    Assert.Equal("solid", ((WidgetAppearance)appearanceField.GetValue(window)!).Material);

                    currentPressure = new ResourcePressureState { Profile = ResourceProfile.Full, Reason = "normal" };
                    window.PushResourceProfile(currentPressure);
                    Assert.Equal("acrylic", ((WidgetAppearance)appearanceField.GetValue(window)!).Material);

                    window.ApplyAppearance(new WidgetAppearance { Material = "transparent", Tint = "theme", Gradient = "none", Intensity = 40 });
                    Assert.Equal("transparent", ((WidgetAppearance)appearanceField.GetValue(window)!).Material);

                    // Exercise native DWM/SetWindowCompositionAttribute transitions on a headless hidden HwndSource
                    MethodInfo tryApplyNative = typeof(WidgetWindow).GetMethod(
                        "TryApplyNativeAppearance",
                        BindingFlags.NonPublic | BindingFlags.Static)!;
                    using var headlessSource = new System.Windows.Interop.HwndSource(
                        new System.Windows.Interop.HwndSourceParameters("vm-widget-test")
                        {
                            Width = 100,
                            Height = 100,
                            WindowStyle = 0,
                        });
                    Assert.True((bool)tryApplyNative.Invoke(null, [headlessSource, "solid", ResourceProfile.Full])!);
                    Assert.True((bool)tryApplyNative.Invoke(null, [headlessSource, "transparent", ResourceProfile.Full])!);
                    Assert.True((bool)tryApplyNative.Invoke(null, [headlessSource, "acrylic", ResourceProfile.Full])!);
                    Assert.True((bool)tryApplyNative.Invoke(null, [headlessSource, "acrylic", ResourceProfile.Gaming])!);

                    // Exercise cross-thread PushResourceProfile dispatch path and hidden bridge sync
                    var bridge = (HostBridge)RuntimeHelpers.GetUninitializedObject(typeof(HostBridge));
                    using var bridgeLifetime = new VoltManager.Bridge.Rpc.BridgeLifetime();
                    typeof(HostBridge).GetField("_lifetime", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .SetValue(bridge, bridgeLifetime);
                    typeof(HostBridge).GetField("_metricsPublisher", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .SetValue(bridge, new UiMetricsPublisher());
                    bridge.SetSurfaceVisible(true);
                    typeof(WidgetWindow).GetField("_bridge", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .SetValue(window, bridge);
                    typeof(WidgetWindow).GetMethod("ApplyEffectiveWebViewVisibility", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(window, null);
                    Assert.False(bridge.IsSurfaceVisible);
                    typeof(WidgetWindow).GetField("_bridge", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .SetValue(window, null);

                    currentPressure = new ResourcePressureState { Profile = ResourceProfile.Critical, Reason = "test-critical" };
                    var worker = new Thread(() => window.PushResourceProfile(currentPressure));
                    worker.Start();
                    Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    Assert.Equal("solid", ((WidgetAppearance)appearanceField.GetValue(window)!).Material);
                }
                finally
                {
                    window.Close();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA WidgetWindow test timed out");
        Assert.Null(failure);
    }

    [Fact]
    public void ProtectedFullscreenCoverageService_CaptureTopLevelWindows_EnumeratesSuccessfully()
    {
        MethodInfo capture = typeof(ProtectedFullscreenCoverageService).GetMethod(
            "CaptureTopLevelWindows",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object? result = capture.Invoke(null, null);
        Assert.NotNull(result);
    }

    [Fact]
    public void PowerAwakeService_EnsureAndClearRequest_WorksAndThrowsAfterDispose()
    {
        var settings = TestSettings.Create();
        settings.Update(s => s.KeepAwake.AutoDisableOnBattery = false);
        var service = new PowerAwakeService(settings);
        try
        {
            var enabled = service.SetEnabled(true);
            Assert.True(enabled.Enabled);
            var disabled = service.SetEnabled(false);
            Assert.False(disabled.Enabled);
        }
        finally
        {
            service.Dispose();
        }

        MethodInfo throwIfDisposed = typeof(PowerAwakeService).GetMethod(
            "ThrowIfDisposed",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() => throwIfDisposed.Invoke(service, null));
        Assert.IsType<ObjectDisposedException>(ex.InnerException);
    }

    [Theory]
    [InlineData("acrylic", ResourceProfile.Full, "acrylic")]
    [InlineData("transparent", ResourceProfile.Full, "transparent")]
    [InlineData("solid", ResourceProfile.Full, "solid")]
    [InlineData("acrylic", ResourceProfile.Balanced, "acrylic")]
    [InlineData("transparent", ResourceProfile.Balanced, "transparent")]
    [InlineData("acrylic", ResourceProfile.Gaming, "solid")]
    [InlineData("transparent", ResourceProfile.Gaming, "solid")]
    [InlineData("solid", ResourceProfile.Gaming, "solid")]
    [InlineData("acrylic", ResourceProfile.Critical, "solid")]
    [InlineData("transparent", ResourceProfile.Critical, "solid")]
    [InlineData("unknown-material", ResourceProfile.Full, "solid")]
    public void WidgetWindow_ResolveEffectiveAppearanceMaterial_ForcesSolidDuringGamingAndCritical(
        string requestedMaterial,
        ResourceProfile profile,
        string expectedMaterial)
    {
        Assert.Equal(
            expectedMaterial,
            WidgetWindow.ResolveEffectiveAppearanceMaterial(requestedMaterial, profile));
    }

    [Fact]
    public void WidgetWindow_EffectiveAppearance_PreservesStylingWhileOverridingMaterialUnderPressure()
    {
        var requested = new WidgetAppearance
        {
            Material = "acrylic",
            Tint = "aurora",
            Gradient = "radial",
            Intensity = 75,
        };

        WidgetAppearance full = WidgetWindow.EffectiveAppearance(requested, ResourceProfile.Full);
        Assert.Equal("acrylic", full.Material);
        Assert.Equal("aurora", full.Tint);
        Assert.Equal("radial", full.Gradient);
        Assert.Equal(75, full.Intensity);

        WidgetAppearance gaming = WidgetWindow.EffectiveAppearance(requested, ResourceProfile.Gaming);
        Assert.Equal("solid", gaming.Material);
        Assert.Equal("aurora", gaming.Tint);
        Assert.Equal("radial", gaming.Gradient);
        Assert.Equal(75, gaming.Intensity);

        WidgetAppearance critical = WidgetWindow.EffectiveAppearance(requested, ResourceProfile.Critical);
        Assert.Equal("solid", critical.Material);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, false, false)]
    public void MonitorService_ShouldSampleVram_RequiresVisualDetailsOrGpuProcessDetection(
        bool visualDetails,
        bool thermalProtection,
        bool gpuProcessDetection,
        bool expected)
    {
        var demand = new MonitorSamplingDemand(visualDetails, thermalProtection, gpuProcessDetection);
        Assert.Equal(expected, MonitorService.ShouldSampleVram(demand));
    }

    [Theory]
    [InlineData(true, true, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 5)]
    [InlineData(false, false, 15)]
    public void MonitorService_ResolveGpuSampleInterval_AdaptsToDemand(
        bool visualDetails,
        bool gpuProcessDetection,
        int expectedSeconds)
    {
        var demand = new MonitorSamplingDemand(visualDetails, ThermalProtection: false, gpuProcessDetection);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MonitorService.ResolveGpuSampleInterval(demand));
    }

    [Fact]
    public void HardwareAccessCoordinator_CreateComputer_DisablesControllerSensors()
    {
        var computer = HardwareAccessCoordinator.CreateComputer();
        Assert.True(computer.IsCpuEnabled);
        Assert.True(computer.IsGpuEnabled);
        Assert.True(computer.IsMotherboardEnabled);
        Assert.True(computer.IsStorageEnabled);
        Assert.True(computer.IsMemoryEnabled);
        Assert.False(computer.IsControllerEnabled);
        Assert.False(computer.IsNetworkEnabled);
        Assert.False(computer.IsPsuEnabled);
        Assert.False(computer.IsBatteryEnabled);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void HardwareAccessCoordinator_ShouldUpdateAny_MatchesRequestedFlags(
        bool temperatures,
        bool visualDetails,
        bool expected)
    {
        var request = new HardwareSampleRequest(temperatures, visualDetails, TimeSpan.FromSeconds(2));
        Assert.Equal(expected, HardwareAccessCoordinator.ShouldUpdateAny(request));
    }

    [Fact]
    public void HardwareAccessCoordinator_Read_SkipsHardwareIterationWhenNothingRequested()
    {
        var coordinator = (HardwareAccessCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(HardwareAccessCoordinator));
        typeof(HardwareAccessCoordinator).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, new object());
        typeof(HardwareAccessCoordinator).GetField("_computer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, HardwareAccessCoordinator.CreateComputer());
        typeof(HardwareAccessCoordinator).GetField("_last", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, SensorReport.Empty);
        typeof(HardwareAccessCoordinator).GetField("_ready", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, true);
        typeof(HardwareAccessCoordinator).GetField("_lastUpdateUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, DateTime.MinValue);

        SensorReport report = coordinator.Read(new HardwareSampleRequest(false, false, TimeSpan.Zero), force: true);
        Assert.False(report.DetailsAvailable);
        Assert.Null(report.CpuTemp);
        Assert.Null(report.GpuTemp);
        Assert.Null(report.CpuClock);
        Assert.Null(report.RamClock);
        Assert.Empty(report.Readings);
        Assert.NotEqual(DateTime.MinValue, report.SampledAtUtc);
    }

    [Fact]
    public void HardwareService_HardwareHost_DisablesControllerAndSkipsEmptyRequests()
    {
        var computer = HardwareHost.CreateComputer();
        Assert.True(computer.IsCpuEnabled);
        Assert.True(computer.IsGpuEnabled);
        Assert.True(computer.IsMotherboardEnabled);
        Assert.True(computer.IsStorageEnabled);
        Assert.True(computer.IsMemoryEnabled);
        Assert.False(computer.IsControllerEnabled);

        Assert.False(HardwareHost.ShouldUpdateAny(new HardwareSampleRequestDto { Temperatures = false, VisualDetails = false }));
        Assert.True(HardwareHost.ShouldUpdateAny(new HardwareSampleRequestDto { Temperatures = true, VisualDetails = false }));
        Assert.True(HardwareHost.ShouldUpdateAny(new HardwareSampleRequestDto { Temperatures = false, VisualDetails = true }));

        var host = (HardwareHost)RuntimeHelpers.GetUninitializedObject(typeof(HardwareHost));
        typeof(HardwareHost).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(host, new object());
        typeof(HardwareHost).GetField("_computer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(host, computer);
        typeof(HardwareHost).GetField("_last", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(host, new SensorReportDto());
        typeof(HardwareHost).GetField("_ready", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(host, true);
        typeof(HardwareHost).GetField("_lastUpdateUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(host, DateTime.MinValue);

        object result = host.Read(
            new HardwareSampleRequestDto(false, false, TimeSpan.Zero),
            force: true);
        var available = (bool)result.GetType().GetProperty("available")!.GetValue(result)!;
        var report = (SensorReportDto)result.GetType().GetProperty("report")!.GetValue(result)!;
        Assert.True(available);
        Assert.False(report.DetailsAvailable);
        Assert.Null(report.CpuTemp);
        Assert.Null(report.GpuTemp);
        Assert.Empty(report.Readings);
    }

    [Fact]
    public void LanRemoteControlService_HasNoAspNetCoreTypesInFieldsOrConstructors()
    {
        Type serviceType = typeof(LanRemoteControlService);
        foreach (FieldInfo field in serviceType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.DoesNotContain("Microsoft.AspNetCore", field.FieldType.FullName ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.AspNetCore", field.FieldType.Assembly.GetName().Name ?? "", StringComparison.Ordinal);
        }

        foreach (ConstructorInfo ctor in serviceType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (ParameterInfo parameter in ctor.GetParameters())
                Assert.DoesNotContain("Microsoft.AspNetCore", parameter.ParameterType.FullName ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LanRemoteControlService_ColdConstruction_DoesNotLoadAspNetCoreAssemblies()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vm-lan-alc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var alc = new TrackingVoltManagerLoadContext(typeof(LanRemoteControlService).Assembly.Location);
        try
        {
            Assembly voltAsm = alc.LoadFromAssemblyPath(typeof(LanRemoteControlService).Assembly.Location);
            Type settingsType = voltAsm.GetType("VoltManager.Services.SettingsService", throwOnError: true)!;
            Type actionsType = voltAsm.GetType("VoltManager.Services.LanRemote.LanRemoteControlActions", throwOnError: true)!;
            Type authType = voltAsm.GetType("VoltManager.Services.LanRemote.LanRemoteAuthStore", throwOnError: true)!;
            Type serviceType = voltAsm.GetType("VoltManager.Services.LanRemote.LanRemoteControlService", throwOnError: true)!;

            object settings = Activator.CreateInstance(settingsType, Path.Combine(tempDir, "settings.json"))!;
            object auth = Activator.CreateInstance(authType, Path.Combine(tempDir, "auth.json"))!;

            ConstructorInfo actionsCtor = actionsType.GetConstructors().Single();
            object?[] actionArgs =
            [
                .. actionsCtor.GetParameters().Select(static p =>
                    p.HasDefaultValue ? p.DefaultValue : CreateStubDelegate(p.ParameterType)),
            ];
            object actions = actionsCtor.Invoke(actionArgs);

            ConstructorInfo internalCtor = serviceType
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(c => c.GetParameters().Length >= 2 && c.GetParameters()[1].ParameterType == actionsType);
            ParameterInfo[] ctorParams = internalCtor.GetParameters();
            object?[] ctorArgs = new object?[ctorParams.Length];
            ctorArgs[0] = settings;
            ctorArgs[1] = actions;
            ctorArgs[2] = auth;
            for (int i = 3; i < ctorParams.Length; i++)
                ctorArgs[i] = ctorParams[i].HasDefaultValue ? ctorParams[i].DefaultValue : null;

            object service = internalCtor.Invoke(ctorArgs);
            object? state = serviceType.GetMethod("GetState")!.Invoke(service, null);
            Assert.NotNull(state);
            ((IDisposable)service).Dispose();

            Assert.DoesNotContain(
                alc.RequestedAssemblies,
                static name => name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            alc.Unload();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Theory]
    [InlineData("VoltManager.runtimeconfig.json", 7)]
    [InlineData("VoltManager.HardwareService.runtimeconfig.json", 8)]
    [InlineData("VoltManager.Supervisor.runtimeconfig.json", 8)]
    public void RuntimeConfig_SetsExpectedGcConserveMemoryLevel(string fileName, int expectedLevel)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        Assert.True(File.Exists(path), $"Expected runtimeconfig at {path}");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        int actual = doc.RootElement
            .GetProperty("runtimeOptions")
            .GetProperty("configProperties")
            .GetProperty("System.GC.ConserveMemory")
            .GetInt32();
        Assert.Equal(expectedLevel, actual);
    }

    private static Delegate CreateStubDelegate(Type delegateType)
    {
        MethodInfo invoke = delegateType.GetMethod("Invoke")!;
        ParameterExpression[] parameters =
        [
            .. invoke.GetParameters().Select(static p => Expression.Parameter(p.ParameterType, p.Name)),
        ];
        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Empty()
            : Expression.Default(invoke.ReturnType);
        return Expression.Lambda(delegateType, body, parameters).Compile();
    }

    private sealed class TrackingVoltManagerLoadContext : AssemblyLoadContext
    {
        private readonly string _voltAssemblyPath;
        public ConcurrentBag<string> RequestedAssemblies { get; } = [];

        public TrackingVoltManagerLoadContext(string voltAssemblyPath)
            : base(isCollectible: true)
            => _voltAssemblyPath = voltAssemblyPath;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? name = assemblyName.Name;
            if (!string.IsNullOrEmpty(name))
                RequestedAssemblies.Add(name);

            if (string.Equals(name, "VoltManager", StringComparison.OrdinalIgnoreCase))
                return LoadFromAssemblyPath(_voltAssemblyPath);

            return null;
        }
    }
}
