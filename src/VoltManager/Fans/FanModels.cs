namespace VoltManager.Fans;

public enum FanRole
{
    Unknown,
    CpuFan,
    CpuOptional,
    GpuFan,
    CaseFan,
    Pump,
    ExternalControllerFan,
}

public enum FanDetectionConfidence
{
    Low,
    Medium,
    High,
    Confirmed,
    UserAssigned,
}

public enum FanControlState
{
    MonitorOnly,
    ControlAvailable,
    ExternalControllerDetected,
    Unsupported,
    PermissionDenied,
    DeviceBusy,
    Disconnected,
    SensorUnavailable,
    SafetyBlocked,
}

public enum FanMode
{
    Automatic,
    Manual,
    Curve,
}

public enum FanProfileMatchStatus
{
    Matched,
    NeedsMapping,
    Missing,
    Incompatible,
}

public enum FanConflictConfidence
{
    None,
    Possible,
    High,
    Confirmed,
}

public sealed record FanCapabilities
{
    public static FanCapabilities MonitorOnly => new()
    {
        RpmReadable = true,
        ControlWritable = false,
        FixedControlSupported = false,
        SoftwareCurveSupported = false,
        FanStopSupported = false,
        CanRestoreDefault = false,
        Backend = "monitoring",
    };

    public bool RpmReadable { get; init; }
    public bool ControlReadable { get; init; }
    public bool ControlWritable { get; init; }
    public bool FixedControlSupported { get; init; }
    public bool SoftwareCurveSupported { get; init; }
    public bool FanStopSupported { get; init; }
    public bool CanRestoreDefault { get; init; }
    public double? MinimumControl { get; init; }
    public double? MaximumControl { get; init; }
    public string Backend { get; init; } = "unknown";
}

public sealed record FanTelemetry
{
    public double? Rpm { get; init; }
    public double? ControlPercent { get; init; }
    public double? ReferenceTemperature { get; init; }
    public DateTime LastUpdatedUtc { get; init; }
    public bool IsStale { get; init; }
}

public sealed record FanTemperatureSensor
{
    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public double? Value { get; init; }
}

public sealed record FanDevice
{
    public string Id { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string ControllerId { get; init; } = "";
    public string HardwareName { get; init; } = "";
    public string SensorName { get; init; } = "";
    public string? HeaderName { get; init; }
    public string DisplayName { get; init; } = "";
    public string? UserName { get; init; }
    public int? ChannelIndex { get; init; }
    public FanRole Role { get; init; }
    public FanDetectionConfidence RoleConfidence { get; init; }
    public string RoleEvidence { get; init; } = "";
    public FanControlState ControlState { get; init; }
    public FanCapabilities Capabilities { get; init; } = FanCapabilities.MonitorOnly;
    public FanTelemetry Telemetry { get; init; } = new();
    public List<FanTemperatureSensor> AvailableTemperatureSensors { get; init; } = new();
}

public sealed record FanExternalSoftwareNotice
{
    public string SoftwareName { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public FanConflictConfidence Confidence { get; init; }
    public string Evidence { get; init; } = "";
    public bool BlocksControl { get; init; }
}

public sealed record FanTopology
{
    public string Revision { get; init; } = "none";
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public bool SensorsAvailable { get; init; }
    public bool AnyControlAvailable => Devices.Any(x => x.Capabilities.ControlWritable);
    public List<FanDevice> Devices { get; init; } = new();
    public List<FanExternalSoftwareNotice> ExternalSoftware { get; init; } = new();
}

public sealed class FanCurvePoint
{
    public double Temperature { get; set; }
    public double ControlPercent { get; set; }
}

public sealed class FanConfiguration
{
    public FanMode Mode { get; set; } = FanMode.Automatic;
    public string? SensorId { get; set; }
    public double? FixedControlPercent { get; set; }
    public List<FanCurvePoint> Curve { get; set; } = new();
}

public sealed class FanMatchHints
{
    public string? HardwareId { get; set; }
    public string? ControllerId { get; set; }
    public FanRole Role { get; set; } = FanRole.Unknown;
    public string? HeaderName { get; set; }
    public string? HardwareName { get; set; }
    public string? SensorName { get; set; }
}

public sealed class FanProfileFan
{
    public string ProfileFanId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? UserName { get; set; }
    public FanMatchHints MatchHints { get; set; } = new();
    public FanConfiguration? Configuration { get; set; }
}

public sealed class FanProfileGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Fan group";
    public List<string> FanProfileIds { get; set; } = new();
}

public sealed class FanProfile
{
    public string Format { get; set; } = FanProfileValidator.Format;
    public int SchemaVersion { get; set; } = FanProfileValidator.SchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Fan profile";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
    public string? SourceHardwareSignature { get; set; }
    public List<FanProfileFan> Fans { get; set; } = new();
    public List<FanProfileGroup> Groups { get; set; } = new();
    public Dictionary<string, string> UiPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record FanProfileCompatibilityItem
{
    public string ProfileFanId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public FanProfileMatchStatus Status { get; init; }
    public string? MatchedFanId { get; init; }
    public List<string> CandidateFanIds { get; init; } = new();
    public string Reason { get; init; } = "";
}

public sealed record FanProfileCompatibilityReport
{
    public string ProfileId { get; init; } = "";
    public string TopologyRevision { get; init; } = "";
    public bool CanStore { get; init; }
    public bool CanApplyControl { get; init; }
    public List<FanProfileCompatibilityItem> Items { get; init; } = new();
}

public sealed record FanProfileSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTime ModifiedAtUtc { get; init; }
    public int FanCount { get; init; }
}

public sealed record FanProfileValidationResult(bool Valid, IReadOnlyList<string> Errors);

public static class FanProfileValidator
{
    public const string Format = "voltmanager.fan-profile";
    public const int SchemaVersion = 1;
    private const int MaxFans = 128;
    private const int MaxGroups = 64;
    private const int MaxCurvePoints = 32;

    public static FanProfileValidationResult Validate(FanProfile? profile)
    {
        var errors = new List<string>();
        if (profile == null)
            return new FanProfileValidationResult(false, new[] { "Profile payload is missing." });

        profile.Fans ??= new List<FanProfileFan>();
        profile.Groups ??= new List<FanProfileGroup>();
        profile.UiPreferences ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.Equals(profile.Format, Format, StringComparison.Ordinal))
            errors.Add("Unsupported fan profile format.");
        if (profile.SchemaVersion != SchemaVersion)
            errors.Add("Unsupported fan profile schema version.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().Length > 80)
            errors.Add("Profile name must contain 1-80 characters.");
        if (profile.Fans.Count > MaxFans)
            errors.Add($"Fan profile exceeds the {MaxFans} fan limit.");
        if (profile.Groups.Count > MaxGroups)
            errors.Add($"Fan profile exceeds the {MaxGroups} group limit.");
        if (profile.UiPreferences.Count > 64)
            errors.Add("Fan profile contains too many UI preferences.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fan in profile.Fans)
        {
            if (fan == null)
            {
                errors.Add("Fan profile contains a null fan entry.");
                continue;
            }
            fan.MatchHints ??= new FanMatchHints();
            if (fan.Configuration != null) fan.Configuration.Curve ??= new List<FanCurvePoint>();
            if (string.IsNullOrWhiteSpace(fan.ProfileFanId) || !ids.Add(fan.ProfileFanId))
                errors.Add("Profile fan identifiers must be non-empty and unique.");
            if (fan.DisplayName.Length > 120 || (fan.UserName?.Length ?? 0) > 120)
                errors.Add($"Fan '{fan.ProfileFanId}' contains an overlong display name.");

            ValidateConfiguration(fan.ProfileFanId, fan.Configuration, errors);
        }

        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in profile.Groups)
        {
            if (group == null)
            {
                errors.Add("Fan profile contains a null group entry.");
                continue;
            }
            group.FanProfileIds ??= new List<string>();
            if (string.IsNullOrWhiteSpace(group.Id) || !groupIds.Add(group.Id))
                errors.Add("Fan group identifiers must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 120)
                errors.Add("Fan group names must contain 1-120 characters.");
            if (group.FanProfileIds.Count > MaxFans || group.FanProfileIds.Any(id => !ids.Contains(id)))
                errors.Add($"Fan group '{group.Id}' references an unknown profile fan.");
        }

        return new FanProfileValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateConfiguration(string fanId, FanConfiguration? configuration, List<string> errors)
    {
        if (configuration == null) return;

        if (configuration.Mode == FanMode.Manual)
        {
            if (configuration.FixedControlPercent is not { } fixedControl ||
                !double.IsFinite(fixedControl) || fixedControl < 0 || fixedControl > 100)
                errors.Add($"Fan '{fanId}' has an invalid manual control percentage.");
        }

        if (configuration.Mode != FanMode.Curve) return;
        if (configuration.Curve.Count is < 2 or > MaxCurvePoints)
        {
            errors.Add($"Fan '{fanId}' curve must contain 2-{MaxCurvePoints} points.");
            return;
        }

        double previousTemperature = double.NegativeInfinity;
        double previousControl = double.NegativeInfinity;
        foreach (var point in configuration.Curve)
        {
            if (point == null)
            {
                errors.Add($"Fan '{fanId}' curve contains a null point.");
                continue;
            }
            if (!double.IsFinite(point.Temperature) || point.Temperature < -20 || point.Temperature > 150 ||
                !double.IsFinite(point.ControlPercent) || point.ControlPercent < 0 || point.ControlPercent > 100)
            {
                errors.Add($"Fan '{fanId}' curve contains an out-of-range value.");
                continue;
            }

            if (point.Temperature <= previousTemperature)
                errors.Add($"Fan '{fanId}' curve temperatures must be strictly increasing.");
            if (point.ControlPercent < previousControl)
                errors.Add($"Fan '{fanId}' curve must be monotonic and cannot reduce cooling as temperature rises.");

            previousTemperature = point.Temperature;
            previousControl = point.ControlPercent;
        }
    }
}
