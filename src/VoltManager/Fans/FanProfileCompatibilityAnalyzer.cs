namespace VoltManager.Fans;

/// <summary>
/// Produces a dry-run mapping report. Ambiguous role-only matches are never guessed:
/// the UI must ask the user to map them explicitly before control can ever be applied.
/// </summary>
public sealed class FanProfileCompatibilityAnalyzer
{
    public FanProfileCompatibilityReport Analyze(FanProfile profile, FanTopology topology)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(topology);

        var validation = FanProfileValidator.Validate(profile);
        var items = profile.Fans.Select(profileFan => Match(profileFan, topology.Devices)).ToList();
        bool allMapped = validation.Valid && items.All(x => x.Status == FanProfileMatchStatus.Matched);
        bool hasControlConfiguration = profile.Fans.Any(x => x.Configuration is { Mode: not FanMode.Automatic });
        bool configuredFansWritable = profile.Fans
            .Where(x => x.Configuration is { Mode: not FanMode.Automatic })
            .All(profileFan =>
            {
                var mapping = items.First(x => x.ProfileFanId == profileFan.ProfileFanId);
                if (mapping.MatchedFanId == null) return false;
                return topology.Devices.First(x => x.Id == mapping.MatchedFanId).Capabilities.ControlWritable;
            });

        return new FanProfileCompatibilityReport
        {
            ProfileId = profile.Id,
            TopologyRevision = topology.Revision,
            CanStore = validation.Valid,
            CanApplyControl = allMapped && hasControlConfiguration && configuredFansWritable,
            Items = items,
        };
    }

    private static FanProfileCompatibilityItem Match(FanProfileFan profileFan, IReadOnlyList<FanDevice> devices)
    {
        var hints = profileFan.MatchHints ?? new FanMatchHints();

        var exactHardware = Unique(devices.Where(x =>
            !string.IsNullOrWhiteSpace(hints.HardwareId) &&
            string.Equals(x.HardwareId, hints.HardwareId, StringComparison.OrdinalIgnoreCase)));
        if (exactHardware != null)
            return Matched(profileFan, exactHardware, "Exact hardware identifier match.");

        var exactHeaderCandidates = devices.Where(x =>
            !string.IsNullOrWhiteSpace(hints.HeaderName) &&
            !string.IsNullOrWhiteSpace(x.HeaderName) &&
            string.Equals(x.HeaderName, hints.HeaderName, StringComparison.OrdinalIgnoreCase));
        if (hints.Role != FanRole.Unknown)
            exactHeaderCandidates = exactHeaderCandidates.Where(x => x.Role == hints.Role);
        var exactHeader = Unique(exactHeaderCandidates);
        if (exactHeader != null)
            return Matched(profileFan, exactHeader, "Header label match.");

        var sensorCandidates = devices.Where(x =>
            !string.IsNullOrWhiteSpace(hints.HardwareName) &&
            !string.IsNullOrWhiteSpace(hints.SensorName) &&
            string.Equals(x.HardwareName, hints.HardwareName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.SensorName, hints.SensorName, StringComparison.OrdinalIgnoreCase));
        var exactSensor = Unique(sensorCandidates);
        if (exactSensor != null)
            return Matched(profileFan, exactSensor, "Hardware and sensor name match.");

        if (hints.Role != FanRole.Unknown)
        {
            var roleCandidates = devices.Where(x => x.Role == hints.Role).ToList();
            if (roleCandidates.Count == 1)
                return Matched(profileFan, roleCandidates[0], "Unique fan role match.");
            if (roleCandidates.Count > 1)
            {
                return new FanProfileCompatibilityItem
                {
                    ProfileFanId = profileFan.ProfileFanId,
                    DisplayName = profileFan.DisplayName,
                    Status = FanProfileMatchStatus.NeedsMapping,
                    CandidateFanIds = roleCandidates.Select(x => x.Id).ToList(),
                    Reason = "Multiple local fans match the profile role; manual mapping is required.",
                };
            }
        }

        return new FanProfileCompatibilityItem
        {
            ProfileFanId = profileFan.ProfileFanId,
            DisplayName = profileFan.DisplayName,
            Status = FanProfileMatchStatus.Missing,
            Reason = "No compatible local fan could be identified without guessing.",
        };
    }

    private static FanDevice? Unique(IEnumerable<FanDevice> candidates)
    {
        FanDevice? result = null;
        int count = 0;
        foreach (var candidate in candidates)
        {
            result = candidate;
            count++;
            if (count > 1) return null;
        }
        return count == 1 ? result : null;
    }

    private static FanProfileCompatibilityItem Matched(FanProfileFan profileFan, FanDevice device, string reason) => new()
    {
        ProfileFanId = profileFan.ProfileFanId,
        DisplayName = profileFan.DisplayName,
        Status = FanProfileMatchStatus.Matched,
        MatchedFanId = device.Id,
        CandidateFanIds = new List<string> { device.Id },
        Reason = reason,
    };
}
