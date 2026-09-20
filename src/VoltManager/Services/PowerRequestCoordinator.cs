using System.Globalization;
using VoltManager.Models;
using VoltManager.Performance;

namespace VoltManager.Services;

internal sealed record PowerPolicyPipeline(
    Func<DateTime, bool> PowerSource,
    Func<DateTime, MetricsSnapshot, bool> Thermal,
    Func<DateTime, bool> Idle,
    Func<DateTime, bool> AppProfile,
    Func<DateTime, bool> HeavyApp,
    Action<MetricsSnapshot, DateTime> CpuAutomation);

public sealed class PowerRequestCoordinator : IDisposable
{
    private static readonly TimeSpan HeavyAppTeardownGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AppProfileTeardownGrace = TimeSpan.FromSeconds(15);

    private readonly RestartableLifecycle _lifecycle = new();
    private readonly object _epochGate = new();
    private readonly AsyncLocal<CancellationToken?> _callbackEpoch = new();
    private readonly PowerPolicyPipeline _pipeline;
    private readonly bool _policyOnly;

    private readonly SettingsService? _settings;
    private readonly PowerPlanService? _power;
    private readonly PowerAwakeService? _awake;
    private readonly AutomationEngine? _automation;
    private readonly AppPowerProfileService? _appProfiles;
    private readonly HeavyAppDetectionService? _heavyApps;
    private readonly PowerSourcePlanService? _powerSourcePlans;
    private readonly ThermalGuardService? _thermalGuard;
    private readonly IdlePowerGuardService? _idlePowerGuard;
    private readonly Func<ResourcePressureState?>? _resourcePressureState;
    private readonly PowerPlanGuardService _planGuard = new();

    private CancellationToken? _epochToken;
    private int _automationTickRunning;
    private double _lastAverageCpu;
    private bool _heavyAppPlanSessionActive;
    private PlanId? _planBeforeHeavyAppSession;
    private string _heavyAppHistoryName = "";
    private string _heavyAppHistoryKind = "";
    private string _heavyAppHistoryReason = "";
    private DateTime _heavyAppLastActiveUtc;
    private bool _heavyAppLastActiveWasGame;
    private bool _appProfilePlanSessionActive;
    private PlanId? _planBeforeAppProfileSession;
    private string _appProfileHistoryName = "";
    private DateTime _appProfileLastActiveUtc;
    private bool _appProfileKeepAwakeRequested;
    private ActivePlanReasonState _lastPublishedPlanReason = new();
    private ActivePlanReasonState _fallbackPlanReason = new();

    public PowerRequestCoordinator(
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
    {
        _settings = settings;
        _power = power;
        _awake = awake;
        _automation = automation;
        _appProfiles = appProfiles;
        _heavyApps = heavyApps;
        _powerSourcePlans = powerSourcePlans;
        _thermalGuard = thermalGuard;
        _idlePowerGuard = idlePowerGuard;
        _resourcePressureState = resourcePressureState;
        _pipeline = new PowerPolicyPipeline(
            HandlePowerSourcePlans,
            HandleThermalGuard,
            HandleIdlePowerGuard,
            HandleAppPowerProfiles,
            HandleHeavyAppDetection,
            HandleCpuAutomation);
    }

    private PowerRequestCoordinator(PowerPolicyPipeline pipeline)
    {
        _policyOnly = true;
        _pipeline = pipeline;
    }

    internal static PowerRequestCoordinator ForPolicyTest(PowerPolicyPipeline pipeline)
        => new(pipeline);

    private SettingsService Settings => _settings ?? throw new InvalidOperationException("Policy-test coordinator has no settings.");
    private PowerPlanService Power => _power ?? throw new InvalidOperationException("Policy-test coordinator has no power service.");
    private PowerAwakeService Awake => _awake ?? throw new InvalidOperationException("Policy-test coordinator has no awake service.");
    private AutomationEngine Automation => _automation ?? throw new InvalidOperationException("Policy-test coordinator has no automation engine.");
    private AppPowerProfileService AppProfiles => _appProfiles ?? throw new InvalidOperationException("Policy-test coordinator has no app-profile service.");
    private HeavyAppDetectionService HeavyApps => _heavyApps ?? throw new InvalidOperationException("Policy-test coordinator has no heavy-app service.");
    private PowerSourcePlanService PowerSourcePlans => _powerSourcePlans ?? throw new InvalidOperationException("Policy-test coordinator has no power-source service.");
    private ThermalGuardService ThermalGuard => _thermalGuard ?? throw new InvalidOperationException("Policy-test coordinator has no thermal service.");
    private IdlePowerGuardService IdlePowerGuard => _idlePowerGuard ?? throw new InvalidOperationException("Policy-test coordinator has no idle service.");

    public PowerPlan? ActivePlan { get; private set; }
    public CpuAutomationState CpuAutomationState { get; private set; } = new();
    public TimeSpan CurrentSamplingInterval { get; private set; } = TimeSpan.FromSeconds(1);

    public event Action<PowerPlan?>? ActivePlanChanged;
    public event Action<ManualOverride?>? ManualOverrideChanged;
    public event Action<CpuAutomationState>? CpuAutomationStateChanged;
    public event Action<ActivePlanReasonState>? ActivePlanReasonChanged;
    public event Action<PowerPlanConflictNotification>? PowerPlanConflictDetected;

    public void Start()
    {
        CancellationToken token = _lifecycle.Start();
        lock (_epochGate)
            _epochToken = token;

        if (_policyOnly)
            return;

        DateTime now = DateTime.UtcNow;
        ClearExpiredManualOverride(now);
        _planGuard.RefreshManualOverride(Settings.Current.Override, now);
        CurrentSamplingInterval = CpuAutomationSampleInterval();
        PublishCpuAutomationState(now);
    }

    public bool Stop()
    {
        lock (_epochGate)
            _epochToken = null;

        if (!_policyOnly)
            SetAppProfileKeepAwakeRequest(false);

        Interlocked.Exchange(ref _automationTickRunning, 0);
        return _lifecycle.Stop();
    }

    public void Dispose()
    {
        Stop();
        _lifecycle.Dispose();
    }

    public void ProcessMetrics(MetricsSnapshot metrics, DateTime nowUtc)
    {
        if (!TryGetEpoch(out CancellationToken epoch))
            return;

        if (Interlocked.Exchange(ref _automationTickRunning, 1) == 1)
            return;

        _callbackEpoch.Value = epoch;
        try
        {
            if (!_policyOnly)
            {
                _lastAverageCpu = Automation.AddSample(metrics.Cpu, nowUtc);
                ClearExpiredManualOverride(nowUtc);
                _planGuard.RefreshManualOverride(Settings.Current.Override, nowUtc);
                SyncAppProfileKeepAwakeRequest(nowUtc);
            }

            if (!CallbackIsCurrent())
                return;

            bool handled =
                _pipeline.PowerSource(nowUtc) ||
                _pipeline.Thermal(nowUtc, metrics) ||
                _pipeline.Idle(nowUtc) ||
                _pipeline.AppProfile(nowUtc) ||
                _pipeline.HeavyApp(nowUtc);

            if (!handled && CallbackIsCurrent())
                _pipeline.CpuAutomation(metrics, nowUtc);

            if (!_policyOnly && CallbackIsCurrent())
            {
                PublishCpuAutomationState(nowUtc);
                PublishActivePlanReason();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Automation sample handling failed", ex);
        }
        finally
        {
            _callbackEpoch.Value = null;
            Interlocked.Exchange(ref _automationTickRunning, 0);
        }
    }

    public void UpdateSamplingPeriod(MonitorService monitor)
    {
        EnsureProduction();
        TimeSpan interval = CpuAutomationSampleInterval();
        if (interval == CurrentSamplingInterval)
        {
            PublishCpuAutomationState(DateTime.UtcNow);
            return;
        }

        CurrentSamplingInterval = interval;
        monitor.SetInterval(interval);
        Automation.Reset();
        PublishCpuAutomationState(DateTime.UtcNow);
    }

    public PowerPlan? RefreshActivePlanFromSystem(DateTime nowUtc)
    {
        EnsureProduction();
        if (!_lifecycle.IsStarted)
            return ActivePlan;

        var current = Power.GetActivePlan();
        current = ReassertExpectedPlanIfNeeded(current, nowUtc) ?? current;
        if (current?.Guid != ActivePlan?.Guid)
            SetActivePlan(current);
        return ActivePlan;
    }

    public bool SetManualOverride(
        PlanId plan,
        TimeSpan? duration,
        string source = "manual",
        string reasonCode = "manual_override")
    {
        EnsureProduction();
        _appProfilePlanSessionActive = false;
        _planBeforeAppProfileSession = null;
        _appProfileHistoryName = "";
        SetAppProfileKeepAwakeRequest(false);
        _heavyAppPlanSessionActive = false;
        _planBeforeHeavyAppSession = null;
        _heavyAppHistoryName = "";
        _heavyAppHistoryKind = "";
        _heavyAppHistoryReason = "";

        if (!Power.SetActivePlan(
                plan,
                HistoryContext(
                    PlanHistoryCategory.Manual,
                    source,
                    reasonCode,
                    ("durationMinutes", duration?.TotalMinutes.ToString(CultureInfo.InvariantCulture)))))
            return false;

        var manualOverride = new ManualOverride
        {
            Plan = ToPlanKey(plan),
            ExpiresAtUtc = duration == null ? null : DateTime.UtcNow.Add(duration.Value),
        };
        Settings.Update(state => state.Override = manualOverride);
        _planGuard.SetExpected(plan, "manualOverride", ToPlanKey(plan));
        Automation.Reset();

        SetActivePlan(Power.GetActivePlan());
        if (CanPublish())
            ManualOverrideChanged?.Invoke(manualOverride);
        PublishCpuAutomationState(DateTime.UtcNow);
        PublishActivePlanReason();
        return true;
    }

    public void SetAutomaticMode()
    {
        EnsureProduction();
        Settings.Update(state =>
        {
            state.Override = null;
            state.MasterAutomationEnabled = true;
        });
        _planGuard.ClearExpected();
        _fallbackPlanReason = new ActivePlanReasonState { Plan = ActivePlan?.PlanId };
        Automation.Reset();
        if (CanPublish())
            ManualOverrideChanged?.Invoke(null);
        PublishCpuAutomationState(DateTime.UtcNow);
        PublishActivePlanReason();
    }

    public void ClearManualOverride()
    {
        EnsureProduction();
        if (Settings.Current.Override == null)
            return;

        Settings.Update(state => state.Override = null);
        _planGuard.ClearExpected("manualOverride");
        Automation.Reset();
        if (CanPublish())
            ManualOverrideChanged?.Invoke(null);
        PublishCpuAutomationState(DateTime.UtcNow);
        PublishActivePlanReason();
    }

    public HeavyAppDetectionState GetHeavyAppStatus() => HeavyApps.Current;
    public HeavyAppDetectionState RefreshHeavyAppDetection() => HeavyApps.Refresh();
    public AppPowerProfileState GetAppPowerProfileStatus() => AppProfiles.Current;
    public AppPowerProfileState RefreshAppPowerProfiles() => AppProfiles.Refresh();

    public bool IsProtectedWorkloadActive()
        => (_resourcePressureState?.Invoke()?.ProtectedWorkloadActive ?? false) || HeavyApps.Current.ProtectedWorkloadActive;

    public ActivePlanReasonState GetActivePlanReason()
    {
        EnsureProduction();
        var expected = _planGuard.Expectation;
        if (expected != null && expected.Plan == ActivePlan?.PlanId)
        {
            return new ActivePlanReasonState
            {
                Source = expected.Source,
                Detail = expected.Detail,
                Plan = expected.Plan,
            };
        }

        return _fallbackPlanReason.Plan == ActivePlan?.PlanId
            ? _fallbackPlanReason
            : new ActivePlanReasonState { Plan = ActivePlan?.PlanId };
    }

    public PowerSourcePlanState GetPowerSourcePlanState()
        => PowerSourcePlans.RefreshState(Settings.Current.Override?.IsActive(DateTime.UtcNow) == true);

    public PowerSourcePlanState SetPowerSourcePlanSwitch(bool enabled)
    {
        PowerSourcePlans.SetEnabled(enabled, Settings.Current.Override?.IsActive(DateTime.UtcNow) == true);
        HandlePowerSourcePlans(DateTime.UtcNow);
        return PowerSourcePlans.Current;
    }

    private bool TryGetEpoch(out CancellationToken token)
    {
        lock (_epochGate)
        {
            if (_epochToken is CancellationToken current && _lifecycle.IsCurrent(current))
            {
                token = current;
                return true;
            }
        }

        token = default;
        return false;
    }

    private bool CallbackIsCurrent()
        => _callbackEpoch.Value is CancellationToken token
            ? _lifecycle.IsCurrent(token)
            : _lifecycle.IsStarted;

    private bool CanPublish() => CallbackIsCurrent();

    private void EnsureProduction()
    {
        if (_policyOnly)
            throw new InvalidOperationException("Operation is unavailable on a policy-test coordinator.");
    }

    private TimeSpan CpuAutomationSampleInterval()
    {
        var state = Settings.Current;
        state.CpuAutomation.Normalize();
        return TimeSpan.FromSeconds(state.CpuAutomation.SampleIntervalSeconds);
    }

    private void HandleCpuAutomation(MetricsSnapshot metrics, DateTime now)
    {
        if (!CallbackIsCurrent())
            return;

        var target = Automation.Evaluate(_lastAverageCpu, now, ActivePlan?.PlanId, Settings.Current);
        var rule = target == null || string.IsNullOrWhiteSpace(Automation.CandidateRuleId)
            ? null
            : Settings.Current.Rules.FirstOrDefault(r => r.Id == Automation.CandidateRuleId);
        var context = target == null ? null : HistoryContext(
            PlanHistoryCategory.Automatic,
            "cpuAutomation",
            "cpu_rule_triggered",
            ("ruleId", rule?.Id),
            ("comparison", rule?.Comparison),
            ("thresholdPct", Invariant(rule?.ThresholdPct)),
            ("durationMinutes", Invariant(rule?.DurationMinutes)),
            ("averageCpu", Invariant(_lastAverageCpu)));

        if (target != null && CallbackIsCurrent() && Power.SetActivePlan(target.Value, context))
        {
            _fallbackPlanReason = new ActivePlanReasonState
            {
                Source = "cpuAutomation",
                Detail = Automation.CandidateRuleId ?? "",
                Plan = target.Value,
            };
            SetActivePlan(Power.GetActivePlan());
        }
    }

    private void PublishCpuAutomationState(DateTime now)
    {
        if (_policyOnly || !CanPublish())
            return;

        var settings = Settings.Current;
        settings.CpuAutomation.Normalize();
        bool manualOverrideActive = settings.Override?.IsActive(now) == true;
        var candidate = string.IsNullOrWhiteSpace(Automation.CandidateRuleId)
            ? null
            : settings.Rules.FirstOrDefault(r => r.Id == Automation.CandidateRuleId);

        CpuAutomationState = new CpuAutomationState
        {
            Enabled = settings.MasterAutomationEnabled && !manualOverrideActive,
            SampleIntervalSeconds = settings.CpuAutomation.SampleIntervalSeconds,
            RawCpu = Automation.LastRawCpu,
            AverageCpu = Automation.LastAverageCpu,
            SampledAtUtc = Automation.LastSampledAtUtc,
            CandidateRuleId = Automation.CandidateRuleId,
            CandidateTargetPlan = candidate?.TargetPlan,
            ActivePlan = ActivePlan?.PlanId,
            ManualOverrideActive = manualOverrideActive,
        };
        CpuAutomationStateChanged?.Invoke(CpuAutomationState);
    }

    private bool HandleAppPowerProfiles(DateTime now)
    {
        if (!CallbackIsCurrent()) return true;
        var config = Settings.Current.AppPowerProfiles ?? new AppPowerProfileSettings();
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        bool canAutoSwitch = Settings.Current.MasterAutomationEnabled && config.Enabled && !userOverrideActive;
        var state = AppProfiles.Current;

        if (canAutoSwitch && state.Active && state.TargetPlan != null)
        {
            _appProfileLastActiveUtc = now;
            var profileName = state.ActiveProfiles.FirstOrDefault()?.Name ?? "";
            if (!_appProfilePlanSessionActive)
            {
                _planBeforeAppProfileSession = ActivePlan?.PlanId;
                _appProfilePlanSessionActive = true;
                Automation.Reset();
            }

            _appProfileHistoryName = profileName;
            var target = state.TargetPlan.Value;
            _planGuard.SetExpected(target, "appProfile", state.ActiveProfiles.FirstOrDefault()?.Name ?? "");
            if (ActivePlan?.PlanId == target)
                return true;

            if (CallbackIsCurrent() && Power.SetActivePlan(target, HistoryContext(
                    PlanHistoryCategory.Automatic,
                    "appProfile",
                    "profile_applied",
                    ("appName", profileName))))
                SetActivePlan(Power.GetActivePlan());
            return true;
        }

        if (_appProfilePlanSessionActive)
        {
            if (canAutoSwitch && now - _appProfileLastActiveUtc < AppProfileTeardownGrace)
                return true;

            _appProfilePlanSessionActive = false;
            var previous = _planBeforeAppProfileSession;
            var profileName = _appProfileHistoryName;
            _planBeforeAppProfileSession = null;
            _appProfileHistoryName = "";
            _planGuard.ClearExpected("appProfile");
            Automation.Reset();

            if (!userOverrideActive && previous != null && ActivePlan?.PlanId != previous && CallbackIsCurrent() && Power.SetActivePlan(
                    previous.Value,
                    HistoryContext(
                        PlanHistoryCategory.Automatic,
                        "appProfile",
                        "profile_session_ended",
                        ("appName", profileName))))
                SetActivePlan(Power.GetActivePlan());
            return true;
        }

        return false;
    }

    private void SyncAppProfileKeepAwakeRequest(DateTime now)
    {
        var cfg = Settings.Current.AppPowerProfiles ?? new AppPowerProfileSettings();
        bool requested = Settings.Current.MasterAutomationEnabled
            && cfg.Enabled
            && Settings.Current.Override?.IsActive(now) != true
            && AppProfiles.Current.Active
            && AppProfiles.Current.KeepAwakeRequested;
        SetAppProfileKeepAwakeRequest(requested);
    }

    private void SetAppProfileKeepAwakeRequest(bool requested)
    {
        if (_policyOnly || _appProfileKeepAwakeRequested == requested)
            return;
        _appProfileKeepAwakeRequested = requested;
        Awake.SetAutomationRequest(requested);
    }

    private bool HandleHeavyAppDetection(DateTime now)
    {
        if (!CallbackIsCurrent()) return true;
        var config = Settings.Current.HeavyAppDetection;
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        bool canAutoSwitch = Settings.Current.MasterAutomationEnabled && config.Enabled && !userOverrideActive;
        var state = HeavyApps.Current;

        if (canAutoSwitch && state.PlanSwitchActive)
        {
            _heavyAppLastActiveUtc = now;
            _heavyAppLastActiveWasGame = state.GameActive;
            var activeProcess = state.ActiveProcesses.FirstOrDefault();
            if (!_heavyAppPlanSessionActive)
            {
                _planBeforeHeavyAppSession = ActivePlan?.PlanId;
                _heavyAppPlanSessionActive = true;
                Automation.Reset();
            }

            _heavyAppHistoryName = activeProcess?.Name ?? "";
            _heavyAppHistoryKind = activeProcess?.Kind ?? "";
            _heavyAppHistoryReason = activeProcess?.Reason ?? "";
            var target = state.TargetPlan;
            _planGuard.SetExpected(target, "heavyApp", state.ActiveProcesses.FirstOrDefault()?.Name ?? "");
            if (ActivePlan?.PlanId == target)
                return true;

            if (CallbackIsCurrent() && Power.SetActivePlan(target, HistoryContext(
                    PlanHistoryCategory.Automatic,
                    "heavyApp",
                    state.GameActive ? "game_load_detected" : "heavy_app_load_detected",
                    ("appName", activeProcess?.Name),
                    ("kind", activeProcess?.Kind),
                    ("detectionReason", activeProcess?.Reason))))
                SetActivePlan(Power.GetActivePlan());
            return true;
        }

        if (_heavyAppPlanSessionActive)
        {
            if (canAutoSwitch && _heavyAppLastActiveWasGame && now - _heavyAppLastActiveUtc < HeavyAppTeardownGrace)
                return true;

            _heavyAppPlanSessionActive = false;
            _heavyAppLastActiveWasGame = false;
            var previous = _planBeforeHeavyAppSession;
            var appName = _heavyAppHistoryName;
            var kind = _heavyAppHistoryKind;
            var detectionReason = _heavyAppHistoryReason;
            _planBeforeHeavyAppSession = null;
            _heavyAppHistoryName = "";
            _heavyAppHistoryKind = "";
            _heavyAppHistoryReason = "";
            _planGuard.ClearExpected("heavyApp");
            Automation.Reset();

            if (!userOverrideActive && previous != null && ActivePlan?.PlanId != previous && CallbackIsCurrent() && Power.SetActivePlan(
                    previous.Value,
                    HistoryContext(
                        PlanHistoryCategory.Automatic,
                        "heavyApp",
                        "heavy_app_session_ended",
                        ("appName", appName),
                        ("kind", kind),
                        ("detectionReason", detectionReason))))
                SetActivePlan(Power.GetActivePlan());
            return true;
        }

        return false;
    }

    private bool HandleThermalGuard(DateTime now, MetricsSnapshot metrics)
    {
        if (!CallbackIsCurrent()) return true;
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        var decision = ThermalGuard.Evaluate(
            metrics.CpuTemp,
            metrics.GpuTemp,
            ActivePlan?.PlanId,
            userOverrideActive,
            Settings.Current.MasterAutomationEnabled,
            now);

        if (decision.State.Active)
            _planGuard.SetExpected(decision.State.TargetPlan, "thermal", decision.State.Message);
        else
            _planGuard.ClearExpected("thermal");

        if (decision.TargetPlan != null && CallbackIsCurrent() && Power.SetActivePlan(
                decision.TargetPlan.Value,
                HistoryContext(
                    PlanHistoryCategory.Automatic,
                    "thermal",
                    decision.State.Message,
                    ("peakTemp", Invariant(decision.State.PeakTemp)),
                    ("thresholdCelsius", Invariant(decision.State.ThresholdCelsius)),
                    ("coolThresholdCelsius", Invariant(decision.State.CoolThresholdCelsius)),
                    ("holdSeconds", decision.State.HoldSeconds.ToString(CultureInfo.InvariantCulture)))))
            SetActivePlan(Power.GetActivePlan());

        if (decision.BlocksLowerPriority)
            Automation.Reset();
        return decision.BlocksLowerPriority;
    }

    private bool HandleIdlePowerGuard(DateTime now)
    {
        if (!CallbackIsCurrent()) return true;
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        var decision = IdlePowerGuard.Evaluate(
            ActivePlan?.PlanId,
            userOverrideActive,
            Settings.Current.MasterAutomationEnabled);

        if (decision.State.Active)
            _planGuard.SetExpected(decision.State.TargetPlan, "idle", decision.State.Message);
        else
            _planGuard.ClearExpected("idle");

        if (decision.TargetPlan != null && CallbackIsCurrent() && Power.SetActivePlan(
                decision.TargetPlan.Value,
                HistoryContext(
                    PlanHistoryCategory.Automatic,
                    "idle",
                    decision.State.Message,
                    ("idleSeconds", Invariant(decision.State.IdleSeconds)),
                    ("idleMinutes", decision.State.IdleMinutes.ToString(CultureInfo.InvariantCulture)),
                    ("onlyOnBattery", decision.State.OnlyOnBattery.ToString()))))
            SetActivePlan(Power.GetActivePlan());

        if (decision.BlocksLowerPriority)
            Automation.Reset();
        return decision.BlocksLowerPriority;
    }

    private bool HandlePowerSourcePlans(DateTime now)
    {
        if (!CallbackIsCurrent()) return true;
        bool userOverrideActive = Settings.Current.Override?.IsActive(now) == true;
        var decision = PowerSourcePlans.Evaluate(ActivePlan?.PlanId, userOverrideActive);
        var expectedPowerSourcePlan = ExpectedPowerSourcePlan(decision);
        if (expectedPowerSourcePlan != null)
            _planGuard.SetExpected(expectedPowerSourcePlan.Value, "powerSource", decision.State.Message);
        else
            _planGuard.ClearExpected("powerSource");

        if (decision.TargetPlan != null && CallbackIsCurrent() && Power.SetActivePlan(
                decision.TargetPlan.Value,
                HistoryContext(
                    PlanHistoryCategory.Automatic,
                    "powerSource",
                    decision.State.Message,
                    ("pluggedIn", decision.State.PluggedIn.ToString()),
                    ("batteryPercent", decision.State.BatteryPercent?.ToString(CultureInfo.InvariantCulture)),
                    ("lowBatteryThresholdPercent", decision.State.LowBatteryThresholdPercent.ToString(CultureInfo.InvariantCulture)))))
            SetActivePlan(Power.GetActivePlan());

        if (decision.BlocksLowerPriority)
            Automation.Reset();
        return decision.BlocksLowerPriority;
    }

    private static PlanId? ExpectedPowerSourcePlan(PowerSourcePlanDecision decision)
    {
        if (!decision.BlocksLowerPriority) return null;
        if (decision.TargetPlan != null) return decision.TargetPlan.Value;
        if (decision.State.LowBatteryActive) return PlanId.PowerSaver;
        if (decision.State.Active && decision.State.PluggedIn) return decision.State.PluggedPlan;
        return null;
    }

    private PowerPlan? ReassertExpectedPlanIfNeeded(PowerPlan? current, DateTime now)
    {
        if (!_planGuard.ShouldReassert(current?.PlanId, now, out var conflict) || conflict == null)
            return current;

        var suspects = PowerPlanGuardService.FindLikelyInterferingProcesses();
        var enriched = PowerPlanGuardService.WithSuspectsAndMessage(conflict, suspects);
        Logger.Warn(enriched.Message);

        if (!Power.SetActivePlan(
                conflict.ExpectedPlan,
                HistoryContext(
                    PlanHistoryCategory.Automatic,
                    "planGuard",
                    "expected_plan_restored",
                    ("expectedSource", conflict.Source),
                    ("expectedDetail", conflict.Detail))))
            return current;

        var restored = Power.GetActivePlan();
        if (enriched.ShouldNotifyUser && CanPublish())
            PowerPlanConflictDetected?.Invoke(enriched);
        return restored ?? current;
    }

    private void ClearExpiredManualOverride(DateTime now)
    {
        var currentOverride = Settings.Current.Override;
        if (currentOverride?.ExpiresAtUtc == null || currentOverride.ExpiresAtUtc > now)
            return;

        Settings.Update(state => state.Override = null);
        _planGuard.ClearExpected("manualOverride");
        Automation.Reset();
        if (CanPublish())
            ManualOverrideChanged?.Invoke(null);
        PublishCpuAutomationState(now);
    }

    private void PublishActivePlanReason()
    {
        if (!CanPublish())
            return;
        var next = GetActivePlanReason();
        if (next == _lastPublishedPlanReason)
            return;
        _lastPublishedPlanReason = next;
        ActivePlanReasonChanged?.Invoke(next);
    }

    private void SetActivePlan(PowerPlan? current)
    {
        ActivePlan = current;
        if (CanPublish())
            ActivePlanChanged?.Invoke(current);
    }

    private static PlanChangeContext HistoryContext(
        PlanHistoryCategory category,
        string source,
        string reasonCode,
        params (string Key, string? Value)[] details)
        => new(
            category,
            source,
            reasonCode,
            details
                .Where(item => item.Value != null)
                .ToDictionary(item => item.Key, item => item.Value!, StringComparer.Ordinal));

    private static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string? Invariant(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ToPlanKey(PlanId plan) => plan switch
    {
        PlanId.PowerSaver => "powerSaver",
        PlanId.Balanced => "balanced",
        PlanId.Performance => "performance",
        _ => "",
    };
}
