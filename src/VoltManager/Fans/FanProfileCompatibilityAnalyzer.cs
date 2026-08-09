namespace VoltManager.Fans;

/// <summary>
/// Produces a dry-run fan/sensor mapping report. Ambiguous matches are never
/// guessed; they are returned as NeedsMapping with explicit candidate IDs.
/// </summary>
public sealed class FanProfileCompatibilityAnalyzer
{
    public FanProfileCompatibilityReport Analyze(FanProfile profile, FanTopology topology)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(topology);

        FanProfileValidationResult validation = FanProfileValidator.Validate(profile);
        var items = profile.Fans.Select(profileFan => Match(profileFan, topology.Devices)).ToList();
        bool allMapped = validation.Valid && items.All(x => x.Status == FanProfileMatchStatus.Matched);
        bool configurationsSupported = validation.Valid && profile.Fans.All(profileFan =>
        {
            if (profileFan.Configuration == null) return true;
            FanProfileCompatibilityItem item = items.First(x => x.ProfileFanId == profileFan.ProfileFanId);
            if (item.MatchedFanId == null) return false;
            FanDevice? fan = topology.Devices.FirstOrDefault(x => x.Id == item.MatchedFanId);
            if (fan == null) return false;
            return ConfigurationSupported(profileFan.Configuration, fan, item.MatchedSensorId);
        });

        bool hasControlIntent = profile.Fans.Any(x => x.Configuration != null);
        return new FanProfileCompatibilityReport
        {
            ProfileId = profile.Id,
            TopologyRevision = topology.Revision,
            CanStore = validation.Valid,
            CanApplyControl = allMapped && configurationsSupported && hasControlIntent,
            Items = items,
        };
    }

    private static FanProfileCompatibilityItem Match(FanProfileFan profileFan, IReadOnlyList<FanDevice> devices)
    {
        FanDevice? matchedFan;
        string reason;
        List<string> fanCandidates;
        (matchedFan, reason, fanCandidates) = MatchFan(profileFan, devices);

        if (matchedFan == null)
        {
            return new FanProfileCompatibilityItem
            {
                ProfileFanId = profileFan.ProfileFanId,
                DisplayName = profileFan.DisplayName,
                Status = fanCandidates.Count > 0 ? FanProfileMatchStatus.NeedsMapping : FanProfileMatchStatus.Missing,
                CandidateFanIds = fanCandidates,
                Reason = reason,
            };
        }

        if (profileFan.Configuration?.Mode == FanMode.Curve)
        {
            SensorMatch sensorMatch = MatchSensor(profileFan.Configuration, matchedFan);
            if (sensorMatch.MatchedSensorId == null)
            {
                return new FanProfileCompatibilityItem
                {
                    ProfileFanId = profileFan.ProfileFanId,
                    DisplayName = profileFan.DisplayName,
                    Status = sensorMatch.Candidates.Count > 0 ? FanProfileMatchStatus.NeedsMapping : FanProfileMatchStatus.Incompatible,
                    MatchedFanId = matchedFan.Id,
                    CandidateFanIds = new List<string> { matchedFan.Id },
                    CandidateSensorIds = sensorMatch.Candidates,
                    Reason = reason + " " + sensorMatch.Reason,
                };
            }

            if (!ConfigurationSupported(profileFan.Configuration, matchedFan, sensorMatch.MatchedSensorId))
            {
                return new FanProfileCompatibilityItem
                {
                    ProfileFanId = profileFan.ProfileFanId,
                    DisplayName = profileFan.DisplayName,
                    Status = FanProfileMatchStatus.Incompatible,
                    MatchedFanId = matchedFan.Id,
                    CandidateFanIds = new List<string> { matchedFan.Id },
                    MatchedSensorId = sensorMatch.MatchedSensorId,
                    CandidateSensorIds = new List<string> { sensorMatch.MatchedSensorId },
                    Reason = "The local fan/sensor mapping exists but the saved control configuration is outside current capabilities.",
                };
            }

            return Matched(profileFan, matchedFan, reason, sensorMatch.MatchedSensorId);
        }

        if (profileFan.Configuration != null && !ConfigurationSupported(profileFan.Configuration, matchedFan, null))
        {
            return new FanProfileCompatibilityItem
            {
                ProfileFanId = profileFan.ProfileFanId,
                DisplayName = profileFan.DisplayName,
                Status = FanProfileMatchStatus.Incompatible,
                MatchedFanId = matchedFan.Id,
                CandidateFanIds = new List<string> { matchedFan.Id },
                Reason = "The local fan matches, but its current control capabilities cannot safely apply the saved configuration.",
            };
        }

        return Matched(profileFan, matchedFan, reason, null);
    }

    private static (FanDevice? Fan, string Reason, List<string> Candidates) MatchFan(
        FanProfileFan profileFan,
        IReadOnlyList<FanDevice> devices)
    {
        FanMatchHints hints = profileFan.MatchHints ?? new FanMatchHints();

        FanDevice? exactHardware = Unique(devices.Where(x =>
            !string.IsNullOrWhiteSpace(hints.HardwareId) &&
            string.Equals(x.HardwareId, hints.HardwareId, StringComparison.OrdinalIgnoreCase)));
        if (exactHardware != null) return (exactHardware, "Exact hardware identifier match.", new() { exactHardware.Id });

        IEnumerable<FanDevice> headerCandidates = devices.Where(x =>
            !string.IsNullOrWhiteSpace(hints.HeaderName) &&
            !string.IsNullOrWhiteSpace(x.HeaderName) &&
            string.Equals(x.HeaderName, hints.HeaderName, StringComparison.OrdinalIgnoreCase));
        if (hints.Role != FanRole.Unknown) headerCandidates = headerCandidates.Where(x => x.Role == hints.Role);
        var headers = headerCandidates.ToList();
        if (headers.Count == 1) return (headers[0], "Header label match.", new() { headers[0].Id });

        var sensorCandidates = devices.Where(x =>
            !string.IsNullOrWhiteSpace(hints.HardwareName) &&
            !string.IsNullOrWhiteSpace(hints.SensorName) &&
            string.Equals(x.HardwareName, hints.HardwareName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.SensorName, hints.SensorName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sensorCandidates.Count == 1)
            return (sensorCandidates[0], "Hardware and sensor name match.", new() { sensorCandidates[0].Id });

        if (hints.Role != FanRole.Unknown)
        {
            var roleCandidates = devices.Where(x => x.Role == hints.Role).ToList();
            if (roleCandidates.Count == 1)
                return (roleCandidates[0], "Unique fan role match.", new() { roleCandidates[0].Id });
            if (roleCandidates.Count > 1)
                return (null, "Multiple local fans match the profile role; manual mapping is required.", roleCandidates.Select(x => x.Id).ToList());
        }

        return (null, "No compatible local fan could be identified without guessing.", new());
    }

    private static SensorMatch MatchSensor(FanConfiguration configuration, FanDevice fan)
    {
        if (!string.IsNullOrWhiteSpace(configuration.SensorId))
        {
            FanTemperatureSensor? exactId = fan.AvailableTemperatureSensors.FirstOrDefault(x => x.Id == configuration.SensorId);
            if (exactId != null) return new SensorMatch(exactId.Id, new() { exactId.Id }, "Exact temperature sensor identifier match.");
        }

        if (configuration.SensorHint != null)
        {
            var exact = fan.AvailableTemperatureSensors.Where(sensor =>
                (string.IsNullOrWhiteSpace(configuration.SensorHint.Hardware) ||
                 string.Equals(sensor.Hardware, configuration.SensorHint.Hardware, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(configuration.SensorHint.Category) ||
                 string.Equals(sensor.Category, configuration.SensorHint.Category, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(configuration.SensorHint.Name) ||
                 string.Equals(sensor.Name, configuration.SensorHint.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exact.Count == 1) return new SensorMatch(exact[0].Id, new() { exact[0].Id }, "Temperature sensor metadata match.");

            var category = fan.AvailableTemperatureSensors.Where(sensor =>
                !string.IsNullOrWhiteSpace(configuration.SensorHint.Category) &&
                string.Equals(sensor.Category, configuration.SensorHint.Category, StringComparison.OrdinalIgnoreCase)).ToList();
            if (category.Count == 1) return new SensorMatch(category[0].Id, new() { category[0].Id }, "Unique temperature sensor category match.");
            if (category.Count > 1) return new SensorMatch(null, category.Select(x => x.Id).ToList(), "Multiple temperature sensors match; manual sensor mapping is required.");
        }

        return fan.AvailableTemperatureSensors.Count switch
        {
            1 => new SensorMatch(fan.AvailableTemperatureSensors[0].Id, new() { fan.AvailableTemperatureSensors[0].Id }, "Only compatible temperature sensor available."),
            > 1 => new SensorMatch(null, fan.AvailableTemperatureSensors.Select(x => x.Id).ToList(), "Multiple temperature sensors are available; manual sensor mapping is required."),
            _ => new SensorMatch(null, new(), "No compatible temperature sensor is available."),
        };
    }

    private static bool ConfigurationSupported(FanConfiguration config, FanDevice fan, string? matchedSensorId)
    {
        if (config.Mode == FanMode.Automatic) return fan.Capabilities.CanRestoreDefault;
        if (fan.ControlState != FanControlState.ControlAvailable || !fan.Capabilities.ControlWritable) return false;
        if (fan.Capabilities.MinimumControl is not { } min || fan.Capabilities.MaximumControl is not { } max || max <= min) return false;

        if (config.Mode == FanMode.Manual)
            return config.FixedControlPercent is { } value && double.IsFinite(value) && value >= min && value <= max;

        if (config.Mode == FanMode.Curve)
        {
            if (string.IsNullOrWhiteSpace(matchedSensorId)) return false;
            return new FanSafetyPolicy().ValidateCurve(config.Curve, min, max).Allowed;
        }

        return false;
    }

    private static FanDevice? Unique(IEnumerable<FanDevice> candidates)
    {
        FanDevice? result = null;
        int count = 0;
        foreach (FanDevice candidate in candidates)
        {
            result = candidate;
            if (++count > 1) return null;
        }
        return count == 1 ? result : null;
    }

    private static FanProfileCompatibilityItem Matched(
        FanProfileFan profileFan,
        FanDevice device,
        string reason,
        string? sensorId) => new()
    {
        ProfileFanId = profileFan.ProfileFanId,
        DisplayName = profileFan.DisplayName,
        Status = FanProfileMatchStatus.Matched,
        MatchedFanId = device.Id,
        CandidateFanIds = new List<string> { device.Id },
        MatchedSensorId = sensorId,
        CandidateSensorIds = sensorId == null ? new List<string>() : new List<string> { sensorId },
        Reason = reason,
    };

    private sealed record SensorMatch(string? MatchedSensorId, List<string> Candidates, string Reason);
}
