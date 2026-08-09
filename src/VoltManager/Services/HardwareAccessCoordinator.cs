using LibreHardwareMonitor.Hardware;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Owns the single LibreHardwareMonitor Computer instance used by VoltManager.
/// Monitoring and fan control share this coordinator so reads and writes are
/// serialized against the same hardware objects instead of competing through
/// separate driver sessions.
/// </summary>
public interface IHardwareAccess : IDisposable
{
    bool Available { get; }
    bool ControlWritesAllowed { get; }
    SensorReport Read(bool force = false);
    HardwareFanControlDescriptor? GetFanControl(string controlIdentifier);
    HardwareFanControlResult SetFanSoftware(string controlIdentifier, double percent);
    HardwareFanControlResult RestoreFanDefault(string controlIdentifier);
    void Invalidate();
}

public sealed class HardwareAccessCoordinator : IHardwareAccess
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private Computer? _computer;
    private SensorReport _last = SensorReport.Empty;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private readonly Dictionary<string, IControl> _controls = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _ready;
    private bool _disposed;
    private bool _readFaulted;

    public bool Available { get; private set; }
    public bool ControlWritesAllowed { get; }

    public HardwareAccessCoordinator(bool controlWritesAllowed = false)
    {
        ControlWritesAllowed = controlWritesAllowed;
        Task.Run(InitComputer);
    }

    public SensorReport Read(bool force = false)
    {
        if (!_ready) return _last;

        lock (_gate)
        {
            if (_computer == null || _disposed) return _last;
            if (!force && DateTime.UtcNow - _lastUpdateUtc < UpdateInterval) return _last;

            _lastUpdateUtc = DateTime.UtcNow;
            try
            {
                var readings = new List<SensorReading>();
                var discoveredControls = new Dictionary<string, IControl>(StringComparer.OrdinalIgnoreCase);

                foreach (IHardware hardware in _computer.Hardware)
                {
                    hardware.Update();
                    Collect(hardware, readings, discoveredControls);
                    foreach (IHardware sub in hardware.SubHardware)
                    {
                        sub.Update();
                        Collect(sub, readings, discoveredControls);
                    }
                }

                _controls.Clear();
                foreach ((string key, IControl control) in discoveredControls)
                    _controls[key] = control;

                _last = new SensorReport
                {
                    CpuTemp = SensorAggregation.SelectCpuTemp(readings),
                    GpuTemp = SensorAggregation.SelectGpuTemp(readings),
                    CpuClock = SensorAggregation.SelectCpuClock(readings),
                    RamClock = SensorAggregation.SelectRamClock(readings),
                    Readings = readings,
                };
                _readFaulted = false;
            }
            catch (Exception ex)
            {
                _readFaulted = Logger.WarnOnce(_readFaulted, "Hardware sensor update failed", ex);
            }

            return _last;
        }
    }

    public HardwareFanControlDescriptor? GetFanControl(string controlIdentifier)
    {
        if (string.IsNullOrWhiteSpace(controlIdentifier)) return null;
        lock (_gate)
        {
            if (!_controls.TryGetValue(controlIdentifier, out IControl? control)) return null;
            return Describe(controlIdentifier, control);
        }
    }

    public HardwareFanControlResult SetFanSoftware(string controlIdentifier, double percent)
    {
        if (!ControlWritesAllowed)
            return HardwareFanControlResult.Fail("hardware_service_required", "Software fan writes require the isolated VoltManager hardware service.");
        if (!double.IsFinite(percent))
            return HardwareFanControlResult.Fail("invalid_value", "The requested fan control value is not finite.");

        lock (_gate)
        {
            if (_disposed || !_ready || _computer == null)
                return HardwareFanControlResult.Fail("hardware_unavailable", "Hardware access is not available.");
            if (!_controls.TryGetValue(controlIdentifier, out IControl? control))
                return HardwareFanControlResult.Fail("control_missing", "The fan control channel is no longer available.");

            double min = control.MinSoftwareValue;
            double max = control.MaxSoftwareValue;
            if (percent < min || percent > max)
                return HardwareFanControlResult.Fail("out_of_range", $"Requested control {percent:0.#}% is outside the backend range {min:0.#}-{max:0.#}%.");

            try
            {
                control.SetSoftware((float)percent);
                return HardwareFanControlResult.Success(Describe(controlIdentifier, control));
            }
            catch (UnauthorizedAccessException ex)
            {
                return HardwareFanControlResult.Fail("permission_denied", ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Warn("Fan control write failed: " + ex.Message);
                return HardwareFanControlResult.Fail("write_failed", ex.Message);
            }
        }
    }

    public HardwareFanControlResult RestoreFanDefault(string controlIdentifier)
    {
        if (!ControlWritesAllowed)
            return HardwareFanControlResult.Fail("hardware_service_required", "Software fan writes require the isolated VoltManager hardware service.");
        lock (_gate)
        {
            if (_disposed || !_ready || _computer == null)
                return HardwareFanControlResult.Fail("hardware_unavailable", "Hardware access is not available.");
            if (!_controls.TryGetValue(controlIdentifier, out IControl? control))
                return HardwareFanControlResult.Fail("control_missing", "The fan control channel is no longer available.");

            try
            {
                control.SetDefault();
                return HardwareFanControlResult.Success(Describe(controlIdentifier, control));
            }
            catch (UnauthorizedAccessException ex)
            {
                return HardwareFanControlResult.Fail("permission_denied", ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Warn("Restore fan default failed: " + ex.Message);
                return HardwareFanControlResult.Fail("restore_failed", ex.Message);
            }
        }
    }

    /// <summary>Forces the next monitoring read to rebuild hardware/control handles.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _lastUpdateUtc = DateTime.MinValue;
            _controls.Clear();
        }
    }

    private void InitComputer()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true,
                IsMemoryEnabled = true,
            };
            computer.Open();

            lock (_gate)
            {
                if (_disposed)
                {
                    TryClose(computer);
                    return;
                }
                _computer = computer;
                Available = true;
                _ready = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Hardware sensors unavailable: " + ex.Message);
        }
    }

    private static void Collect(
        IHardware hardware,
        List<SensorReading> readings,
        Dictionary<string, IControl> discoveredControls)
    {
        string category = SensorAggregation.MapCategory(hardware.HardwareType);
        ISensor[] sensors = hardware.Sensors;
        var controlSensors = sensors
            .Where(sensor => sensor.SensorType == SensorType.Control && sensor.Control != null)
            .ToList();

        foreach (ISensor sensor in sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value)) continue;

            string type = sensor.SensorType switch
            {
                SensorType.Temperature => "temp",
                SensorType.Fan => "fan",
                SensorType.Clock => "clock",
                _ => "",
            };
            if (type.Length == 0 || !SensorAggregation.IsLiveReading(type, sensor.Name, value)) continue;

            ISensor? controlSensor = null;
            IControl? control = null;
            string? controlIdentifier = null;

            if (sensor.SensorType == SensorType.Fan)
            {
                // Some LHM devices expose IControl directly on the fan sensor; others
                // (notably GPU paths) expose a separate SensorType.Control with the same
                // hardware channel index. Correlate only within the same IHardware node.
                control = sensor.Control;
                if (control != null)
                {
                    controlIdentifier = sensor.Identifier.ToString();
                }
                else
                {
                    var sameIndex = controlSensors.Where(candidate => candidate.Index == sensor.Index).ToList();
                    if (sameIndex.Count == 1)
                        controlSensor = sameIndex[0];
                    else
                    {
                        var sameName = controlSensors.Where(candidate =>
                            string.Equals(candidate.Name, sensor.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (sameName.Count == 1) controlSensor = sameName[0];
                    }

                    control = controlSensor?.Control;
                    controlIdentifier = controlSensor?.Identifier.ToString();
                }

                if (control != null && !string.IsNullOrWhiteSpace(controlIdentifier))
                    discoveredControls[controlIdentifier] = control;
            }

            double? controlPercent = null;
            if (control != null)
            {
                if (controlSensor?.Value is { } controlSensorValue && !float.IsNaN(controlSensorValue))
                    controlPercent = Math.Round(controlSensorValue, 1);
                else if (control.ControlMode == ControlMode.Software)
                    controlPercent = Math.Round(control.SoftwareValue, 1);
            }

            readings.Add(new SensorReading
            {
                Identifier = sensor.Identifier.ToString(),
                Hardware = hardware.Name,
                Category = category,
                Name = sensor.Name,
                Type = type,
                Value = Math.Round(value, type == "clock" ? 0 : type == "temp" ? 1 : 0),
                ControlAvailable = control != null,
                ControlIdentifier = controlIdentifier,
                ControlMode = control?.ControlMode.ToString(),
                ControlPercent = controlPercent,
                ControlMin = control != null ? Math.Round(control.MinSoftwareValue, 1) : null,
                ControlMax = control != null ? Math.Round(control.MaxSoftwareValue, 1) : null,
            });
        }
    }

    private static HardwareFanControlDescriptor Describe(string identifier, IControl control) => new()
    {
        Identifier = identifier,
        Mode = control.ControlMode.ToString(),
        SoftwareValue = control.ControlMode == ControlMode.Software ? Math.Round(control.SoftwareValue, 1) : null,
        Minimum = Math.Round(control.MinSoftwareValue, 1),
        Maximum = Math.Round(control.MaxSoftwareValue, 1),
    };

    private static void TryClose(Computer computer)
    {
        try { computer.Close(); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            Available = false;
            _controls.Clear();
            if (_computer != null)
            {
                TryClose(_computer);
                _computer = null;
            }
        }
    }
}

public sealed record HardwareFanControlDescriptor
{
    public string Identifier { get; init; } = "";
    public string Mode { get; init; } = "";
    public double? SoftwareValue { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; }
}

public sealed record HardwareFanControlResult
{
    public bool Ok { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public HardwareFanControlDescriptor? Control { get; init; }

    public static HardwareFanControlResult Success(HardwareFanControlDescriptor descriptor) => new()
    {
        Ok = true,
        Code = "ok",
        Control = descriptor,
    };

    public static HardwareFanControlResult Fail(string code, string message) => new()
    {
        Ok = false,
        Code = code,
        Message = message,
    };
}
