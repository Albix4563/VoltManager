using System.Security.Cryptography;
using System.Text;
using VoltManager.Models;

namespace VoltManager.Fans;

/// <summary>
/// Converts the sensor telemetry already collected by MonitorService into a fan-oriented
/// topology. This service is deliberately read-only: seeing an RPM sensor does not imply
/// that the corresponding controller exposes a writable fan control.
/// </summary>
public sealed class FanDiscoveryService
{
    public FanTopology BuildTopology(
        MetricsSnapshot metrics,
        IReadOnlyDictionary<string, string>? aliases = null,
        IReadOnlyList<FanExternalSoftwareNotice>? externalSoftware = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var temperatureSensors = metrics.Sensors
            .Where(IsTemperature)
            .Select(ToTemperatureSensor)
            .ToList();

        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var devices = new List<FanDevice>();

        foreach (var sensor in metrics.Sensors.Where(IsFan))
        {
            string baseKey = !string.IsNullOrWhiteSpace(sensor.Identifier)
                ? "lhm|" + sensor.Identifier.Trim()
                : BuildBaseKey(sensor);
            int occurrence = occurrences.TryGetValue(baseKey, out int current) ? current : 0;
            occurrences[baseKey] = occurrence + 1;

            string hardwareId = occurrence == 0 ? baseKey : $"{baseKey}|{occurrence}";
            string id = "fan-" + ShortHash(hardwareId);
            string controllerId = "controller-" + ShortHash(Normalize(sensor.Hardware));
            var classification = Classify(sensor);
            string? headerName = DetectHeaderName(sensor.Name);
            string? alias = null;
            aliases?.TryGetValue(id, out alias);

            var availableTemperatures = SelectAvailableTemperatures(
                classification.Role,
                sensor.Hardware,
                temperatureSensors);
            FanCapabilities capabilities = BuildCapabilities(sensor);

            devices.Add(new FanDevice
            {
                Id = id,
                HardwareId = hardwareId,
                ControllerId = controllerId,
                HardwareName = sensor.Hardware,
                SensorName = sensor.Name,
                HeaderName = headerName,
                DisplayName = string.IsNullOrWhiteSpace(alias) ? BuildDisplayName(sensor, classification.Role) : alias.Trim(),
                UserName = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
                ChannelIndex = ParseTrailingIndex(sensor.Name),
                Role = classification.Role,
                RoleConfidence = classification.Confidence,
                RoleEvidence = classification.Evidence,
                ControlState = sensor.ControlAvailable ? FanControlState.SafetyBlocked : FanControlState.MonitorOnly,
                Capabilities = capabilities,
                Telemetry = new FanTelemetry
                {
                    Rpm = sensor.Value,
                    ControlPercent = sensor.ControlPercent,
                    ReferenceTemperature = SelectReferenceTemperature(classification.Role, availableTemperatures),
                    LastUpdatedUtc = DateTime.UtcNow,
                    IsStale = false,
                },
                AvailableTemperatureSensors = availableTemperatures,
            });
        }

        devices.Sort(CompareDevices);
        string revisionSeed = string.Join("|", devices.Select(x => x.Id));

        return new FanTopology
        {
            Revision = "fan-topology-" + ShortHash(revisionSeed),
            GeneratedAtUtc = DateTime.UtcNow,
            SensorsAvailable = metrics.SensorsAvailable,
            Devices = devices,
            ExternalSoftware = externalSoftware?.ToList() ?? new List<FanExternalSoftwareNotice>(),
        };
    }

    private static bool IsFan(SensorReading reading) =>
        string.Equals(reading.Type, "fan", StringComparison.OrdinalIgnoreCase);

    private static bool IsTemperature(SensorReading reading) =>
        string.Equals(reading.Type, "temp", StringComparison.OrdinalIgnoreCase);

    private static FanTemperatureSensor ToTemperatureSensor(SensorReading sensor)
    {
        string key = !string.IsNullOrWhiteSpace(sensor.Identifier)
            ? "lhm|" + sensor.Identifier.Trim()
            : $"{Normalize(sensor.Hardware)}|{Normalize(sensor.Category)}|{Normalize(sensor.Name)}";
        return new FanTemperatureSensor
        {
            Id = "temp-" + ShortHash(key),
            Hardware = sensor.Hardware,
            Category = sensor.Category,
            Name = sensor.Name,
            Value = sensor.Value,
        };
    }

    private static FanCapabilities BuildCapabilities(SensorReading sensor)
    {
        if (!sensor.ControlAvailable) return FanCapabilities.MonitorOnly;

        // LibreHardwareMonitor confirms that this exact sensor has an IControl object,
        // so min/max/default can be reported. Writes remain safety-blocked in this
        // phase because IControl's numeric range is not a physical minimum-RPM proof.
        return new FanCapabilities
        {
            RpmReadable = true,
            ControlReadable = sensor.ControlPercent.HasValue,
            ControlWritable = false,
            FixedControlSupported = false,
            SoftwareCurveSupported = false,
            FanStopSupported = false,
            CanRestoreDefault = true,
            MinimumControl = sensor.ControlMin,
            MaximumControl = sensor.ControlMax,
            Backend = "libre-hardware-monitor",
        };
    }

    private static List<FanTemperatureSensor> SelectAvailableTemperatures(
        FanRole role,
        string hardwareName,
        IReadOnlyList<FanTemperatureSensor> temperatures)
    {
        IEnumerable<FanTemperatureSensor> selected = role switch
        {
            FanRole.CpuFan or FanRole.CpuOptional =>
                temperatures.Where(x => string.Equals(x.Category, "cpu", StringComparison.OrdinalIgnoreCase)),

            FanRole.GpuFan =>
                temperatures.Where(x =>
                    string.Equals(x.Category, "gpu", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Hardware, hardwareName, StringComparison.OrdinalIgnoreCase)),

            _ => temperatures.Where(x =>
                string.Equals(x.Category, "cpu", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Category, "gpu", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Category, "motherboard", StringComparison.OrdinalIgnoreCase)),
        };

        return selected
            .OrderBy(x => TemperaturePriority(role, x))
            .ThenBy(x => x.Hardware, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int TemperaturePriority(FanRole role, FanTemperatureSensor sensor)
    {
        string name = sensor.Name;
        if (role is FanRole.CpuFan or FanRole.CpuOptional)
        {
            if (ContainsAny(name, "Tctl/Tdie", "CPU Package", "Package")) return 0;
            if (ContainsAny(name, "Core Max", "CPU Die", "CCD")) return 1;
        }
        if (role == FanRole.GpuFan)
        {
            if (ContainsAny(name, "GPU Core", "Temperature")) return 0;
            if (ContainsAny(name, "Hot Spot", "Hotspot")) return 1;
        }
        return 10;
    }

    private static double? SelectReferenceTemperature(FanRole role, IReadOnlyList<FanTemperatureSensor> sensors)
    {
        if (sensors.Count == 0) return null;
        if (role is FanRole.CpuFan or FanRole.CpuOptional or FanRole.GpuFan)
            return sensors[0].Value;
        return null;
    }

    private static (FanRole Role, FanDetectionConfidence Confidence, string Evidence) Classify(SensorReading sensor)
    {
        string name = Normalize(sensor.Name);
        if (string.Equals(sensor.Category, "gpu", StringComparison.OrdinalIgnoreCase))
            return (FanRole.GpuFan, FanDetectionConfidence.Confirmed, "Fan sensor belongs to a GPU hardware node.");

        if (ContainsAny(name, "pump", "aio pump", "water pump"))
            return (FanRole.Pump, FanDetectionConfidence.High, "Fan sensor name identifies a pump channel.");

        if (ContainsAny(name, "cpu opt", "cpu_opt", "cpu optional", "cpuopt"))
            return (FanRole.CpuOptional, FanDetectionConfidence.High, "Fan sensor name identifies a CPU optional channel.");

        if (ContainsAny(name, "cpu fan", "cpu_fan", "cpufan"))
            return (FanRole.CpuFan, FanDetectionConfidence.High, "Fan sensor name identifies a CPU fan channel.");

        if (ContainsAny(name, "sys fan", "sys_fan", "sysfan", "cha fan", "cha_fan", "chafan",
            "chassis fan", "system fan", "case fan"))
            return (FanRole.CaseFan, FanDetectionConfidence.High, "Fan sensor name identifies a system/chassis fan channel.");

        return (FanRole.Unknown, FanDetectionConfidence.Low, "No reliable role metadata is exposed by the current sensor source.");
    }

    private static string BuildDisplayName(SensorReading sensor, FanRole role)
    {
        if (!string.IsNullOrWhiteSpace(sensor.Name)) return sensor.Name.Trim();
        return role switch
        {
            FanRole.CpuFan => "CPU Fan",
            FanRole.CpuOptional => "CPU Optional",
            FanRole.GpuFan => "GPU Fan",
            FanRole.CaseFan => "Case Fan",
            FanRole.Pump => "Pump",
            _ => "Unknown Fan",
        };
    }

    private static string? DetectHeaderName(string name)
    {
        string compact = Normalize(name).Replace(' ', '_');
        if (compact.Contains("cpu_opt", StringComparison.Ordinal)) return "CPU_OPT";
        if (compact.Contains("cpu_fan", StringComparison.Ordinal)) return ExtractIndexedHeader(compact, "CPU_FAN");
        if (compact.Contains("sys_fan", StringComparison.Ordinal)) return ExtractIndexedHeader(compact, "SYS_FAN");
        if (compact.Contains("cha_fan", StringComparison.Ordinal)) return ExtractIndexedHeader(compact, "CHA_FAN");
        if (compact.Contains("pump", StringComparison.Ordinal)) return "PUMP";
        return null;
    }

    private static string ExtractIndexedHeader(string compact, string prefix)
    {
        int start = compact.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return prefix;
        int index = start + prefix.Length;
        var digits = new StringBuilder();
        while (index < compact.Length && (compact[index] == '_' || char.IsDigit(compact[index])))
        {
            if (char.IsDigit(compact[index])) digits.Append(compact[index]);
            index++;
        }
        return digits.Length == 0 ? prefix : prefix + "_" + digits;
    }

    private static int? ParseTrailingIndex(string name)
    {
        int end = name.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(name[end])) end--;
        int start = end;
        while (start >= 0 && char.IsDigit(name[start])) start--;
        if (start == end) return null;
        return int.TryParse(name[(start + 1)..(end + 1)], out int result) ? result : null;
    }

    private static int CompareDevices(FanDevice left, FanDevice right)
    {
        int role = RoleOrder(left.Role).CompareTo(RoleOrder(right.Role));
        if (role != 0) return role;
        int hardware = StringComparer.OrdinalIgnoreCase.Compare(left.HardwareName, right.HardwareName);
        if (hardware != 0) return hardware;
        return StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
    }

    private static int RoleOrder(FanRole role) => role switch
    {
        FanRole.CpuFan => 0,
        FanRole.CpuOptional => 1,
        FanRole.Pump => 2,
        FanRole.GpuFan => 3,
        FanRole.CaseFan => 4,
        FanRole.ExternalControllerFan => 5,
        _ => 6,
    };

    private static string BuildBaseKey(SensorReading sensor) =>
        $"{Normalize(sensor.Hardware)}|{Normalize(sensor.Category)}|{Normalize(sensor.Name)}";

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (string needle in needles)
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ShortHash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}
