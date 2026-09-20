# Issue 36 Application Lifecycle and Window Orchestration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Separate VoltManager application/runtime orchestration, power arbitration, update scheduling, and dashboard tray/WebView2 lifecycle into explicit, testable, idempotent coordinators without changing observable behavior.

**Architecture:** App remains the WPF host/composition root. AppServiceGraph exposes long-lived services, PowerRequestCoordinator owns power state/arbitration, ApplicationLifecycleCoordinator owns application timers/listeners/start-stop ordering, UpdateCoordinator owns update scheduling/defer state, and WebViewTrayCoordinator owns dashboard lifecycle policy through a narrow surface adapter. Windows keep concrete WPF/WebView2 operations and existing services keep domain behavior.

**Tech Stack:** C# / .NET 8 WPF, WebView2, xUnit 2.5.3, Node.js built-in test runner, PowerShell resource-validation harness.

**Spec:** docs/superpowers/specs/2026-09-20-issue-36-application-lifecycle-orchestration-design.md

## Global Constraints

- Work on Dev; every complete working batch is committed and pushed to origin/Dev.
- Preserve the RPC/event contract established by issue #35.
- Preserve power precedence exactly: PowerSource -> Thermal -> Idle -> AppProfile -> HeavyApp -> CpuAutomation.
- Preserve manual override semantics and power-plan guard behavior.
- Preserve lazy App.WebViewEnvironment; minimized startup without an enabled widget must not create Chromium.
- Preserve update channel/interval/snooze/skip behavior and protected-workload deferral.
- Preserve WebView renderer arguments and widget behavior.
- Every extracted coordinator has idempotent Start, Stop, and Dispose; Start after Dispose throws ObjectDisposedException.
- Final resource evidence uses the full scripts/Run-ResourceValidation.ps1 defaults against baseline commit ddbfa613ea4c3c50272bc77976ee5f621496050b.

## Review Focus

- A callback queued just before Stop must not publish into a stopped or restarted lifecycle epoch; Task 1 and every coordinator lifecycle test pin this.
- Reopening the dashboard while suspend/teardown is in flight must leave the visible document resumed; Task 5 pins this race.
- A protected workload starting during update download must still prevent install/restart; Task 4 pins the final pre-install guard.
- Repeated startup/shutdown/uninstall requests must not double-dispose or skip later cleanup after one failure; Task 3 pins this.
- Removing broad App dependencies must not force WebViewEnvironment creation during minimized startup; Tasks 3, 5, and 6 pin this.

---

### Task 1: Shared restartable lifecycle epoch

**Files:**
- Create: src/VoltManager/Services/RestartableLifecycle.cs
- Create: tests/VoltManager.Tests/RestartableLifecycleTests.cs
- Create: tests/VoltManager.Tests/TestLifecycleFakes.cs

**Interfaces:**
- Produces: internal sealed class RestartableLifecycle : IDisposable
- Produces: CancellationToken Start()
- Produces: bool Stop()
- Produces: bool IsCurrent(CancellationToken token)
- Produces: bool IsStarted
- Produces: void Dispose()

The shared test disposable is defined in TestLifecycleFakes.cs and reused by later tasks:

~~~csharp
internal sealed class CallbackDisposable : IDisposable
{
    private readonly Action? _onDispose;
    private int _disposed;

    public CallbackDisposable(Action? onCreate = null, Action? onDispose = null)
    {
        _onDispose = onDispose;
        onCreate?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _onDispose?.Invoke();
    }
}
~~~

- [ ] **Step 1: Write the failing tests**

~~~csharp
public sealed class RestartableLifecycleTests
{
    [Fact]
    public void Start_stop_restart_uses_one_fresh_epoch()
    {
        using var lifecycle = new RestartableLifecycle();
        CancellationToken first = lifecycle.Start();
        CancellationToken duplicate = lifecycle.Start();
        Assert.Equal(first, duplicate);
        Assert.True(lifecycle.IsCurrent(first));

        Assert.True(lifecycle.Stop());
        Assert.False(lifecycle.Stop());
        Assert.True(first.IsCancellationRequested);

        CancellationToken second = lifecycle.Start();
        Assert.NotEqual(first, second);
        Assert.True(lifecycle.IsCurrent(second));
        Assert.False(lifecycle.IsCurrent(first));
    }

    [Fact]
    public void Dispose_is_idempotent_and_terminal()
    {
        var lifecycle = new RestartableLifecycle();
        CancellationToken token = lifecycle.Start();
        lifecycle.Dispose();
        lifecycle.Dispose();
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => lifecycle.Start());
    }
}
~~~

- [ ] **Step 2: Verify RED**

Run:

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~RestartableLifecycleTests -v:minimal
~~~

Expected: compile/test failure because RestartableLifecycle does not exist.

- [ ] **Step 3: Implement the minimal lifecycle primitive**

~~~csharp
internal sealed class RestartableLifecycle : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _epoch;
    private bool _disposed;

    public bool IsStarted
    {
        get { lock (_gate) return _epoch is { IsCancellationRequested: false }; }
    }

    public CancellationToken Start()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RestartableLifecycle));
            if (_epoch is { IsCancellationRequested: false })
                return _epoch.Token;
            _epoch?.Dispose();
            _epoch = new CancellationTokenSource();
            return _epoch.Token;
        }
    }

    public bool Stop()
    {
        CancellationTokenSource? epoch;
        lock (_gate)
        {
            if (_epoch is null || _epoch.IsCancellationRequested) return false;
            epoch = _epoch;
            _epoch = null;
        }
        epoch.Cancel();
        epoch.Dispose();
        return true;
    }

    public bool IsCurrent(CancellationToken token)
    {
        lock (_gate)
            return !_disposed && _epoch is not null &&
                   !_epoch.IsCancellationRequested && _epoch.Token == token;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }
}
~~~

- [ ] **Step 4: Verify GREEN and full .NET tests**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~RestartableLifecycleTests -v:minimal
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
~~~

Expected: both exit 0.

- [ ] **Step 5: Commit and push**

~~~powershell
git add src/VoltManager/Services/RestartableLifecycle.cs tests/VoltManager.Tests/RestartableLifecycleTests.cs tests/VoltManager.Tests/TestLifecycleFakes.cs
git commit -m "refactor: add restartable lifecycle primitive"
git push origin Dev
~~~

### Task 2: Extract PowerRequestCoordinator

**Files:**
- Create: src/VoltManager/Services/PowerRequestCoordinator.cs
- Create: tests/VoltManager.Tests/PowerRequestCoordinatorTests.cs
- Modify: src/VoltManager/App.xaml.cs
- Modify: src/VoltManager/App.AdaptiveResources.cs
- Modify: src/VoltManager/Bridge/EnergyRpcHandler.cs only if composition callbacks need retargeting.

**Interfaces:**
- Production constructor:
  PowerRequestCoordinator(
    SettingsService settings,
    PowerPlanService power,
    PowerAwakeService awake,
    AutomationEngine automation,
    AppPowerProfileService appProfiles,
    HeavyAppDetectionService heavyApps,
    PowerSourcePlanService powerSourcePlans,
    ThermalGuardService thermalGuard,
    IdlePowerGuardService idlePowerGuard,
    Func<ResourcePressureState?> resourcePressureState)
- Produces: Start(), Stop(), Dispose() using RestartableLifecycle; ProcessMetrics and state publication are inert while stopped.
- Produces: ProcessMetrics(MetricsSnapshot metrics, DateTime nowUtc)
- Produces: SetManualOverride(PlanId plan, TimeSpan? duration, string source = "manual", string reasonCode = "manual_override")
- Produces: SetAutomaticMode(), ClearManualOverride()
- Produces: ActivePlan, CpuAutomationState, GetActivePlanReason(), IsProtectedWorkloadActive()
- Produces existing power/application events currently forwarded by App.
- Test seam: internal PowerPolicyPipeline with delegates for each ordered policy.

The test seam has this exact shape:

~~~csharp
internal sealed record PowerPolicyPipeline(
    Func<DateTime, bool> PowerSource,
    Func<DateTime, MetricsSnapshot, bool> Thermal,
    Func<DateTime, bool> Idle,
    Func<DateTime, bool> AppProfile,
    Func<DateTime, bool> HeavyApp,
    Action<MetricsSnapshot, DateTime> CpuAutomation);

internal static PowerRequestCoordinator ForPolicyTest(PowerPolicyPipeline pipeline)
    => new(pipeline);
~~~

- [ ] **Step 1: Write RED precedence and manual-override tests**

~~~csharp
[Theory]
[InlineData("power", "power")]
[InlineData("thermal", "power,thermal")]
[InlineData("idle", "power,thermal,idle")]
[InlineData("profile", "power,thermal,idle,profile")]
[InlineData("heavy", "power,thermal,idle,profile,heavy")]
[InlineData("none", "power,thermal,idle,profile,heavy,cpu")]
public void ProcessMetrics_preserves_priority(string blocker, string expected)
{
    var calls = new List<string>();
    bool Hit(string name) { calls.Add(name); return blocker == name; }

    var coordinator = PowerRequestCoordinator.ForPolicyTest(new PowerPolicyPipeline(
        _ => Hit("power"),
        (_, _) => Hit("thermal"),
        _ => Hit("idle"),
        _ => Hit("profile"),
        _ => Hit("heavy"),
        (_, _) => calls.Add("cpu")));

    coordinator.Start();
    coordinator.ProcessMetrics(new MetricsSnapshot(), DateTime.UnixEpoch);
    Assert.Equal(expected, string.Join(",", calls));
}
~~~

Pin manual override state with real domain services and the existing injectable PowerPlanService:

~~~csharp
[Fact]
public void Manual_override_updates_settings_active_plan_and_publication()
{
    var settings = TestSettings.Create();
    Guid activeGuid = Guid.Parse(PowerPlanService.BalancedGuid);
    string RunPowercfg(string args)
    {
        if (args.StartsWith("/setactive ", StringComparison.OrdinalIgnoreCase))
            activeGuid = Guid.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        return "";
    }

    var power = new PowerPlanService(settings, () => activeGuid, RunPowercfg);
    using var awake = new PowerAwakeService(settings, () => false, () => DateTime.UnixEpoch);
    using var profiles = new AppPowerProfileService(settings);
    using var heavy = new HeavyAppDetectionService(settings);
    using var coordinator = new PowerRequestCoordinator(
        settings,
        power,
        awake,
        new AutomationEngine(),
        profiles,
        heavy,
        new PowerSourcePlanService(settings, () => new PowerSourceSnapshot(true, 100)),
        new ThermalGuardService(settings),
        new IdlePowerGuardService(settings, () => 0, () => false),
        () => null);

    PowerPlan? published = null;
    coordinator.ActivePlanChanged += plan => published = plan;
    coordinator.Start();

    Assert.True(coordinator.SetManualOverride(PlanId.Performance, null));
    Assert.Equal("performance", settings.Current.Override?.Plan);
    Assert.Equal(PlanId.Performance, coordinator.ActivePlan?.PlanId);
    Assert.Equal(PlanId.Performance, published?.PlanId);
    Assert.True(coordinator.CpuAutomationState.ManualOverrideActive);
}
~~~

Add one lifecycle assertion in the same test file:

~~~csharp
[Fact]
public void Power_coordinator_dispose_is_terminal()
{
    using var coordinator = PowerRequestCoordinator.ForPolicyTest(
        new PowerPolicyPipeline(_ => false, (_, _) => false, _ => false,
            _ => false, _ => false, (_, _) => { }));
    coordinator.Start();
    coordinator.Stop();
    coordinator.Dispose();
    coordinator.Dispose();
    Assert.Throws<ObjectDisposedException>(() => coordinator.Start());
}
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~PowerRequestCoordinatorTests -v:minimal
~~~

Expected: failure because coordinator/pipeline do not exist.

- [ ] **Step 3: Move power state and methods**

Move from App.xaml.cs: automation overlap/sampling state, app-profile/heavy-app sessions and grace windows, plan guard, active plan/reason/CPU state and events, OnMetricsSampled, sampling publication, all five Handle* priority methods, CPU fallback, conflict/reassertion, manual override methods, expiration, and history helpers.

The arbitration body remains exactly:

~~~csharp
bool handled =
    HandlePowerSourcePlans(nowUtc) ||
    HandleThermalGuard(nowUtc, metrics) ||
    HandleIdlePowerGuard(nowUtc) ||
    HandleAppPowerProfiles(nowUtc) ||
    HandleHeavyAppDetection(nowUtc);

if (!handled)
    HandleCpuAutomation(metrics, nowUtc);
~~~

Keep temporary forwarding members on App so RPC/window/widget callers remain compatible until later tasks narrow them.

- [ ] **Step 4: Verify GREEN and regressions**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~PowerRequestCoordinatorTests|FullyQualifiedName~PowerPlan|FullyQualifiedName~ResourcePressure|FullyQualifiedName~EnergyRpcHandlerTests" -v:minimal
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
~~~

Expected: both exit 0.

- [ ] **Step 5: Commit and push**

~~~powershell
git add src/VoltManager/App.xaml.cs src/VoltManager/App.AdaptiveResources.cs src/VoltManager/Services/PowerRequestCoordinator.cs tests/VoltManager.Tests/PowerRequestCoordinatorTests.cs
git status --short
git commit -m "refactor: extract power request coordinator"
git push origin Dev
~~~

If EnergyRpcHandler.cs changed, stage it after inspecting the diff.

### Task 3: Explicit service graph and ApplicationLifecycleCoordinator

**Files:**
- Create: src/VoltManager/Services/AppServiceGraph.cs
- Create: src/VoltManager/Services/ApplicationLifecycleCoordinator.cs
- Create: tests/VoltManager.Tests/ApplicationLifecycleCoordinatorTests.cs
- Create: tests/VoltManager.Tests/RuntimeServiceLifecycleTests.cs
- Modify: src/VoltManager/App.xaml.cs
- Modify: src/VoltManager/App.Reliability.cs
- Modify: src/VoltManager/App.UninstallLifecycle.cs
- Modify restartable runtime services as required: MonitorService.cs, HeavyAppDetectionService.cs, AppPowerProfileService.cs, StandbyAutoCleanerService.cs, ProtectedFullscreenCoverageService.cs, ScheduledPowerActionService.cs, RemoteCommandService.cs.

**Interfaces:**
- Consumes: RestartableLifecycle, PowerRequestCoordinator.
- Produces: AppServiceGraph with typed service properties and no runtime starts in its constructor.
- Produces: ApplicationLifecycleCoordinator.Start(), Stop(), Dispose().
- Test seam: ApplicationLifecycleActions for attach/detach, service start/stop, timer factories, and remote callbacks.

Use this exact action record:

~~~csharp
internal sealed record ApplicationLifecycleActions(
    Action Attach,
    Action Detach,
    Action StartServices,
    Action StopServices,
    Func<CancellationToken, IDisposable> CreatePlanPollTimer,
    Func<CancellationToken, IDisposable> CreateBatteryHistoryTimer);
~~~

- [ ] **Step 1: Write RED lifecycle ownership tests**

~~~csharp
[Fact]
public void Start_stop_restart_does_not_duplicate_runtime_resources()
{
    int attach = 0, detach = 0, timers = 0, timerDisposals = 0;
    var actions = new ApplicationLifecycleActions(
        Attach: () => attach++,
        Detach: () => detach++,
        StartServices: () => { },
        StopServices: () => { },
        CreatePlanPollTimer: _ => new CallbackDisposable(
            onCreate: () => timers++, onDispose: () => timerDisposals++),
        CreateBatteryHistoryTimer: _ => new CallbackDisposable(
            onCreate: () => timers++, onDispose: () => timerDisposals++));

    using var coordinator = new ApplicationLifecycleCoordinator(actions);
    coordinator.Start();
    coordinator.Start();
    Assert.Equal(1, attach);
    Assert.Equal(2, timers);

    coordinator.Stop();
    coordinator.Stop();
    Assert.Equal(1, detach);
    Assert.Equal(2, timerDisposals);

    coordinator.Start();
    Assert.Equal(2, attach);
    Assert.Equal(4, timers);
}
~~~

Pin best-effort cleanup and epoch cancellation explicitly:

~~~csharp
[Fact]
public void Stop_continues_after_cleanup_failure_and_cancels_epoch()
{
    var calls = new List<string>();
    CancellationToken timerEpoch = default;
    var actions = new ApplicationLifecycleActions(
        Attach: () => calls.Add("attach"),
        Detach: () => calls.Add("detach"),
        StartServices: () => calls.Add("start-services"),
        StopServices: () =>
        {
            calls.Add("stop-services");
            throw new InvalidOperationException("expected");
        },
        CreatePlanPollTimer: token =>
        {
            timerEpoch = token;
            return new CallbackDisposable(onDispose: () => calls.Add("plan-dispose"));
        },
        CreateBatteryHistoryTimer: _ =>
            new CallbackDisposable(onDispose: () => calls.Add("battery-dispose")));

    using var coordinator = new ApplicationLifecycleCoordinator(actions);
    coordinator.Start();

    Exception? error = Record.Exception(coordinator.Stop);

    Assert.Null(error);
    Assert.True(timerEpoch.IsCancellationRequested);
    Assert.Contains("plan-dispose", calls);
    Assert.Contains("battery-dispose", calls);
    Assert.Contains("detach", calls);
    Assert.Contains("stop-services", calls);
}
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~ApplicationLifecycleCoordinatorTests -v:minimal
~~~

Expected: coordinator/actions missing.

- [ ] **Step 3: Make subordinate runtime services restartable**

Timer-owning services get idempotent Stop() methods that dispose/null runtime timers. Dispose() calls Stop() plus terminal provider/native cleanup.

RemoteCommandService guards its registrations: repeated Start is a no-op, Stop unregisters/disposes/clears, Dispose calls Stop.

MonitorService and ProtectedFullscreenCoverageService separate restartable runtime shutdown from terminal provider/native disposal.

Add RuntimeServiceLifecycleTests with explicit restart checks. Timer-backed services are verified by reading their private timer field only for this lifecycle contract:

~~~csharp
private static object? Field(object instance, string name) =>
    instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(instance);

[Fact]
public void Heavy_app_detection_stop_is_idempotent_and_restartable()
{
    using var service = new HeavyAppDetectionService(TestSettings.Create());
    service.StartDelayed(TimeSpan.FromHours(1));
    object? first = Field(service, "_timer");
    Assert.NotNull(first);

    service.Stop();
    service.Stop();
    Assert.Null(Field(service, "_timer"));

    service.StartDelayed(TimeSpan.FromHours(1));
    object? second = Field(service, "_timer");
    Assert.NotNull(second);
    Assert.NotSame(first, second);
}

[Fact]
public void App_profile_and_standby_cleaner_restart_after_stop()
{
    using var profiles = new AppPowerProfileService(TestSettings.Create());
    using var cleaner = new StandbyAutoCleanerService(TestSettings.Create());

    profiles.StartDelayed(TimeSpan.FromHours(1));
    cleaner.StartDelayed(TimeSpan.FromHours(1));
    profiles.Stop();
    cleaner.Stop();

    Assert.Null(Field(profiles, "_timer"));
    Assert.Null(Field(cleaner, "_timer"));

    profiles.StartDelayed(TimeSpan.FromHours(1));
    cleaner.StartDelayed(TimeSpan.FromHours(1));
    Assert.NotNull(Field(profiles, "_timer"));
    Assert.NotNull(Field(cleaner, "_timer"));
}
~~~

Add these concrete runtime tests in the same file:

~~~csharp
[Fact]
public void Monitor_timer_is_recreated_after_stop()
{
    using var monitor = new MonitorService();
    monitor.Start(TimeSpan.FromHours(1));
    object? first = Field(monitor, "_timer");
    Assert.NotNull(first);

    monitor.Stop();
    monitor.Stop();
    Assert.Null(Field(monitor, "_timer"));

    monitor.Start(TimeSpan.FromHours(1));
    object? second = Field(monitor, "_timer");
    Assert.NotNull(second);
    Assert.NotSame(first, second);
}

[Fact]
public void Scheduled_power_daily_timer_is_recreated_after_stop()
{
    var settings = TestSettings.Create();
    settings.Update(state =>
    {
        state.AutoShutdown.Enabled = true;
        state.AutoShutdown.Mode = ScheduledPowerMode.Daily;
        state.AutoShutdown.Time = "23:59";
    });
    using var service = new ScheduledPowerActionService(
        settings, new NoopPowerActionExecutor(), new FixedClock());

    service.Start();
    object? first = Field(service, "_dailyTimer");
    Assert.NotNull(first);

    service.Stop();
    service.Stop();
    Assert.Null(Field(service, "_dailyTimer"));

    service.Start();
    object? second = Field(service, "_dailyTimer");
    Assert.NotNull(second);
    Assert.NotSame(first, second);
}

[Fact]
public void Fullscreen_coverage_stop_unhooks_and_allows_restart()
{
    using var coverage = new ProtectedFullscreenCoverageService(
        () => new HashSet<int>());

    coverage.Start();
    coverage.Stop();
    coverage.Stop();

    var hooks = Assert.IsAssignableFrom<System.Collections.ICollection>(
        Field(coverage, "_hooks"));
    Assert.Equal(0, hooks.Count);

    coverage.Start();
    coverage.Stop();
}

[Fact]
public void Remote_commands_start_is_idempotent_and_restartable()
{
    using var remote = new RemoteCommandService();
    remote.Start();
    int firstCount = ((System.Collections.ICollection)Field(remote, "_waits")!).Count;
    Assert.Equal(RemoteCommandProtocol.AllKeys.Length, firstCount);

    remote.Start();
    Assert.Equal(firstCount,
        ((System.Collections.ICollection)Field(remote, "_waits")!).Count);

    remote.Stop();
    remote.Stop();
    Assert.Equal(0,
        ((System.Collections.ICollection)Field(remote, "_waits")!).Count);

    remote.Start();
    Assert.Equal(firstCount,
        ((System.Collections.ICollection)Field(remote, "_waits")!).Count);
}

private sealed class NoopPowerActionExecutor : IPowerActionExecutor
{
    public void Execute(ScheduledPowerActionType action) { }
}

private sealed class FixedClock : ISystemClock
{
    public DateTime UtcNow => DateTime.UnixEpoch;
}
~~~

ProtectedFullscreenCoverageService.Stop must disable its fallback/coalesce timers and unhook/clear hooks without disposing the readonly timers; terminal Dispose performs the final timer disposal. RemoteCommandService uses ValidationEnvironment's test named-object prefix already active in the test host, so the test never targets installed-app events.

- [ ] **Step 4: Implement graph and lifecycle coordinator**

AppServiceGraph exposes the construction order from the spec and starts nothing in constructors.

ApplicationLifecycleCoordinator.Start attaches monitor/settings/system/remote subscriptions, starts runtime services, and creates plan-poll/battery timers exactly once. Timer callbacks capture the lifecycle token and publish only while IsCurrent(epoch).

Stop cancels the epoch first, captures/disposes owned timers and registrations, detaches listeners, then stops services using best-effort cleanup so one exception cannot skip later owners.

- [ ] **Step 5: Unify normal/fatal/uninstall shutdown**

App.ExitApp stops window coordinators, stops/disposes application lifecycle, releases show wait/event/mutex, then calls Shutdown.

App.Reliability retains the hard deadline and BoundedCleanup, but calls the shared lifecycle cleanup path instead of maintaining a second service inventory.

App.UninstallLifecycle keeps its native event/wait and routes to ExitApp.

- [ ] **Step 6: Verify GREEN**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~ApplicationLifecycleCoordinatorTests|FullyQualifiedName~UninstallLifecycleContractTests|FullyQualifiedName~UnhandledExceptionPolicyTests|FullyQualifiedName~ResourceOptimizationTests" -v:minimal
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
~~~

Expected: both exit 0.

- [ ] **Step 7: Commit and push**

~~~powershell
git add src/VoltManager/App.xaml.cs src/VoltManager/App.Reliability.cs src/VoltManager/App.UninstallLifecycle.cs
git add src/VoltManager/Services/AppServiceGraph.cs src/VoltManager/Services/ApplicationLifecycleCoordinator.cs
git add src/VoltManager/Services/MonitorService.cs src/VoltManager/Services/HeavyAppDetectionService.cs src/VoltManager/Services/AppPowerProfileService.cs
git add src/VoltManager/Services/StandbyAutoCleanerService.cs src/VoltManager/Services/ProtectedFullscreenCoverageService.cs src/VoltManager/Services/ScheduledPowerActionService.cs src/VoltManager/Services/RemoteCommandService.cs
git add tests/VoltManager.Tests/ApplicationLifecycleCoordinatorTests.cs tests/VoltManager.Tests/RuntimeServiceLifecycleTests.cs
git commit -m "refactor: centralize application lifecycle ownership"
git push origin Dev
~~~

### Task 4: Extract UpdateCoordinator

**Files:**
- Create: src/VoltManager/Services/UpdateCoordinator.cs
- Create: tests/VoltManager.Tests/UpdateCoordinatorTests.cs
- Modify: src/VoltManager/MainWindow.xaml.cs
- Modify: src/VoltManager/App.xaml.cs only for composition/injection.

**Interfaces:**
- Consumes: RestartableLifecycle, UpdateService, SettingsService, protected-workload query/event.
- Produces: Start(), Stop(), Dispose().
- Produces: CheckNowAsync(bool automatic, CancellationToken cancellationToken = default).
- Produces events/callbacks UpdateAvailable, PromptRequested, InstallRequested.
- Produces: DeferInstall(string url), Task NotifyProtectedWorkloadChangedAsync(bool active).
- Produces: PrepareInstallAsync(string url, Func<string, CancellationToken, Task<string>> download, CancellationToken cancellationToken = default); this performs the final protected-workload check after download and raises InstallRequested only when still safe.
- Internal test timer factory signature: Func<TimeSpan, Action, IDisposable>.

The test factory has this exact entry point:

~~~csharp
internal static UpdateCoordinator ForTest(
    Func<Task<UpdateInfo>> check,
    Func<bool> protectedWorkload,
    Func<TimeSpan, Action, IDisposable> createTimer)
~~~

- [ ] **Step 1: Write RED update lifecycle tests**

Use an internal constructor accepting update-check delegate, protected-workload query, timer factory, and publication callbacks.

~~~csharp
[Fact]
public void Repeated_start_owns_one_timer()
{
    int timers = 0;
    using var coordinator = UpdateCoordinator.ForTest(
        check: () => Task.FromResult(new UpdateInfo()),
        protectedWorkload: () => false,
        createTimer: (_, _) => { timers++; return new CallbackDisposable(); });

    coordinator.Start();
    coordinator.Start();
    Assert.Equal(1, timers);
}
~~~

Pin the remaining update races with deterministic tasks:

~~~csharp
[Fact]
public async Task Concurrent_checks_share_one_inflight_operation()
{
    int checks = 0;
    var release = new TaskCompletionSource<UpdateInfo>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var coordinator = UpdateCoordinator.ForTest(
        check: () =>
        {
            Interlocked.Increment(ref checks);
            return release.Task;
        },
        protectedWorkload: () => false,
        createTimer: (_, _) => new CallbackDisposable());

    coordinator.Start();
    Task first = coordinator.CheckNowAsync(automatic: true);
    Task second = coordinator.CheckNowAsync(automatic: true);
    Assert.Equal(1, Volatile.Read(ref checks));

    release.SetResult(new UpdateInfo());
    await Task.WhenAll(first, second);
}

[Fact]
public async Task Protected_workload_defers_check_and_resumes_once()
{
    bool protectedWorkload = true;
    int checks = 0;
    using var coordinator = UpdateCoordinator.ForTest(
        check: () =>
        {
            checks++;
            return Task.FromResult(new UpdateInfo());
        },
        protectedWorkload: () => protectedWorkload,
        createTimer: (_, _) => new CallbackDisposable());

    coordinator.Start();
    await coordinator.CheckNowAsync(automatic: true);
    Assert.Equal(0, checks);

    protectedWorkload = false;
    await coordinator.NotifyProtectedWorkloadChangedAsync(active: false);
    await coordinator.NotifyProtectedWorkloadChangedAsync(active: false);
    Assert.Equal(1, checks);
}

[Fact]
public async Task Stop_suppresses_late_update_publication()
{
    var release = new TaskCompletionSource<UpdateInfo>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var coordinator = UpdateCoordinator.ForTest(
        check: () => release.Task,
        protectedWorkload: () => false,
        createTimer: (_, _) => new CallbackDisposable());
    int published = 0;
    coordinator.UpdateAvailable += _ => published++;

    coordinator.Start();
    Task pending = coordinator.CheckNowAsync(automatic: false);
    coordinator.Stop();
    release.SetResult(new UpdateInfo
    {
        UpdateAvailable = true,
        LatestVersion = "9.9.9",
        DownloadUrl = "https://example.invalid/update.exe",
    });
    await pending;

    Assert.Equal(0, published);
}

[Fact]
public async Task Workload_starting_during_download_blocks_install_request()
{
    bool protectedWorkload = false;
    var download = new TaskCompletionSource<string>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var coordinator = UpdateCoordinator.ForTest(
        check: () => Task.FromResult(new UpdateInfo()),
        protectedWorkload: () => protectedWorkload,
        createTimer: (_, _) => new CallbackDisposable());
    string? installPath = null;
    coordinator.InstallRequested += path => installPath = path;

    coordinator.Start();
    Task prepare = coordinator.PrepareInstallAsync(
        "https://example.invalid/update.exe",
        (_, _) => download.Task);
    protectedWorkload = true;
    download.SetResult(@"C:\Temp\VoltManagerUpdate.exe");
    await prepare;

    Assert.Null(installPath);
}
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~UpdateCoordinatorTests -v:minimal
~~~

Expected: coordinator missing.

- [ ] **Step 3: Move non-visual update state**

Move auto-update timer, in-flight guard, deferred-check state, deferred URL, scheduling/overlap/defer logic, version suppression, snooze/skip state operations, and protected-workload resume into UpdateCoordinator.

Keep in MainWindow: UpdatePromptWindow, bridge publication, MessageBox UX, installer Process.Start, _exiting, and app exit.

Wire coordinator events to dispatcher/UI handlers and detach on window stop.

- [ ] **Step 4: Verify GREEN**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~UpdateCoordinatorTests|FullyQualifiedName~UpdateSchedulePolicyTests|FullyQualifiedName~UpdateServiceTests|FullyQualifiedName~UpdateRpcHandlerTests" -v:minimal
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
~~~

Expected: both exit 0.

- [ ] **Step 5: Commit and push**

~~~powershell
git add src/VoltManager/Services/UpdateCoordinator.cs src/VoltManager/MainWindow.xaml.cs src/VoltManager/App.xaml.cs tests/VoltManager.Tests/UpdateCoordinatorTests.cs
git commit -m "refactor: extract update lifecycle coordinator"
git push origin Dev
~~~

### Task 5: Extract WebViewTrayCoordinator

**Files:**
- Create: src/VoltManager/Services/WebViewTrayCoordinator.cs
- Create: src/VoltManager/Services/WebViewLifecycleBinding.cs
- Create: tests/VoltManager.Tests/WebViewTrayCoordinatorTests.cs
- Modify: src/VoltManager/MainWindow.xaml.cs
- Modify: src/VoltManager/MainWindow.AdaptiveResources.cs
- Modify: src/VoltManager/MainWindow.ThemeBootstrap.cs only if recovery/rewire ownership requires it.

**Interfaces:**
- Consumes: RestartableLifecycle.
- Produces: IDashboardSurface with EnsureWebViewAsync, SetWebViewVisible, SuspendAsync, Resume, NavigateBlank, NavigateApp, RecoverBrowserAsync, PublishFreshState.
- Produces: Start(bool initiallyVisible), HideToTray(), ShowFromTrayAsync(), SetVisible(bool), HandleProcessFailureAsync(WebViewFailureKind), NotifyNavigationSucceeded(), Stop(), Dispose().
- Produces internal WebViewFailureKind mapped from the WebView2 enum inside MainWindow; the coordinator itself has no WebView2 dependency.
- Produces: WebViewLifecycleBinding<TSource>.Attach(TSource source) and Dispose(); attaching the same source twice is a no-op, switching sources detaches the old source first.

Define the surface boundary before the coordinator:

~~~csharp
internal interface IDashboardSurface
{
    bool IsVisible { get; }
    void HideWindow();
    void ShowAndActivateWindow();
    Task EnsureWebViewAsync(CancellationToken cancellationToken);
    void SetWebViewVisible(bool visible);
    Task<bool> SuspendAsync(CancellationToken cancellationToken);
    void Resume();
    void NavigateBlank();
    void NavigateApp();
    void Reload();
    Task RecoverBrowserAsync(CancellationToken cancellationToken);
    void PublishFreshState();
}

internal enum WebViewFailureKind
{
    Renderer,
    BrowserProcessExited,
}
~~~

Use this binding shape for MainWindow's ProcessFailed and NavigationCompleted handlers:

~~~csharp
internal sealed class WebViewLifecycleBinding<TSource> : IDisposable
    where TSource : class
{
    private readonly Action<TSource> _attach;
    private readonly Action<TSource> _detach;
    private TSource? _source;
    private bool _disposed;

    public WebViewLifecycleBinding(Action<TSource> attach, Action<TSource> detach)
    {
        _attach = attach;
        _detach = detach;
    }

    public void Attach(TSource source)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WebViewLifecycleBinding<TSource>));
        if (ReferenceEquals(_source, source)) return;
        if (_source is not null) _detach(_source);
        _source = source;
        _attach(source);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source is not null) _detach(_source);
        _source = null;
    }
}
~~~

- [ ] **Step 1: Write RED fake-surface tests**

Pin minimized startup directly:

~~~csharp
[Fact]
public void Start_minimized_does_not_ensure_webview()
{
    var surface = new FakeDashboardSurface();
    var timers = new ManualTimerFactory();
    using var coordinator = new WebViewTrayCoordinator(
        surface, timers.Create, TimeSpan.FromSeconds(20));

    coordinator.Start(initiallyVisible: false);

    Assert.Equal(0, surface.EnsureCalls);
}
~~~

~~~csharp
[Fact]
public async Task Reopen_cancels_pending_blank_and_visible_state_wins_suspend_race()
{
    var surface = new FakeDashboardSurface();
    var timers = new ManualTimerFactory();
    using var coordinator = new WebViewTrayCoordinator(
        surface, timers.Create, TimeSpan.FromSeconds(20));

    coordinator.Start(initiallyVisible: true);
    coordinator.HideToTray();
    Task suspend = surface.PendingSuspend;

    await coordinator.ShowFromTrayAsync();
    surface.CompleteSuspend(success: true);
    await suspend;
    timers.FireAll();

    Assert.True(surface.Visible);
    Assert.True(surface.Resumed);
    Assert.False(surface.BlankNavigated);
}
~~~

Pin delayed teardown, retry cap, single-flight recovery, and stop suppression:

~~~csharp
[Fact]
public async Task Hidden_dashboard_blanks_after_one_tray_timeout()
{
    var surface = new FakeDashboardSurface();
    var timers = new ManualTimerFactory();
    using var coordinator = new WebViewTrayCoordinator(
        surface, timers.Create, TimeSpan.FromSeconds(20));
    coordinator.Start(initiallyVisible: true);

    coordinator.HideToTray();
    surface.CompleteSuspend(success: true);
    await surface.PendingSuspend;
    Assert.Equal(1, timers.Count);

    timers.FireAll();

    Assert.Equal(1, surface.BlankCalls);
}

[Fact]
public async Task Renderer_reload_is_capped_at_five_attempts()
{
    var surface = new FakeDashboardSurface();
    var timers = new ManualTimerFactory();
    using var coordinator = new WebViewTrayCoordinator(
        surface, timers.Create, TimeSpan.FromSeconds(20));
    coordinator.Start(initiallyVisible: true);

    for (int i = 0; i < 7; i++)
        await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);

    Assert.Equal(5, surface.ReloadCalls);
}

[Fact]
public async Task Successful_navigation_resets_renderer_retry_budget()
{
    var surface = new FakeDashboardSurface();
    var timers = new ManualTimerFactory();
    using var coordinator = new WebViewTrayCoordinator(
        surface, timers.Create, TimeSpan.FromSeconds(20));
    coordinator.Start(initiallyVisible: true);

    for (int i = 0; i < 5; i++)
        await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);
    Assert.Equal(5, surface.ReloadCalls);

    coordinator.NotifyNavigationSucceeded();
    await coordinator.HandleProcessFailureAsync(WebViewFailureKind.Renderer);

    Assert.Equal(6, surface.ReloadCalls);
}

[Fact]
public async Task Browser_recovery_is_single_flight()
{
    var surface = new FakeDashboardSurface();
    var timers = new ManualTimerFactory();
    using var coordinator = new WebViewTrayCoordinator(
        surface, timers.Create, TimeSpan.FromSeconds(20));
    coordinator.Start(initiallyVisible: true);

    Task first = coordinator.HandleProcessFailureAsync(
        WebViewFailureKind.BrowserProcessExited);
    Task second = coordinator.HandleProcessFailureAsync(
        WebViewFailureKind.BrowserProcessExited);
    Assert.Equal(1, surface.RecoverCalls);

    surface.CompleteRecovery();
    await Task.WhenAll(first, second);
}

[Fact]
public void Stop_cancels_pending_tray_teardown()
{
    var surface = new FakeDashboardSurface();
    var timers = new ManualTimerFactory();
    using var coordinator = new WebViewTrayCoordinator(
        surface, timers.Create, TimeSpan.FromSeconds(20));
    coordinator.Start(initiallyVisible: true);
    coordinator.HideToTray();

    coordinator.Stop();
    timers.FireAll();

    Assert.Equal(0, surface.BlankCalls);
    Assert.Equal(0, timers.Count);
}

[Fact]
public void Webview_binding_never_duplicates_handlers_for_same_core()
{
    var first = new object();
    var second = new object();
    int attaches = 0;
    int detaches = 0;
    using var binding = new WebViewLifecycleBinding<object>(
        _ => attaches++,
        _ => detaches++);

    binding.Attach(first);
    binding.Attach(first);
    Assert.Equal(1, attaches);
    Assert.Equal(0, detaches);

    binding.Attach(second);
    Assert.Equal(2, attaches);
    Assert.Equal(1, detaches);

    binding.Dispose();
    binding.Dispose();
    Assert.Equal(2, detaches);
}
~~~

The test file also defines deterministic timer/surface fakes:

~~~csharp
internal sealed class ManualTimerFactory
{
    private readonly List<Action> _callbacks = new();
    public int Count => _callbacks.Count;

    public IDisposable Create(TimeSpan _, Action callback)
    {
        _callbacks.Add(callback);
        return new CallbackDisposable(onDispose: () => _callbacks.Remove(callback));
    }

    public void FireAll()
    {
        foreach (Action callback in _callbacks.ToArray())
            callback();
    }
}

internal sealed class FakeDashboardSurface : IDashboardSurface
{
    private TaskCompletionSource<bool> _suspend =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _recovery =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsVisible => Visible;
    public bool Visible { get; private set; } = true;
    public bool Resumed { get; private set; }
    public bool BlankNavigated { get; private set; }
    public int BlankCalls { get; private set; }
    public int ReloadCalls { get; private set; }
    public int RecoverCalls { get; private set; }
    public int EnsureCalls { get; private set; }
    public Task PendingSuspend => _suspend.Task;

    public void HideWindow() => Visible = false;
    public void ShowAndActivateWindow() => Visible = true;
    public Task EnsureWebViewAsync(CancellationToken _)
    {
        EnsureCalls++;
        return Task.CompletedTask;
    }
    public void SetWebViewVisible(bool visible) => Visible = visible;
    public Task<bool> SuspendAsync(CancellationToken _) => _suspend.Task;
    public void Resume() => Resumed = true;
    public void NavigateBlank()
    {
        BlankNavigated = true;
        BlankCalls++;
    }
    public void NavigateApp() { }
    public void Reload() => ReloadCalls++;
    public Task RecoverBrowserAsync(CancellationToken _)
    {
        RecoverCalls++;
        return _recovery.Task;
    }
    public void PublishFreshState() { }
    public void CompleteSuspend(bool success) => _suspend.TrySetResult(success);
    public void CompleteRecovery() => _recovery.TrySetResult(true);
}
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~WebViewTrayCoordinatorTests -v:minimal
~~~

Expected: coordinator/surface missing.

- [ ] **Step 3: Implement policy and MainWindow adapter**

Move tray timer, suspend running/generation, renderer reload count, and browser recovery guard from MainWindow into coordinator.

All timer/async continuations capture the lifecycle token and recheck IsCurrent before touching the surface.

MainWindow keeps actual WPF/WebView calls and maps CoreWebView2ProcessFailedEventArgs to WebViewFailureKind. Replace the anonymous NavigationCompleted callback with a named handler, then use WebViewLifecycleBinding<CoreWebView2> to attach/detach ProcessFailed and NavigationCompleted exactly once per active core. The named successful-navigation handler calls NotifyNavigationSucceeded().

- [ ] **Step 4: Verify GREEN**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~WebViewTrayCoordinatorTests|FullyQualifiedName~ResourcePressureTests|FullyQualifiedName~BridgeLifetimeTests" -v:minimal
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
~~~

Expected: both exit 0.

- [ ] **Step 5: Commit and push**

~~~powershell
git add src/VoltManager/Services/WebViewTrayCoordinator.cs src/VoltManager/Services/WebViewLifecycleBinding.cs src/VoltManager/MainWindow.xaml.cs src/VoltManager/MainWindow.AdaptiveResources.cs tests/VoltManager.Tests/WebViewTrayCoordinatorTests.cs
git status --short
git commit -m "refactor: extract dashboard webview tray lifecycle"
git push origin Dev
~~~

If MainWindow.ThemeBootstrap.cs changed, stage it after inspecting the diff.

### Task 6: Narrow widget runtime dependencies

**Files:**
- Create: src/VoltManager/Services/WidgetRuntimeContext.cs
- Create: tests/VoltManager.Tests/WidgetRuntimeContextTests.cs
- Modify: src/VoltManager/Services/WidgetManager.cs
- Modify: src/VoltManager/WidgetWindow.xaml.cs
- Modify: src/VoltManager/App.xaml.cs

**Interfaces:**
- Produces: WidgetRuntimeContext containing only required services/event sources and callbacks.
- WidgetManager constructor:
  WidgetManager(
    SettingsService settings,
    ThemeService theme,
    LocalizationService loc,
    Func<Task<CoreWebView2Environment>> environmentFactory,
    Action<bool> refreshSamplingDemand,
    Func<WidgetManager, WidgetItem, Task<CoreWebView2Environment>, Size, WidgetPlacement, WidgetWindow> windowFactory)
- WidgetWindow consumes WidgetRuntimeContext; neither stores App.

Use this runtime context shape for each WidgetWindow:

~~~csharp
internal sealed record WidgetRuntimeContext(
    HardwareInfoService Hardware,
    PowerPlanService Power,
    SettingsService Settings,
    UpdateService Updates,
    StartupService AutoStart,
    MonitorService Monitor,
    ThemeService Theme,
    LocalizationService Loc,
    PowerAwakeService Awake,
    ProtectedFullscreenCoverageService FullscreenCoverage,
    PowerRequestCoordinator PowerRequests,
    Func<ResourcePressureState> ResourcePressureState,
    Action<bool> RefreshSamplingDemand,
    Func<WebView2, bool, HostBridge> CreateBridge);
~~~

- [ ] **Step 1: Write RED dependency and laziness tests**

~~~csharp
[Fact]
public void Widget_components_do_not_store_App()
{
    Assert.DoesNotContain(typeof(WidgetManager).GetFields(
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
        field => field.FieldType == typeof(App));

    Assert.DoesNotContain(typeof(WidgetWindow).GetFields(
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
        field => field.FieldType == typeof(App));
}
~~~

Pin environment laziness by constructing the manager with a throwing window factory that is never reached:

~~~csharp
[Fact]
public void Widget_manager_construction_does_not_create_webview_environment()
{
    var settings = TestSettings.Create();
    var theme = new ThemeService();
    var loc = new LocalizationService();
    loc.Initialize(settings.Current);
    int environmentCalls = 0;

    using var manager = new WidgetManager(
        settings,
        theme,
        loc,
        environmentFactory: () =>
        {
            environmentCalls++;
            throw new InvalidOperationException("environment must stay lazy");
        },
        refreshSamplingDemand: _ => { },
        windowFactory: (_, _, _, _, _) =>
            throw new InvalidOperationException("no window expected during construction"));

    Assert.Equal(0, environmentCalls);
}
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~WidgetRuntimeContextTests -v:minimal
~~~

Expected: failure because widget classes still store App.

- [ ] **Step 3: Replace broad App access**

Replace widget access to settings/theme/localization, monitor/events, fullscreen coverage, power state, resource pressure, and sampling-demand with named context dependencies.

Preserve lazy environment access:

~~~csharp
private Task<CoreWebView2Environment> EnvTask() => _environmentFactory();
~~~

Do not invoke the factory in constructors.

- [ ] **Step 4: Verify GREEN**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter "FullyQualifiedName~WidgetRuntimeContextTests|FullyQualifiedName~WidgetRpcHandlerTests|FullyQualifiedName~ResourceOptimizationTests" -v:minimal
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
~~~

Expected: both exit 0.

- [ ] **Step 5: Commit and push**

~~~powershell
git add src/VoltManager/Services/WidgetRuntimeContext.cs src/VoltManager/Services/WidgetManager.cs src/VoltManager/WidgetWindow.xaml.cs src/VoltManager/App.xaml.cs tests/VoltManager.Tests/WidgetRuntimeContextTests.cs
git commit -m "refactor: narrow widget runtime dependencies"
git push origin Dev
~~~

### Task 7: Final integration and lifecycle coverage

**Files:**
- Create: tests/VoltManager.Tests/ApplicationOrchestrationContractTests.cs
- Modify: src/VoltManager/App.xaml.cs
- Modify: src/VoltManager/App.Reliability.cs
- Modify: src/VoltManager/App.AdaptiveResources.cs
- Modify: src/VoltManager/MainWindow.xaml.cs
- Modify partials only where removing state now owned by coordinators.

**Interfaces:**
- Consumes all coordinators from Tasks 2-6.
- Produces final composition where App is host/composition root and MainWindow is UI adapter.
- Produces MainWindow.StopRuntime() or equivalent narrow method called before application shutdown.

- [ ] **Step 1: Write RED final architecture/lazy-start tests**

~~~csharp
[Fact]
public void App_no_longer_declares_coordinator_owned_timer_fields()
{
    string source = LocateSource("App.xaml.cs");
    Assert.DoesNotContain("_planPollTimer", source);
    Assert.DoesNotContain("_batteryHistoryTimer", source);
}

[Fact]
public void MainWindow_no_longer_declares_update_or_tray_timer_fields()
{
    string source = LocateSource("MainWindow.xaml.cs");
    Assert.DoesNotContain("_autoUpdateTimer", source);
    Assert.DoesNotContain("_trayTeardownTimer", source);
}
~~~

Reuse the repository-search approach already present in UnhandledExceptionPolicyTests:

~~~csharp
private static string LocateSource(string fileName)
{
    string? dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8 && dir != null; i++)
    {
        string candidate = Path.Combine(dir, "src", "VoltManager", fileName);
        if (File.Exists(candidate))
            return File.ReadAllText(candidate);
        dir = Directory.GetParent(dir)?.FullName;
    }
    throw new FileNotFoundException(
        "Could not locate " + fileName + " from " + AppContext.BaseDirectory);
}
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release --filter FullyQualifiedName~ApplicationOrchestrationContractTests -v:minimal
~~~

Expected: failure on remaining legacy ownership.

- [ ] **Step 3: Remove obsolete state and finalize composition**

Remove coordinator-owned fields from App/MainWindow. Keep only compatibility forwarders still required by bridge/widget consumers.

Ensure Closed/application exit routes through coordinator owners rather than disposing timer fields directly. Keep App.WebViewEnvironment lazy getter unchanged.

- [ ] **Step 4: Run full verification**

~~~powershell
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
node --test tests/*.test.mjs
dotnet build VoltManager.sln -c Release -v:minimal
dotnet run --project tests/VoltManager.WindowsHarness/VoltManager.WindowsHarness.csproj -c Release --no-build -- --mode deterministic --output TestResults/resource-validation
~~~

Expected: all commands exit 0.

- [ ] **Step 5: Commit and push**

~~~powershell
git add src/VoltManager tests/VoltManager.Tests
git status --short
git commit -m "refactor: finalize application lifecycle orchestration"
git push origin Dev
~~~

Inspect git status before committing and unstage unrelated/generated artifacts.

### Task 8: Full resource benchmark and completion audit

**Files:**
- Generated only under artifacts/resource-validation/issue-36-*; do not commit benchmark payloads.

**Interfaces:**
- Consumes baseline ddbfa613ea4c3c50272bc77976ee5f621496050b.
- Consumes candidate Dev HEAD after Task 7.
- Produces real benchmark-summary.json and Markdown.

- [ ] **Step 1: Build isolated baseline**

~~~powershell
git check-ignore .worktrees
git worktree add --detach .worktrees/issue36-baseline ddbfa613ea4c3c50272bc77976ee5f621496050b
dotnet publish .worktrees/issue36-baseline/src/VoltManager/VoltManager.csproj -c Release -r win-x64 --self-contained false -o artifacts/resource-validation/issue-36-baseline
~~~

If .worktrees is not ignored, create the detached baseline worktree in a temporary directory outside the repository.

- [ ] **Step 2: Build candidate and harness**

~~~powershell
dotnet publish src/VoltManager/VoltManager.csproj -c Release -r win-x64 --self-contained false -o artifacts/resource-validation/issue-36-candidate
dotnet build tests/VoltManager.WindowsHarness/VoltManager.WindowsHarness.csproj -c Release
~~~

Expected: both exit 0.

- [ ] **Step 3: Run the real protocol with default durations/repetitions**

~~~powershell
powershell.exe -NoProfile -NonInteractive -File scripts/Run-ResourceValidation.ps1 -BaselineApp artifacts/resource-validation/issue-36-baseline/VoltManager.exe -BaselineSupervisor artifacts/resource-validation/issue-36-baseline/VoltManager.Supervisor.exe -CandidateApp artifacts/resource-validation/issue-36-candidate/VoltManager.exe -CandidateSupervisor artifacts/resource-validation/issue-36-candidate/VoltManager.Supervisor.exe -Output artifacts/resource-validation/issue-36-final
~~~

Do not override the defaults: 30-second stabilization, 120-second measurement, five repetitions, plus script-selected extra repetitions on unstable data.

- [ ] **Step 4: Audit all issue criteria with fresh evidence**

~~~powershell
Get-Content artifacts/resource-validation/issue-36-final/benchmark-summary.md
dotnet test tests/VoltManager.Tests/VoltManager.Tests.csproj -c Release -v:minimal
node --test tests/*.test.mjs
dotnet build VoltManager.sln -c Release -v:minimal
git status --short --branch
git log --oneline ddbfa61..HEAD
~~~

Confirm normal/minimized/tray/reopen/recovery/close/uninstall coverage, idempotent lifecycle, no duplicate timer/listener cycles, unchanged power/manual precedence, lazy Chromium startup, protected-workload update behavior, benchmark no-regression, clean tree, and HEAD == origin/Dev.

- [ ] **Step 5: Remove only the temporary baseline worktree**

~~~powershell
git worktree remove .worktrees/issue36-baseline
git worktree prune
~~~

Keep benchmark evidence until the completion report is prepared.
