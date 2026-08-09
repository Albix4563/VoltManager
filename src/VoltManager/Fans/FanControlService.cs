using VoltManager.Services;

namespace VoltManager.Fans;

/// <summary>
/// Supervises all software-owned fan sessions. A write is never fire-and-forget:
/// active sessions are revalidated for topology, sensor freshness and external
/// controller conflicts. Any loss of required evidence causes a best-effort
/// RestoreDefault and removes the software session.
/// </summary>
public sealed class FanControlService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TelemetryStaleAfter = TimeSpan.FromSeconds(8);
    private const double MinWriteDelta = 1.5;

    private readonly Func<bool, FanTopology> _topologyProvider;
    private readonly FanSafetyPolicy _safety;
    private readonly IFanBackend _backend;
    private readonly FanControlRecoveryStore _recoveryStore;
    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<FanControlRecoveryEntry> _recoveryPending;
    private readonly System.Threading.Timer _timer;
    private int _tickRunning;
    private bool _disposed;
    private string? _lastError;

    public event Action<FanControlRuntimeState>? StateChanged;

    public FanControlService(
        MonitorService monitor,
        FanDiscoveryService discovery,
        FanAliasStore aliases,
        FanExternalConflictDetector conflicts,
        IFanBackend backend,
        FanSafetyPolicy? safety = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(conflicts);
        _topologyProvider = force => discovery.BuildTopology(monitor.Latest, aliases.GetAll(), conflicts.Scan(force));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _safety = safety ?? new FanSafetyPolicy();
        _recoveryStore = new FanControlRecoveryStore();
        _recoveryPending = _recoveryStore.Load();
        if (_recoveryPending.Count > 0) _lastError = "recovering_previous_session";
        _timer = new System.Threading.Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    internal FanControlService(
        Func<bool, FanTopology> topologyProvider,
        IFanBackend backend,
        FanSafetyPolicy? safety = null,
        bool startTimer = false)
    {
        _topologyProvider = topologyProvider ?? throw new ArgumentNullException(nameof(topologyProvider));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _safety = safety ?? new FanSafetyPolicy();
        _recoveryStore = FanControlRecoveryStore.Disabled;
        _recoveryPending = new List<FanControlRecoveryEntry>();
        _timer = new System.Threading.Timer(
            _ => Tick(),
            null,
            startTimer ? TickInterval : Timeout.InfiniteTimeSpan,
            startTimer ? TickInterval : Timeout.InfiniteTimeSpan);
    }

    public FanControlRuntimeState Current
    {
        get
        {
            lock (_gate) return SnapshotLocked();
        }
    }

    public FanConfiguration? GetActiveConfiguration(string fanId)
    {
        lock (_gate)
            return _sessions.TryGetValue(fanId, out ActiveSession? session)
                ? CloneConfiguration(session.Configuration)
                : null;
    }

    public FanConfigurationPreview Preview(string fanId, FanConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        FanTopology topology = BuildTopology(forceConflictScan: false);
        FanDevice? fan = topology.Devices.FirstOrDefault(x => x.Id == fanId);
        if (fan == null)
            return InvalidPreview(fanId, "device_missing", "The selected fan is no longer present.");

        FanConfiguration normalized = NormalizeConfiguration(fan, configuration);
        double? temperature = ResolveReferenceTemperature(fan, normalized);
        double? requested = RequestedControl(normalized, temperature);
        FanSafetyDecision decision = _safety.Validate(fan, normalized, topology.ExternalSoftware, temperature);

        var warnings = new List<string>();
        if (decision.SafetyOverrideActive)
            warnings.Add("VoltManager's thermal safety ramp increases the requested output at the current temperature.");
        if (topology.ExternalSoftware.Any(x => !x.BlocksControl))
            warnings.Add("A hardware utility is running but device ownership is not confirmed.");

        return new FanConfigurationPreview
        {
            Valid = decision.Allowed,
            FanId = fanId,
            ReferenceTemperature = temperature,
            RequestedControlPercent = requested,
            EffectiveControlPercent = decision.EffectiveControlPercent,
            SafetyOverrideActive = decision.SafetyOverrideActive,
            Warnings = warnings,
            Errors = decision.Allowed ? new List<string>() : new List<string> { decision.Message },
        };
    }

    public FanApplyResult Apply(string topologyRevision, string fanId, FanConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_disposed) return Fail(fanId, configuration.Mode, "disposed", "Fan control service is shutting down.");
        lock (_gate)
        {
            if (_recoveryPending.Count > 0)
                return Fail(fanId, configuration.Mode, "recovery_pending", "VoltManager is still trying to release a fan-control lease from a previous unclean shutdown.");
        }

        FanTopology topology = BuildTopology(forceConflictScan: true);
        if (!string.Equals(topologyRevision, topology.Revision, StringComparison.Ordinal))
            return Fail(fanId, configuration.Mode, "topology_changed", "Hardware topology changed. Refresh the Fan Center before applying changes.");

        FanDevice? fan = topology.Devices.FirstOrDefault(x => x.Id == fanId);
        if (fan == null)
            return Fail(fanId, configuration.Mode, "device_missing", "The selected fan is no longer present.");

        FanConfiguration normalized = NormalizeConfiguration(fan, configuration);
        if (normalized.Mode == FanMode.Automatic && HasActiveSession(fanId))
            return Restore(fanId, fan, "automatic_release");

        double? temperature = ResolveReferenceTemperature(fan, normalized);
        FanSafetyDecision decision = _safety.Validate(fan, normalized, topology.ExternalSoftware, temperature);
        if (!decision.Allowed)
            return Fail(fanId, normalized.Mode, decision.Code, decision.Message);

        if (normalized.Mode == FanMode.Automatic)
            return Restore(fanId, fan, "automatic");

        if (!_backend.CanHandle(fan))
            return Fail(fanId, normalized.Mode, "backend_unavailable", "No fan backend can safely write this channel.");

        double target = decision.EffectiveControlPercent!.Value;
        // Persist ownership intent before crossing the process/hardware boundary.
        // If the write succeeds but its IPC reply is lost, the next recovery pass
        // still knows that this channel may require SetDefault().
        QueueRecovery(fan);
        PersistRecoveryMarker();
        FanBackendWriteResult write = _backend.SetSoftware(fan, target);
        if (!write.Success)
        {
            BestEffortRestore(fan);
            PersistRecoveryMarker();
            return Fail(fanId, normalized.Mode, write.Code, write.Message);
        }

        lock (_gate)
        {
            _sessions[fanId] = new ActiveSession
            {
                Fan = fan,
                Configuration = CloneConfiguration(normalized),
                LastAppliedControlPercent = write.EffectiveControlPercent ?? target,
                LastWriteUtc = DateTime.UtcNow,
                Status = decision.SafetyOverrideActive ? "safety_override" : "active",
            };
            // The pre-write recovery lease is now represented by the supervised
            // active session. Keep it on disk through PersistRecoveryMarker(), but
            // do not treat it as an orphaned lease during watchdog ticks.
            _recoveryPending.RemoveAll(x => x.FanId == fanId || x.ControlIdentifier == fan.ControlIdentifier);
            _lastError = null;
        }
        PersistRecoveryMarker();
        Publish();

        return new FanApplyResult
        {
            Success = true,
            Code = "ok",
            FanId = fanId,
            Mode = normalized.Mode,
            AppliedControlPercent = write.EffectiveControlPercent ?? target,
            SafetyOverrideActive = decision.SafetyOverrideActive,
        };
    }

    public FanApplyResult Restore(string fanId)
    {
        bool owned = HasActiveSession(fanId);
        FanTopology topology = BuildTopology(forceConflictScan: true);
        if (!owned && topology.ExternalSoftware.Any(x => x.BlocksControl))
            return Fail(fanId, FanMode.Automatic, "external_controller", "Another fan/hardware utility is active; VoltManager will not write to this controller.");

        FanDevice? fan = topology.Devices.FirstOrDefault(x => x.Id == fanId);
        lock (_gate)
        {
            if (fan == null && _sessions.TryGetValue(fanId, out ActiveSession? session)) fan = session.Fan;
        }
        return fan == null
            ? Fail(fanId, FanMode.Automatic, "device_missing", "The selected fan is no longer present.")
            : Restore(fanId, fan, "user_restore");
    }

    public IReadOnlyDictionary<string, List<FanCurvePoint>> GetPresets(string fanId)
    {
        FanDevice? fan = BuildTopology(false).Devices.FirstOrDefault(x => x.Id == fanId);
        if (fan?.Capabilities.MinimumControl is not { } min || fan.Capabilities.MaximumControl is not { } max || max <= min)
            return new Dictionary<string, List<FanCurvePoint>>();

        return new Dictionary<string, List<FanCurvePoint>>(StringComparer.OrdinalIgnoreCase)
        {
            ["silent"] = FanCurveEngine.CreatePreset(FanCurvePreset.Silent, min, max),
            ["balanced"] = FanCurveEngine.CreatePreset(FanCurvePreset.Balanced, min, max),
            ["performance"] = FanCurveEngine.CreatePreset(FanCurvePreset.Performance, min, max),
        };
    }

    public void SuspendAll(string reason = "suspend") => RestoreAll(reason);

    public void Resume()
    {
        // Deliberately do not reapply previous software values after sleep/resume.
        // Hardware is rediscovered and remains on its default policy until the user
        // or a profile explicitly starts a fresh validated control session.
        lock (_gate) _lastError = null;
        Publish();
    }

    private void Tick()
    {
        if (_disposed || Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
        try
        {
            if (HasPendingRecovery())
            {
                AttemptPendingRecovery();
                if (HasPendingRecovery()) return;
            }

            List<ActiveSession> sessions;
            lock (_gate) sessions = _sessions.Values.Select(CloneSession).ToList();
            if (sessions.Count == 0) return;

            FanTopology topology = BuildTopology(forceConflictScan: false);
            if (topology.ExternalSoftware.Any(x => x.BlocksControl))
            {
                RestoreAll("external_controller_detected");
                return;
            }

            foreach (ActiveSession session in sessions)
                TickSession(session, topology);
        }
        catch (Exception ex)
        {
            Logger.Error("Fan control watchdog failed", ex);
            RestoreAll("watchdog_failure");
        }
        finally
        {
            Interlocked.Exchange(ref _tickRunning, 0);
        }
    }

    private void TickSession(ActiveSession session, FanTopology topology)
    {
        FanDevice? fan = topology.Devices.FirstOrDefault(x => x.Id == session.Fan.Id);
        if (fan == null)
        {
            BestEffortRestore(session.Fan);
            RemoveSession(session.Fan.Id, "device_disconnected");
            return;
        }

        FanConfiguration normalized = NormalizeConfiguration(fan, session.Configuration);
        double? temperature = ResolveReferenceTemperature(fan, normalized);
        if (!temperature.HasValue || DateTime.UtcNow - fan.Telemetry.LastUpdatedUtc > TelemetryStaleAfter)
        {
            BestEffortRestore(fan);
            RemoveSession(fan.Id, "sensor_unavailable");
            return;
        }

        FanSafetyDecision decision = _safety.Validate(fan, normalized, topology.ExternalSoftware, temperature);
        if (!decision.Allowed || !decision.EffectiveControlPercent.HasValue)
        {
            BestEffortRestore(fan);
            RemoveSession(fan.Id, decision.Code);
            return;
        }

        double target = decision.EffectiveControlPercent.Value;
        if (normalized.Mode == FanMode.Curve)
            target = FanCurveEngine.ApplyDownwardRateLimit(session.LastAppliedControlPercent, target);
        target = FanSafetyPolicy.ApplyThermalGuard(target, temperature, fan.Capabilities.MaximumControl ?? target);

        bool periodicVerify = DateTime.UtcNow - session.LastWriteUtc >= TimeSpan.FromSeconds(10);
        if (!periodicVerify && session.LastAppliedControlPercent.HasValue &&
            Math.Abs(target - session.LastAppliedControlPercent.Value) < MinWriteDelta)
            return;

        FanBackendWriteResult write = _backend.SetSoftware(fan, target);
        if (!write.Success)
        {
            BestEffortRestore(fan);
            RemoveSession(fan.Id, write.Code);
            return;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(fan.Id, out ActiveSession? live)) return;
            live.Fan = fan;
            live.LastAppliedControlPercent = write.EffectiveControlPercent ?? target;
            live.LastWriteUtc = DateTime.UtcNow;
            live.Status = decision.SafetyOverrideActive ? "safety_override" : "active";
        }
        Publish();
    }

    private FanApplyResult Restore(string fanId, FanDevice fan, string reason)
    {
        FanBackendWriteResult restore = _backend.RestoreDefault(fan);
        if (!restore.Success)
        {
            lock (_gate) _lastError = restore.Message;
            Publish();
            return Fail(fanId, FanMode.Automatic, restore.Code, restore.Message);
        }

        lock (_gate)
        {
            _sessions.Remove(fanId);
            _recoveryPending.RemoveAll(x => x.FanId == fanId || x.ControlIdentifier == fan.ControlIdentifier);
            _lastError = null;
        }
        PersistRecoveryMarker();
        Logger.Info($"Fan control released: {fan.DisplayName} ({reason}).");
        Publish();
        return new FanApplyResult
        {
            Success = true,
            Code = "ok",
            FanId = fanId,
            Mode = FanMode.Automatic,
        };
    }

    private void RestoreAll(string reason)
    {
        List<ActiveSession> sessions;
        lock (_gate) sessions = _sessions.Values.Select(CloneSession).ToList();
        foreach (ActiveSession session in sessions) BestEffortRestore(session.Fan);
        lock (_gate)
        {
            _sessions.Clear();
            _lastError = reason;
        }
        PersistRecoveryMarker();
        Publish();
    }

    private bool BestEffortRestore(FanDevice fan)
    {
        try
        {
            FanBackendWriteResult result = _backend.RestoreDefault(fan);
            if (result.Success)
            {
                lock (_gate) _recoveryPending.RemoveAll(x => x.FanId == fan.Id || x.ControlIdentifier == fan.ControlIdentifier);
                return true;
            }
            Logger.Warn($"Could not restore default fan control for {fan.DisplayName}: {result.Message}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not restore default fan control for {fan.DisplayName}: {ex.Message}");
        }
        QueueRecovery(fan);
        return false;
    }

    private void RemoveSession(string fanId, string reason)
    {
        lock (_gate)
        {
            _sessions.Remove(fanId);
            _lastError = reason;
        }
        PersistRecoveryMarker();
        Publish();
    }

    private bool HasPendingRecovery()
    {
        lock (_gate) return _recoveryPending.Count > 0;
    }

    private void AttemptPendingRecovery()
    {
        FanTopology topology = BuildTopology(forceConflictScan: false);
        if (topology.ExternalSoftware.Any(x => x.BlocksControl))
        {
            lock (_gate) _lastError = "recovery_deferred_external_controller";
            Publish();
            return;
        }

        List<FanControlRecoveryEntry> pending;
        lock (_gate) pending = _recoveryPending.ToList();
        bool changed = false;
        foreach (FanControlRecoveryEntry entry in pending)
        {
            FanDevice? fan = topology.Devices.FirstOrDefault(x =>
                x.Id == entry.FanId || string.Equals(x.ControlIdentifier, entry.ControlIdentifier, StringComparison.OrdinalIgnoreCase));
            if (fan == null) continue;
            FanBackendWriteResult restored = _backend.RestoreDefault(fan);
            if (!restored.Success) continue;
            lock (_gate) _recoveryPending.RemoveAll(x => x.ControlIdentifier == entry.ControlIdentifier);
            changed = true;
        }

        if (changed) PersistRecoveryMarker();
        lock (_gate)
        {
            if (_recoveryPending.Count == 0) _lastError = null;
            else _lastError = "recovering_previous_session";
        }
        if (changed) Publish();
    }

    private void QueueRecovery(FanDevice fan)
    {
        if (string.IsNullOrWhiteSpace(fan.ControlIdentifier)) return;
        lock (_gate)
        {
            if (_recoveryPending.Any(x => string.Equals(x.ControlIdentifier, fan.ControlIdentifier, StringComparison.OrdinalIgnoreCase))) return;
            _recoveryPending.Add(new FanControlRecoveryEntry
            {
                FanId = fan.Id,
                ControlIdentifier = fan.ControlIdentifier,
                Backend = fan.Capabilities.Backend,
                DisplayName = fan.DisplayName,
            });
        }
    }

    private void PersistRecoveryMarker()
    {
        List<FanControlRecoveryEntry> entries;
        lock (_gate)
        {
            entries = _recoveryPending.Concat(_sessions.Values
                .Where(x => !string.IsNullOrWhiteSpace(x.Fan.ControlIdentifier))
                .Select(x => new FanControlRecoveryEntry
                {
                    FanId = x.Fan.Id,
                    ControlIdentifier = x.Fan.ControlIdentifier!,
                    Backend = x.Fan.Capabilities.Backend,
                    DisplayName = x.Fan.DisplayName,
                })).ToList();
        }
        _recoveryStore.Save(entries);
    }

    private FanTopology BuildTopology(bool forceConflictScan) => _topologyProvider(forceConflictScan);

    internal void RunWatchdogOnceForTests() => Tick();

    private static FanConfiguration NormalizeConfiguration(FanDevice fan, FanConfiguration source)
    {
        FanConfiguration clone = CloneConfiguration(source);
        if (clone.Mode == FanMode.Manual && string.IsNullOrWhiteSpace(clone.SensorId))
            clone.SensorId = fan.AvailableTemperatureSensors.FirstOrDefault()?.Id;
        return clone;
    }

    private static double? ResolveReferenceTemperature(FanDevice fan, FanConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.SensorId))
            return fan.AvailableTemperatureSensors.FirstOrDefault(x =>
                string.Equals(x.Id, configuration.SensorId, StringComparison.Ordinal))?.Value;
        return fan.Telemetry.ReferenceTemperature ?? fan.AvailableTemperatureSensors.FirstOrDefault()?.Value;
    }

    private static double? RequestedControl(FanConfiguration configuration, double? temperature) => configuration.Mode switch
    {
        FanMode.Manual => configuration.FixedControlPercent,
        FanMode.Curve when temperature.HasValue => FanCurveEngine.Interpolate(configuration.Curve, temperature.Value),
        _ => null,
    };

    internal static FanConfiguration CloneConfiguration(FanConfiguration configuration) => new()
    {
        Mode = configuration.Mode,
        SensorId = configuration.SensorId,
        SensorHint = configuration.SensorHint == null ? null : new FanTemperatureSensorHint
        {
            Hardware = configuration.SensorHint.Hardware,
            Category = configuration.SensorHint.Category,
            Name = configuration.SensorHint.Name,
        },
        FixedControlPercent = configuration.FixedControlPercent,
        Curve = configuration.Curve?.Select(point => new FanCurvePoint
        {
            Temperature = point.Temperature,
            ControlPercent = point.ControlPercent,
        }).ToList() ?? new List<FanCurvePoint>(),
    };

    private static ActiveSession CloneSession(ActiveSession session) => new()
    {
        Fan = session.Fan,
        Configuration = CloneConfiguration(session.Configuration),
        LastAppliedControlPercent = session.LastAppliedControlPercent,
        LastWriteUtc = session.LastWriteUtc,
        Status = session.Status,
    };

    private bool HasActiveSession(string fanId)
    {
        lock (_gate) return _sessions.ContainsKey(fanId);
    }

    private FanControlRuntimeState SnapshotLocked() => new()
    {
        UpdatedAtUtc = DateTime.UtcNow,
        LastError = _lastError,
        Sessions = _sessions.Values.Select(session => new FanControlSessionSnapshot
        {
            FanId = session.Fan.Id,
            Mode = session.Configuration.Mode,
            SensorId = session.Configuration.SensorId,
            Configuration = CloneConfiguration(session.Configuration),
            LastAppliedControlPercent = session.LastAppliedControlPercent,
            LastUpdatedUtc = session.LastWriteUtc,
            Status = session.Status,
        }).ToList(),
    };

    private void Publish()
    {
        FanControlRuntimeState state;
        lock (_gate) state = SnapshotLocked();
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { Logger.Warn("Fan control state listener failed: " + ex.Message); }
    }

    private static FanConfigurationPreview InvalidPreview(string fanId, string code, string message) => new()
    {
        Valid = false,
        FanId = fanId,
        Errors = new List<string> { $"{code}: {message}" },
    };

    private static FanApplyResult Fail(string fanId, FanMode mode, string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
        FanId = fanId,
        Mode = mode,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        RestoreAll("service_shutdown");
    }

    private sealed class ActiveSession
    {
        public FanDevice Fan { get; set; } = new();
        public FanConfiguration Configuration { get; set; } = new();
        public double? LastAppliedControlPercent { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public string Status { get; set; } = "active";
    }
}
