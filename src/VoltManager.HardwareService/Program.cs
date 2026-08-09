using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace VoltManager.HardwareService;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> Main(string[] args)
    {
        string? pipeName = ReadArg(args, "--pipe");
        if (!int.TryParse(ReadArg(args, "--parent"), out int parentPid) || parentPid <= 0 || string.IsNullOrWhiteSpace(pipeName))
            return 2;

        using var hardware = new HardwareHost();
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var shutdown = new CancellationTokenSource();
        _ = WatchParentAsync(parentPid, hardware, shutdown.Token);

        try
        {
            await server.WaitForConnectionAsync(shutdown.Token);
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true };

            while (!shutdown.IsCancellationRequested && server.IsConnected)
            {
                string? line = await reader.ReadLineAsync(shutdown.Token);
                if (line == null) break;
                if (line.Length > 128 * 1024)
                {
                    await writer.WriteLineAsync(SerializeFailure("", "request_too_large"));
                    continue;
                }

                ServiceRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<ServiceRequest>(line, JsonOptions);
                    if (request == null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method))
                        throw new InvalidDataException("Invalid request envelope.");
                    object? result = Dispatch(request, hardware);
                    await writer.WriteLineAsync(SerializeSuccess(request.Id, result));
                    if (request.Method == "shutdown") break;
                }
                catch (Exception ex)
                {
                    await writer.WriteLineAsync(SerializeFailure(request?.Id ?? "", ex.Message));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            shutdown.Cancel();
            hardware.RestoreAllActive();
        }
        return 0;
    }

    private static object? Dispatch(ServiceRequest request, HardwareHost hardware)
    {
        JsonElement payload = request.Payload;
        return request.Method switch
        {
            "ping" => new { ready = true },
            "read" => hardware.Read(payload.TryGetProperty("force", out JsonElement force) && force.ValueKind == JsonValueKind.True),
            "getFanControl" => hardware.GetFanControl(GetString(payload, "controlIdentifier")),
            "setFanSoftware" => hardware.SetFanSoftware(GetString(payload, "controlIdentifier"), payload.GetProperty("percent").GetDouble()),
            "restoreFanDefault" => hardware.RestoreFanDefault(GetString(payload, "controlIdentifier")),
            "invalidate" => hardware.Invalidate(),
            "shutdown" => hardware.Shutdown(),
            _ => throw new InvalidOperationException("Unknown hardware service method: " + request.Method),
        };
    }

    private static async Task WatchParentAsync(int parentPid, HardwareHost hardware, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) { return; }
            if (ParentAlive(parentPid)) continue;
            hardware.RestoreAllActive();
            Environment.Exit(0);
        }
    }

    private static bool ParentAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static string? ReadArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    private static string GetString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";

    private static string SerializeSuccess(string id, object? result) =>
        JsonSerializer.Serialize(new { id, ok = true, result }, JsonOptions);
    private static string SerializeFailure(string id, string error) =>
        JsonSerializer.Serialize(new { id, ok = false, error }, JsonOptions);

    private sealed class ServiceRequest
    {
        public string Id { get; set; } = "";
        public string Method { get; set; } = "";
        public JsonElement Payload { get; set; }
    }
}

internal sealed class HardwareHost : IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly HashSet<string> _activeControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IControl> _controls = new(StringComparer.OrdinalIgnoreCase);
    private Computer? _computer;
    private SensorReportDto _last = new();
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private bool _ready;
    private bool _disposed;

    public HardwareHost() => Task.Run(Initialize);

    public object Read(bool force)
    {
        lock (_gate)
        {
            if (!_ready || _computer == null) return new { available = false, report = _last };
            RefreshLocked(force);
            return new { available = true, report = _last };
        }
    }

    public ControlDescriptorDto? GetFanControl(string identifier)
    {
        lock (_gate)
        {
            EnsureControlLocked(identifier);
            return _controls.TryGetValue(identifier, out IControl? control) ? Describe(identifier, control) : null;
        }
    }

    public ControlResultDto SetFanSoftware(string identifier, double percent)
    {
        if (!double.IsFinite(percent)) return ControlResultDto.Fail("invalid_value", "Fan control value is not finite.");
        lock (_gate)
        {
            if (!_ready || _computer == null) return ControlResultDto.Fail("hardware_unavailable", "Hardware is unavailable.");
            EnsureControlLocked(identifier);
            if (!_controls.TryGetValue(identifier, out IControl? control)) return ControlResultDto.Fail("control_missing", "Fan control channel is unavailable.");
            double min = control.MinSoftwareValue, max = control.MaxSoftwareValue;
            if (percent < min || percent > max) return ControlResultDto.Fail("out_of_range", $"Requested value is outside {min:0.#}-{max:0.#}%.");
            try
            {
                control.SetSoftware((float)percent);
                _activeControls.Add(identifier);
                return ControlResultDto.Success(Describe(identifier, control));
            }
            catch (UnauthorizedAccessException ex) { return ControlResultDto.Fail("permission_denied", ex.Message); }
            catch (Exception ex) { return ControlResultDto.Fail("write_failed", ex.Message); }
        }
    }

    public ControlResultDto RestoreFanDefault(string identifier)
    {
        lock (_gate)
        {
            if (!_ready || _computer == null) return ControlResultDto.Fail("hardware_unavailable", "Hardware is unavailable.");
            EnsureControlLocked(identifier);
            if (!_controls.TryGetValue(identifier, out IControl? control)) return ControlResultDto.Fail("control_missing", "Fan control channel is unavailable.");
            try
            {
                control.SetDefault();
                _activeControls.Remove(identifier);
                return ControlResultDto.Success(Describe(identifier, control));
            }
            catch (UnauthorizedAccessException ex) { return ControlResultDto.Fail("permission_denied", ex.Message); }
            catch (Exception ex) { return ControlResultDto.Fail("restore_failed", ex.Message); }
        }
    }

    public object Invalidate()
    {
        lock (_gate) _lastUpdateUtc = DateTime.MinValue;
        return new { success = true };
    }

    public object Shutdown()
    {
        RestoreAllActive();
        return new { success = true };
    }

    public void RestoreAllActive()
    {
        lock (_gate)
        {
            foreach (string id in _activeControls.ToList())
            {
                if (!_controls.TryGetValue(id, out IControl? control)) continue;
                try { control.SetDefault(); _activeControls.Remove(id); } catch { }
            }
        }
    }

    private void Initialize()
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
                if (_disposed) { TryClose(computer); return; }
                _computer = computer;
                _ready = true;
                RefreshLocked(true);
            }
        }
        catch { }
    }

    private void EnsureControlLocked(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || _controls.ContainsKey(identifier)) return;
        RefreshLocked(true);
    }

    private void RefreshLocked(bool force)
    {
        if (_computer == null) return;
        if (!force && DateTime.UtcNow - _lastUpdateUtc < UpdateInterval) return;
        _lastUpdateUtc = DateTime.UtcNow;
        var readings = new List<SensorReadingDto>();
        var foundControls = new Dictionary<string, IControl>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (IHardware hardware in _computer.Hardware)
            {
                hardware.Update(); Collect(hardware, readings, foundControls);
                foreach (IHardware sub in hardware.SubHardware) { sub.Update(); Collect(sub, readings, foundControls); }
            }
            _controls.Clear();
            foreach ((string id, IControl control) in foundControls) _controls[id] = control;
            _last = new SensorReportDto
            {
                CpuTemp = SelectTemperature(readings, "cpu", "Tctl/Tdie", "CPU Package", "Package", "Core Max"),
                GpuTemp = SelectTemperature(readings, "gpu", "GPU Core", "GPU Hot Spot", "Temperature"),
                CpuClock = SelectClock(readings, "cpu"),
                RamClock = SelectMemoryClock(readings),
                Readings = readings,
            };
        }
        catch { }
    }

    private static void Collect(IHardware hardware, List<SensorReadingDto> readings, Dictionary<string, IControl> controls)
    {
        string category = hardware.HardwareType switch
        {
            HardwareType.Cpu => "cpu",
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "gpu",
            HardwareType.Storage => "storage",
            HardwareType.Memory => "memory",
            _ => "motherboard",
        };
        ISensor[] sensors = hardware.Sensors;
        var controlSensors = sensors.Where(x => x.SensorType == SensorType.Control && x.Control != null).ToList();
        foreach (ISensor sensor in sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value)) continue;
            string type = sensor.SensorType switch { SensorType.Temperature => "temp", SensorType.Fan => "fan", SensorType.Clock => "clock", _ => "" };
            if (type.Length == 0 || !IsLive(type, sensor.Name, value)) continue;

            IControl? control = null; ISensor? controlSensor = null; string? controlId = null;
            if (sensor.SensorType == SensorType.Fan)
            {
                control = sensor.Control;
                if (control != null) controlId = sensor.Identifier.ToString();
                else
                {
                    var byIndex = controlSensors.Where(x => x.Index == sensor.Index).ToList();
                    if (byIndex.Count == 1) controlSensor = byIndex[0];
                    else
                    {
                        var byName = controlSensors.Where(x => string.Equals(x.Name, sensor.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (byName.Count == 1) controlSensor = byName[0];
                    }
                    control = controlSensor?.Control; controlId = controlSensor?.Identifier.ToString();
                }
                if (control != null && !string.IsNullOrWhiteSpace(controlId)) controls[controlId] = control;
            }

            double? controlPercent = null;
            if (control != null)
            {
                if (controlSensor?.Value is { } cv && !float.IsNaN(cv)) controlPercent = Math.Round(cv, 1);
                else if (control.ControlMode == ControlMode.Software) controlPercent = Math.Round(control.SoftwareValue, 1);
            }
            readings.Add(new SensorReadingDto
            {
                Identifier = sensor.Identifier.ToString(), Hardware = hardware.Name, Category = category, Name = sensor.Name, Type = type,
                Value = Math.Round(value, type == "clock" ? 0 : type == "temp" ? 1 : 0), ControlAvailable = control != null,
                ControlIdentifier = controlId, ControlMode = control?.ControlMode.ToString(), ControlPercent = controlPercent,
                ControlMin = control != null ? Math.Round(control.MinSoftwareValue, 1) : null,
                ControlMax = control != null ? Math.Round(control.MaxSoftwareValue, 1) : null,
            });
        }
    }

    private static bool IsLive(string type, string name, float value)
    {
        if (type == "temp") return value > 0 && !name.Contains("Warning", StringComparison.OrdinalIgnoreCase) && !name.Contains("Critical", StringComparison.OrdinalIgnoreCase) && !name.Contains("Trip", StringComparison.OrdinalIgnoreCase) && !name.Contains("Throttle", StringComparison.OrdinalIgnoreCase);
        if (type == "clock") return value > 0;
        return true;
    }

    private static double? SelectTemperature(List<SensorReadingDto> readings, string category, params string[] preferred)
    {
        var list = readings.Where(x => x.Category == category && x.Type == "temp").ToList();
        if (list.Count == 0) return null;
        foreach (string token in preferred)
        {
            SensorReadingDto? match = list.FirstOrDefault(x => x.Name.Equals(token, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault(x => x.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Value;
        }
        return list.Max(x => x.Value);
    }

    private static double? SelectClock(List<SensorReadingDto> readings, string category)
    {
        var clocks = readings.Where(x => x.Category == category && x.Type == "clock" && !ContainsAny(x.Name, "Bus", "Fabric", "Memory", "DRAM", "SOC", "Uncore", "FCLK", "MCLK", "UCLK")).ToList();
        return clocks.Count == 0 ? null : clocks.Max(x => x.Value);
    }
    private static double? SelectMemoryClock(List<SensorReadingDto> readings)
    {
        var clocks = readings.Where(x => (x.Category == "memory" || x.Category == "motherboard") && x.Type == "clock" && ContainsAny(x.Name, "Memory", "DRAM", "DDR", "RAM") && !ContainsAny(x.Name, "Controller", "Fabric", "Uncore", "Infinity")).ToList();
        return clocks.Count == 0 ? null : clocks.Max(x => x.Value);
    }
    private static bool ContainsAny(string value, params string[] tokens) => tokens.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static ControlDescriptorDto Describe(string id, IControl control) => new() { Identifier = id, Mode = control.ControlMode.ToString(), SoftwareValue = control.ControlMode == ControlMode.Software ? Math.Round(control.SoftwareValue, 1) : null, Minimum = Math.Round(control.MinSoftwareValue, 1), Maximum = Math.Round(control.MaxSoftwareValue, 1) };
    private static void TryClose(Computer computer) { try { computer.Close(); } catch { } }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            RestoreAllActive(); _ready = false;
            if (_computer != null) { TryClose(_computer); _computer = null; }
        }
    }
}

internal sealed class SensorReportDto
{
    public double? CpuTemp { get; set; }
    public double? GpuTemp { get; set; }
    public double? CpuClock { get; set; }
    public double? RamClock { get; set; }
    public List<SensorReadingDto> Readings { get; set; } = new();
}
internal sealed class SensorReadingDto
{
    public string Identifier { get; set; } = "";
    public string Hardware { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public double Value { get; set; }
    public bool ControlAvailable { get; set; }
    public string? ControlIdentifier { get; set; }
    public string? ControlMode { get; set; }
    public double? ControlPercent { get; set; }
    public double? ControlMin { get; set; }
    public double? ControlMax { get; set; }
}
internal sealed class ControlDescriptorDto
{
    public string Identifier { get; set; } = "";
    public string Mode { get; set; } = "";
    public double? SoftwareValue { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
}
internal sealed class ControlResultDto
{
    public bool Ok { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public ControlDescriptorDto? Control { get; set; }
    public static ControlResultDto Success(ControlDescriptorDto descriptor) => new() { Ok = true, Code = "ok", Control = descriptor };
    public static ControlResultDto Fail(string code, string message) => new() { Ok = false, Code = code, Message = message };
}
