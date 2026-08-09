using VoltManager.Services;

namespace VoltManager.Fans;

/// <summary>
/// Application-level facade for the Cooling UI. The current implementation is intentionally
/// read-only at the hardware boundary; profiles and aliases are safe application data only.
/// </summary>
public sealed class FanManagementService
{
    private readonly MonitorService _monitor;
    private readonly FanDiscoveryService _discovery;
    private readonly FanAliasStore _aliases;
    private readonly FanProfileStore _profiles;
    private readonly FanExternalConflictDetector _conflicts;

    public FanManagementService(
        MonitorService monitor,
        FanDiscoveryService? discovery = null,
        FanAliasStore? aliases = null,
        FanProfileStore? profiles = null,
        FanExternalConflictDetector? conflicts = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _discovery = discovery ?? new FanDiscoveryService();
        _aliases = aliases ?? new FanAliasStore();
        _profiles = profiles ?? new FanProfileStore();
        _conflicts = conflicts ?? new FanExternalConflictDetector();
    }

    public FanTopology GetTopology()
    {
        var aliases = _aliases.GetAll();
        var conflicts = _conflicts.Scan();
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

    public IReadOnlyList<FanProfileSummary> ListProfiles() => _profiles.List();

    public FanProfile GetProfile(string id) => _profiles.Get(id);

    public FanProfileSummary SaveCurrentProfile(string name)
    {
        string normalizedName = (name ?? "").Trim();
        if (normalizedName.Length is < 1 or > 80)
            throw new ArgumentException("Profile name must contain 1-80 characters.", nameof(name));

        FanTopology topology = GetTopology();
        var profile = new FanProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = normalizedName,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow,
            SourceHardwareSignature = topology.Revision,
            Fans = topology.Devices.Select(ToProfileFan).ToList(),
            UiPreferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
            Groups = source.Groups.Select(group => new FanProfileGroup
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = group.Name,
                FanProfileIds = group.FanProfileIds.ToList(),
            }).ToList(),
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

    private static FanProfileFan ToProfileFan(FanDevice fan) => new()
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
        // Monitoring-only phase: no control configuration is invented or persisted.
        Configuration = null,
    };

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
        Configuration = fan.Configuration == null ? null : new FanConfiguration
        {
            Mode = fan.Configuration.Mode,
            SensorId = fan.Configuration.SensorId,
            FixedControlPercent = fan.Configuration.FixedControlPercent,
            Curve = fan.Configuration.Curve.Select(point => new FanCurvePoint
            {
                Temperature = point.Temperature,
                ControlPercent = point.ControlPercent,
            }).ToList(),
        },
    };
}
