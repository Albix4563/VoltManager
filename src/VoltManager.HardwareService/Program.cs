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
        _ = WatchParentAsync(parentPid, shutdown.Token);

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
        finally { shutdown.Cancel(); }
        return 0;
    }

    private static object? Dispatch(ServiceRequest request, HardwareHost hardware)
    {
        JsonElement payload = request.Payload;
        return request.Method switch
        {
            "ping" => new { ready = true },
            "read" => hardware.Read(ReadSampleRequest(payload), payload.TryGetProperty("force", out JsonElement force) && force.ValueKind == JsonValueKind.True),
            "invalidate" => hardware.Invalidate(),
            "shutdown" => new { success = true },
            _ => throw new InvalidOperationException("Unknown hardware service method: " + request.Method),
        };
    }

    private static HardwareSampleRequestDto ReadSampleRequest(JsonElement payload)
    {
        bool temperatures = !payload.TryGetProperty("temperatures", out JsonElement temp) || temp.ValueKind == JsonValueKind.True;
        bool visualDetails = !payload.TryGetProperty("visualDetails", out JsonElement details) || details.ValueKind == JsonValueKind.True;
        long intervalMs = payload.TryGetProperty("minimumIntervalMs", out JsonElement interval)
            && interval.TryGetInt64(out long parsed)
            ? Math.Clamp(parsed, 0, 60_000)
            : 2_000;
        return new HardwareSampleRequestDto(temperatures, visualDetails, TimeSpan.FromMilliseconds(intervalMs));
    }

    private static async Task WatchParentAsync(int parentPid, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) { return; }
            if (ParentAlive(parentPid)) continue;
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
    private const int MaxUiSensors = 32;
    private readonly object _gate = new();
    private Computer? _computer;
    private SensorReportDto _last = new();
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private HardwareSampleRequestDto _lastRequest;
    private bool _ready;
    private bool _disposed;

    public HardwareHost() => Task.Run(Initialize);

    public object Read(HardwareSampleRequestDto request, bool force)
    {
        lock (_gate)
        {
            if (!_ready || _computer == null) return new { available = false, report = _last };
            RefreshLocked(request, force);
            return new { available = true, report = _last };
        }
    }

    public object Invalidate()
    {
        lock (_gate)
        {
            _lastUpdateUtc = DateTime.MinValue;
            _lastRequest = default;
        }
        return new { success = true };
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
                RefreshLocked(HardwareSampleRequestDto.Full, true);
            }
        }
        catch { }
    }

    private void RefreshLocked(HardwareSampleRequestDto request, bool force)
    {
        if (_computer == null) return;
        DateTime nowUtc = DateTime.UtcNow;
        if (!force && _lastRequest.Covers(request) && nowUtc - _lastUpdateUtc < request.MinimumInterval) return;
        _lastUpdateUtc = nowUtc;
        var readings = new List<SensorReadingDto>();
        try
        {
            foreach (IHardware hardware in _computer.Hardware)
            {
                if (!ShouldUpdate(hardware.HardwareType, request)) continue;
                hardware.Update();
                Collect(hardware, readings, request);
                foreach (IHardware sub in hardware.SubHardware)
                {
                    sub.Update();
                    Collect(sub, readings, request);
                }
            }
            _last = new SensorReportDto
            {
                CpuTemp = SelectTemperature(readings, "cpu", "Tctl/Tdie", "CPU Package", "Package", "Core Max"),
                GpuTemp = SelectTemperature(readings, "gpu", "GPU Core", "GPU Hot Spot", "Temperature"),
                CpuClock = SelectClock(readings, "cpu"),
                RamClock = SelectMemoryClock(readings),
                Readings = CapReadingsForUi(readings),
                SampledAtUtc = nowUtc,
                DetailsAvailable = request.VisualDetails,
            };
            _lastRequest = request;
        }
        catch { }
    }

    private static bool ShouldUpdate(HardwareType type, HardwareSampleRequestDto request)
        => request.VisualDetails || (request.Temperatures && type is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);

    private static void Collect(IHardware hardware, List<SensorReadingDto> readings, HardwareSampleRequestDto request)
    {
        string category = hardware.HardwareType switch
        {
            HardwareType.Cpu => "cpu",
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "gpu",
            HardwareType.Storage => "storage",
            HardwareType.Memory => "memory",
            _ => "motherboard",
        };

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value)) continue;
            string type = sensor.SensorType switch
            {
                SensorType.Temperature when request.Temperatures => "temp",
                SensorType.Clock when request.VisualDetails => "clock",
                _ => "",
            };
            if (type.Length == 0 || !IsLive(type, sensor.Name, value)) continue;

            readings.Add(new SensorReadingDto
            {
                Identifier = sensor.Identifier.ToString(),
                Hardware = hardware.Name,
                Category = category,
                Name = sensor.Name,
                Type = type,
                Value = Math.Round(value, type == "clock" ? 0 : 1),
            });
        }
    }

    private static bool IsLive(string type, string name, float value)
    {
        if (type == "temp")
            return value > 0
                && !name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Trip", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Throttle", StringComparison.OrdinalIgnoreCase);
        if (type == "clock") return value > 0;
        return true;
    }

    private static double? SelectTemperature(List<SensorReadingDto> readings, string category, params string[] preferred)
    {
        var list = readings.Where(x => x.Category == category && x.Type == "temp").ToList();
        if (list.Count == 0) return null;
        foreach (string token in preferred)
        {
            SensorReadingDto? match = list.FirstOrDefault(x => x.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(x => x.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Value;
        }
        return list.Max(x => x.Value);
    }

    private static double? SelectClock(List<SensorReadingDto> readings, string category)
    {
        var clocks = readings
            .Where(x => x.Category == category && x.Type == "clock" && !ContainsAny(x.Name, "Bus", "Fabric", "Memory", "DRAM", "SOC", "Uncore", "FCLK", "MCLK", "UCLK"))
            .ToList();
        return clocks.Count == 0 ? null : clocks.Max(x => x.Value);
    }

    private static double? SelectMemoryClock(List<SensorReadingDto> readings)
    {
        var clocks = readings
            .Where(x => (x.Category == "memory" || x.Category == "motherboard") && x.Type == "clock"
                && ContainsAny(x.Name, "Memory", "DRAM", "DDR", "RAM")
                && !ContainsAny(x.Name, "Controller", "Fabric", "Uncore", "Infinity"))
            .ToList();
        return clocks.Count == 0 ? null : clocks.Max(x => x.Value);
    }

    private static List<SensorReadingDto> CapReadingsForUi(List<SensorReadingDto> readings)
    {
        if (readings.Count == 0) return readings;
        if (readings.Count <= MaxUiSensors && readings.TrueForAll(r => r.Type is not "clock"))
            return readings;

        var preferred = new List<SensorReadingDto>(Math.Min(MaxUiSensors, readings.Count));
        foreach (var reading in readings)
        {
            if (reading.Type == "temp") preferred.Add(reading);
            if (preferred.Count >= MaxUiSensors) return preferred;
        }
        foreach (var reading in readings)
        {
            if (reading.Type == "clock") preferred.Add(reading);
            if (preferred.Count >= MaxUiSensors) break;
        }
        return preferred;
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

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
            if (_computer != null)
            {
                TryClose(_computer);
                _computer = null;
            }
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
    public DateTime? SampledAtUtc { get; set; }
    public bool DetailsAvailable { get; set; }
}

internal readonly record struct HardwareSampleRequestDto(bool Temperatures, bool VisualDetails, TimeSpan MinimumInterval)
{
    public static readonly HardwareSampleRequestDto Full = new(true, true, TimeSpan.FromSeconds(2));

    public bool Covers(HardwareSampleRequestDto requested)
        => (!requested.Temperatures || Temperatures) && (!requested.VisualDetails || VisualDetails);
}

internal sealed class SensorReadingDto
{
    public string Identifier { get; set; } = "";
    public string Hardware { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public double Value { get; set; }
}
