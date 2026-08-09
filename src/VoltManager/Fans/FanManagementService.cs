using VoltManager.Services;

namespace VoltManager.Fans;

/// <summary>
/// Application facade for the complete Cooling feature: topology, supervised
/// control, aliases, presets and versioned profiles. Hardware writes are delegated
/// exclusively to FanControlService and therefore always cross FanSafetyPolicy.
/// </summary>
public sealed class FanManagementService : IDisposable
{
    private readonly MonitorService _monitor;
    private readonly FanDiscoveryService _discovery;
    private readonly FanAliasStore _aliases;
    private readonly FanProfileStore _profiles;
    private readonly FanExternalConflictDetector _conflicts;
    private readonly FanControlService _control;

    public event Action<FanControlRuntimeState>? ControlStateChanged
    {
        add => _control.StateChanged += value;
        remove => _control.StateChanged -= value;
    }

    public FanControlRuntimeState ControlState => _control.Current;
    public FanSafetyPolicyInfo SafetyPolicyInfo => new()
    {
        RampStartTemperature = FanSafetyPolicy.RampStartTemperature,
        StrongRampTemperature = FanSafetyPolicy.StrongRampTemperature,
        EmergencyTemperature = FanSafetyPolicy.EmergencyTemperature,
    };

    public FanManagementService(
        MonitorService monitor,
        IHardwareAccess hardwareAccess,
        FanDiscoveryService? discovery = null,
        FanAliasStore? aliases = null,
        FanProfileStore? profiles = null,
        FanExternalConflictDetector? conflicts = null,
        FanSafetyPolicy? safety = null,
        IFanBackend? backend = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        ArgumentNullException.ThrowIfNull(hardwareAccess);
        _discovery = discovery ?? new FanDiscoveryService(() => hardwareAccess.ControlWritesAllowed);
        _aliases = aliases ?? new FanAliasStore();
        _profiles = profiles ?? new FanProfileStore();
        _conflicts = conflicts ?? new FanExternalConflictDetector();
        _control = new FanControlService(
            _monitor,
            _discovery,
            _aliases,
            _conflicts,
            backend ?? new LibreHardwareMonitorFanBackend(hardwareAccess),
            safety);
    }

    public FanTopology GetTopology(bool forceConflictScan = false)
    {
        var aliases = _aliases.GetAll();
        var conflicts = _conflicts.Scan(forceConflictScan);
        return _discovery.BuildTopology(_monitor.Latest, aliases, conflicts);
    }

    public FanTopology RenameFan(string fanId, string? alias)
    {
        var topology = GetTopology();
        if (!topology.Devices.Any(x => string.Equals(x.Id, fanId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Fan is no longer present in the current hardware topology.");

        _aliases.Set(fanId, alias);
        return GetTopology();
    }

    public FanConfigurationPreview PreviewConfiguration(string fanId, FanConfiguration configuration) =>
        _control.Preview(fanId, configuration);

    public FanApplyResult ApplyConfiguration(string topologyRevision, string fanId, FanConfiguration configuration) =>
        _control.Apply(topologyRevision, fanId, configuration);

    public FanApplyResult RestoreDefault(string fanId) => _control.Restore(fanId);

    public FanProfileApplyResult ApplyGroupConfiguration(
        string topologyRevision,
        IReadOnlyList<string> fanIds,
        FanConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(fanIds);
        ArgumentNullException.ThrowIfNull(configuration);
        FanTopology topology = GetTopology(forceConflictScan: true);
        if (!string.Equals(topologyRevision, topology.Revision, StringComparison.Ordinal))
            return ProfileFail("topology_changed", "Hardware topology changed. Refresh before applying the group configuration.");

        var uniqueIds = fanIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
        if (uniqueIds.Count == 0) return ProfileFail("empty_group", "The fan group is empty.");

        FanDevice? sourceFan = topology.Devices.FirstOrDefault(x => x.Id == uniqueIds[0]);
        FanTemperatureSensor? sourceSensor = sourceFan?.AvailableTemperatureSensors.FirstOrDefault(x => x.Id == configuration.SensorId);
        var prepared = new List<(FanDevice Fan, FanConfiguration Configuration)>();
        foreach (string fanId in uniqueIds)
        {
            FanDevice? fan = topology.Devices.FirstOrDefault(x => x.Id == fanId);
            if (fan == null) return ProfileFail("device_missing", $"Fan '{fanId}' is no longer present.");

            FanConfiguration local = FanControlService.CloneConfiguration(configuration);
            if (local.Mode == FanMode.Curve && !fan.AvailableTemperatureSensors.Any(x => x.Id == local.SensorId))
            {
                local.SensorId = sourceSensor == null ? null : fan.AvailableTemperatureSensors.FirstOrDefault(x =>
                    string.Equals(x.Category, sourceSensor.Category, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Name, sourceSensor.Name, StringComparison.OrdinalIgnoreCase))?.Id;
            }

            FanConfigurationPreview preview = _control.Preview(fan.Id, local);
            if (!preview.Valid)
                return ProfileFail("group_preflight_failed", $"'{fan.DisplayName}': {string.Join("; ", preview.Errors)}");
            prepared.Add((fan, local));
        }

        var applied = new List<string>();
        var results = new List<FanApplyResult>();
        foreach ((FanDevice fan, FanConfiguration local) in prepared)
        {
            FanApplyResult result = _control.Apply(topology.Revision, fan.Id, local);
            results.Add(result);
            if (!result.Success)
            {
                foreach (string appliedId in applied) _control.Restore(appliedId);
                return new FanProfileApplyResult
                {
                    Success = false,
                    Code = "group_apply_failed",
                    Message = result.Message,
                    FanResults = results,
                };
            }
            applied.Add(fan.Id);
        }

        return new FanProfileApplyResult
        {
            Success = true,
            Code = "ok",
            Message = "Group configuration applied.",
            FanResults = results,
        };
    }

    public IReadOnlyDictionary<string, List<FanCurvePoint>> GetPresets(string fanId) => _control.GetPresets(fanId);

    public IReadOnlyList<FanProfileSummary> ListProfiles() => _profiles.List();

    public FanProfile GetProfile(string id) => _profiles.Get(id);

    public FanProfileSummary SaveCurrentProfile(string name) => SaveProfile(new FanProfileSaveRequest { Name = name });

    public FanProfileSummary SaveProfile(FanProfileSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string normalizedName = (request.Name ?? "").Trim();
        if (normalizedName.Length is < 1 or > 80)
            throw new ArgumentException("Profile name must contain 1-80 characters.", nameof(request));

        FanTopology topology = GetTopology();
        request.Configurations ??= new Dictionary<string, FanConfiguration>(StringComparer.Ordinal);
        request.Groups ??= new List<FanProfileGroup>();
        request.UiPreferences ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FanProfile? existing = string.IsNullOrWhiteSpace(request.ProfileId) ? null : _profiles.Get(request.ProfileId);

        var profile = new FanProfile
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = normalizedName,
            CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow,
            SourceHardwareSignature = topology.Revision,
            Fans = topology.Devices.Select(fan => ToProfileFan(
                fan,
                request.Configurations.TryGetValue(fan.Id, out FanConfiguration? requested)
                    ? requested
                    : _control.GetActiveConfiguration(fan.Id))).ToList(),
            Groups = CloneGroups(request.Groups),
            UiPreferences = new Dictionary<string, string>(request.UiPreferences, StringComparer.OrdinalIgnoreCase)
            {
                ["sourceTopologyRevision"] = topology.Revision,
            },
        };
        return _profiles.Save(profile);
    }

    public FanProfileSummary RenameProfile(string id, string name)
    {
        string normalizedName = (name ?? "").Trim();
        if (normalizedName.Length is < 1 or > 80)
            throw new ArgumentException("Profile name must contain 1-80 characters.", nameof(name));
        var profile = _profiles.Get(id);
        profile.Name = normalizedName;
        return _profiles.Save(profile);
    }

    public FanProfileSummary DuplicateProfile(string id, string? name = null)
    {
        FanProfile source = _profiles.Get(id);
        var clone = new FanProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? source.Name + " copy" : name.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow,
            SourceHardwareSignature = source.SourceHardwareSignature,
            Fans = source.Fans.Select(CloneFan).ToList(),
            Groups = CloneGroups(source.Groups),
            UiPreferences = new Dictionary<string, string>(source.UiPreferences, StringComparer.OrdinalIgnoreCase),
        };
        return _profiles.Save(clone);
    }

    public bool DeleteProfile(string id) => _profiles.Delete(id);

    public FanProfileCompatibilityReport AnalyzeProfile(string id) =>
        _profiles.Analyze(id, GetTopology());

    public FanProfileImportResult ImportProfile(string path) =>
        _profiles.Import(path, GetTopology());

    public string ExportProfile(string id, string path) => _profiles.Export(id, path);

    public FanProfileApplyResult ApplyProfile(string profileId, IReadOnlyList<FanProfileApplyMapping>? manualMappings)
    {
        FanProfile profile = _profiles.Get(profileId);
        FanTopology topology = GetTopology(forceConflictScan: true);
        FanProfileCompatibilityReport compatibility = new FanProfileCompatibilityAnalyzer().Analyze(profile, topology);
        var explicitMappings = (manualMappings ?? Array.Empty<FanProfileApplyMapping>())
            .Where(x => !string.IsNullOrWhiteSpace(x.ProfileFanId) && !string.IsNullOrWhiteSpace(x.LocalFanId))
            .GroupBy(x => x.ProfileFanId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);

        var prepared = new List<(FanDevice Fan, FanConfiguration Configuration)>();
        var aliasesToApply = new List<(string FanId, string? Alias)>();
        foreach (FanProfileFan profileFan in profile.Fans)
        {
            FanProfileApplyMapping? explicitMapping = explicitMappings.GetValueOrDefault(profileFan.ProfileFanId);
            string? localFanId = explicitMapping?.LocalFanId
                ?? compatibility.Items.FirstOrDefault(x => x.ProfileFanId == profileFan.ProfileFanId)?.MatchedFanId;

            if (!string.IsNullOrWhiteSpace(localFanId))
            {
                FanDevice? mappedFan = topology.Devices.FirstOrDefault(x => x.Id == localFanId);
                if (mappedFan != null)
                    aliasesToApply.Add((mappedFan.Id, profileFan.UserName));
            }

            if (profileFan.Configuration == null) continue;
            if (string.IsNullOrWhiteSpace(localFanId))
                return ProfileFail("mapping_required", $"'{profileFan.DisplayName}' requires a local fan mapping before the profile can be applied.");

            FanDevice? fan = topology.Devices.FirstOrDefault(x => x.Id == localFanId);
            if (fan == null)
                return ProfileFail("mapped_device_missing", $"The mapped local fan for '{profileFan.DisplayName}' is not present.");

            FanConfiguration config = FanControlService.CloneConfiguration(profileFan.Configuration);
            if (config.Mode == FanMode.Curve)
            {
                config.SensorId = ResolveProfileSensor(fan, config, explicitMapping?.LocalSensorId);
                if (string.IsNullOrWhiteSpace(config.SensorId))
                    return ProfileFail("sensor_mapping_required", $"'{profileFan.DisplayName}' requires a compatible temperature sensor mapping.");
            }

            FanConfigurationPreview preview = _control.Preview(fan.Id, config);
            if (!preview.Valid)
                return ProfileFail("profile_preflight_failed", $"'{profileFan.DisplayName}': {string.Join("; ", preview.Errors)}");
            prepared.Add((fan, config));
        }

        var appliedFanIds = new List<string>();
        var results = new List<FanApplyResult>();
        foreach ((FanDevice fan, FanConfiguration configuration) in prepared)
        {
            FanApplyResult result = _control.Apply(topology.Revision, fan.Id, configuration);
            results.Add(result);
            if (!result.Success)
            {
                foreach (string applied in appliedFanIds) _control.Restore(applied);
                return new FanProfileApplyResult
                {
                    Success = false,
                    Code = "profile_apply_failed",
                    Message = result.Message,
                    FanResults = results,
                };
            }
            appliedFanIds.Add(fan.Id);
        }

        foreach ((string fanId, string? alias) in aliasesToApply)
            _aliases.Set(fanId, alias);

        return new FanProfileApplyResult
        {
            Success = true,
            Code = "ok",
            Message = prepared.Count == 0 ? "Profile application data applied; no hardware control configuration was present." : "Profile applied.",
            FanResults = results,
        };
    }

    public void SuspendControl() => _control.SuspendAll("system_suspend");
    public void ResumeControl() => _control.Resume();

    private static string? ResolveProfileSensor(FanDevice fan, FanConfiguration config, string? explicitSensorId)
    {
        if (!string.IsNullOrWhiteSpace(explicitSensorId) &&
            fan.AvailableTemperatureSensors.Any(x => x.Id == explicitSensorId))
            return explicitSensorId;

        if (!string.IsNullOrWhiteSpace(config.SensorId) &&
            fan.AvailableTemperatureSensors.Any(x => x.Id == config.SensorId))
            return config.SensorId;

        if (config.SensorHint != null)
        {
            var exact = fan.AvailableTemperatureSensors.Where(sensor =>
                (string.IsNullOrWhiteSpace(config.SensorHint.Category) ||
                 string.Equals(sensor.Category, config.SensorHint.Category, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(config.SensorHint.Name) ||
                 string.Equals(sensor.Name, config.SensorHint.Name, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(config.SensorHint.Hardware) ||
                 string.Equals(sensor.Hardware, config.SensorHint.Hardware, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exact.Count == 1) return exact[0].Id;

            var category = fan.AvailableTemperatureSensors.Where(sensor =>
                !string.IsNullOrWhiteSpace(config.SensorHint.Category) &&
                string.Equals(sensor.Category, config.SensorHint.Category, StringComparison.OrdinalIgnoreCase)).ToList();
            if (category.Count == 1) return category[0].Id;
        }

        return null;
    }

    private static FanProfileFan ToProfileFan(FanDevice fan, FanConfiguration? configuration)
    {
        FanConfiguration? saved = configuration == null
            ? (fan.Capabilities.CanRestoreDefault ? new FanConfiguration { Mode = FanMode.Automatic } : null)
            : FanControlService.CloneConfiguration(configuration);

        if (saved != null && !string.IsNullOrWhiteSpace(saved.SensorId))
        {
            FanTemperatureSensor? sensor = fan.AvailableTemperatureSensors.FirstOrDefault(x => x.Id == saved.SensorId);
            if (sensor != null)
            {
                saved.SensorHint = new FanTemperatureSensorHint
                {
                    Hardware = sensor.Hardware,
                    Category = sensor.Category,
                    Name = sensor.Name,
                };
            }
        }

        return new FanProfileFan
        {
            ProfileFanId = fan.Id,
            DisplayName = fan.DisplayName,
            UserName = fan.UserName,
            MatchHints = new FanMatchHints
            {
                HardwareId = fan.HardwareId,
                ControllerId = fan.ControllerId,
                Role = fan.Role,
                HeaderName = fan.HeaderName,
                HardwareName = fan.HardwareName,
                SensorName = fan.SensorName,
            },
            Configuration = saved,
        };
    }

    private static FanProfileFan CloneFan(FanProfileFan fan) => new()
    {
        ProfileFanId = fan.ProfileFanId,
        DisplayName = fan.DisplayName,
        UserName = fan.UserName,
        MatchHints = new FanMatchHints
        {
            HardwareId = fan.MatchHints.HardwareId,
            ControllerId = fan.MatchHints.ControllerId,
            Role = fan.MatchHints.Role,
            HeaderName = fan.MatchHints.HeaderName,
            HardwareName = fan.MatchHints.HardwareName,
            SensorName = fan.MatchHints.SensorName,
        },
        Configuration = fan.Configuration == null ? null : FanControlService.CloneConfiguration(fan.Configuration),
    };

    private static List<FanProfileGroup> CloneGroups(IEnumerable<FanProfileGroup> groups) => groups.Select(group => new FanProfileGroup
    {
        Id = string.IsNullOrWhiteSpace(group.Id) ? Guid.NewGuid().ToString("N") : group.Id,
        Name = group.Name,
        FanProfileIds = group.FanProfileIds?.ToList() ?? new List<string>(),
    }).ToList();

    private static FanProfileApplyResult ProfileFail(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
    };

    public void Dispose() => _control.Dispose();
}
