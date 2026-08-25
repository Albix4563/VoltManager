from pathlib import Path
import re
import shutil


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8-sig")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def remove_method(path: str, signature: str, replacement: str = "") -> None:
    text = read(path)
    start = text.find(signature)
    if start < 0:
        raise RuntimeError(f"Missing method in {path}: {signature}")
    brace = text.find("{", start)
    if brace < 0:
        raise RuntimeError(f"Missing opening brace in {path}: {signature}")

    depth = 0
    in_string = False
    verbatim = False
    escape = False
    i = brace
    while i < len(text):
        c = text[i]
        if in_string:
            if verbatim:
                if c == '"':
                    if i + 1 < len(text) and text[i + 1] == '"':
                        i += 2
                        continue
                    in_string = False
                    verbatim = False
            else:
                if escape:
                    escape = False
                elif c == "\\":
                    escape = True
                elif c == '"':
                    in_string = False
        else:
            if c == '"':
                in_string = True
                verbatim = i > 0 and text[i - 1] == "@"
            elif c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    end = i + 1
                    while end < len(text) and text[end] in "\r\n":
                        end += 1
                    new_text = text[:start] + replacement.rstrip()
                    if replacement:
                        new_text += "\n\n"
                    new_text += text[end:]
                    write(path, new_text)
                    return
        i += 1
    raise RuntimeError(f"Unbalanced method in {path}: {signature}")


# 1. Delete all feature-owned source, UI assets and tests.
fan_dir = Path("src/VoltManager/Fans")
if fan_dir.exists():
    shutil.rmtree(fan_dir)

for raw in [
    "src/VoltManager/wwwroot/css/fans.css",
    "src/VoltManager/wwwroot/js/fan-visualizer.feature.js",
    "src/VoltManager/wwwroot/js/fan-visualizer.js",
    "src/VoltManager/wwwroot/js/fans.feature.js",
    "src/VoltManager/wwwroot/js/fans.js",
    "tests/VoltManager.Tests/FanControlRecoveryStoreTests.cs",
    "tests/VoltManager.Tests/FanControlServiceTests.cs",
    "tests/VoltManager.Tests/FanDiscoveryTests.cs",
    "tests/VoltManager.Tests/FanExternalConflictDetectorTests.cs",
    "tests/VoltManager.Tests/FanProfileCompatibilityTests.cs",
    "tests/VoltManager.Tests/FanProfileStoreTests.cs",
    "tests/VoltManager.Tests/FanSafetyPolicyTests.cs",
    "tests/VoltManager.Tests/LibreHardwareMonitorFanControlContractTests.cs",
    "tests/fan-visualizer-motion.test.mjs",
]:
    path = Path(raw)
    if path.exists():
        path.unlink()


# 2. Remove application lifecycle ownership, preserving generic sensor invalidation on resume.
app_path = "src/VoltManager/App.xaml.cs"
app = read(app_path)
app = app.replace("using VoltManager.Fans;\n", "")
app = "\n".join(
    line for line in app.splitlines()
    if "FanManagementService Fans" not in line
    and "Fans = new FanManagementService" not in line
    and 'SafeCleanup("fan management", Fans.Dispose);' not in line
) + "\n"
write(app_path, app)
remove_method(
    app_path,
    "    private void OnSystemPowerModeChanged",
    '''    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        try
        {
            HardwareAccess.Invalidate();
        }
        catch (Exception ex)
        {
            Logger.Warn("Hardware resume handling failed: " + ex.Message);
        }
    }''',
)


# 3. Remove the complete WebView RPC/event surface for the deleted feature.
bridge_path = "src/VoltManager/Bridge/HostBridge.cs"
bridge = read(bridge_path).replace("using VoltManager.Fans;\n", "")
bridge = "\n".join(line for line in bridge.splitlines() if "_app.Fans.ControlStateChanged" not in line) + "\n"
start = bridge.find('            case "getFanTopology":')
end = bridge.find('            case "getBatteryHealth":', start)
if start < 0 or end < 0:
    raise RuntimeError("Fan RPC block boundaries were not found in HostBridge.cs")
bridge = bridge[:start] + bridge[end:]
write(bridge_path, bridge)


# 4. Shrink the shared sensor model to generic temperature/clock telemetry.
models_path = "src/VoltManager/Models/Models.cs"
models = read(models_path).replace("// temp|fan", "// temp|clock")
control_names = {
    "ControlAvailable", "ControlIdentifier", "ControlMode",
    "ControlPercent", "ControlMin", "ControlMax",
}
models = "\n".join(
    line for line in models.splitlines()
    if not any(re.search(rf"\\b{name}\\b", line) for name in control_names)
) + "\n"
write(models_path, models)


# 5. Make the in-process hardware access sensor-only.
write("src/VoltManager/Services/HardwareAccessCoordinator.cs", r'''using LibreHardwareMonitor.Hardware;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Owns the single LibreHardwareMonitor Computer instance used by VoltManager
/// and serializes sensor reads against the same hardware session.
/// </summary>
public interface IHardwareAccess : IDisposable
{
    bool Available { get; }
    SensorReport Read(bool force = false);
    void Invalidate();
}

public sealed class HardwareAccessCoordinator : IHardwareAccess
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private Computer? _computer;
    private SensorReport _last = SensorReport.Empty;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private volatile bool _ready;
    private bool _disposed;
    private bool _readFaulted;

    public bool Available { get; private set; }

    public HardwareAccessCoordinator() => Task.Run(InitComputer);

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
                foreach (IHardware hardware in _computer.Hardware)
                {
                    hardware.Update();
                    Collect(hardware, readings);
                    foreach (IHardware sub in hardware.SubHardware)
                    {
                        sub.Update();
                        Collect(sub, readings);
                    }
                }

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

    public void Invalidate()
    {
        lock (_gate) _lastUpdateUtc = DateTime.MinValue;
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

    private static void Collect(IHardware hardware, List<SensorReading> readings)
    {
        string category = SensorAggregation.MapCategory(hardware.HardwareType);
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value || float.IsNaN(value)) continue;

            string type = sensor.SensorType switch
            {
                SensorType.Temperature => "temp",
                SensorType.Clock => "clock",
                _ => "",
            };
            if (type.Length == 0 || !SensorAggregation.IsLiveReading(type, sensor.Name, value)) continue;

            readings.Add(new SensorReading
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
            if (_computer != null)
            {
                TryClose(_computer);
                _computer = null;
            }
        }
    }
}
''')


# 6. Make the isolated hardware client sensor-only.
write("src/VoltManager/Services/HardwareServiceClient.cs", r'''using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VoltManager.Services;

/// <summary>Named-pipe client for the isolated hardware sensor process.</summary>
public sealed class HardwareServiceClient : IHardwareAccess
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Process _process;
    private SensorReport _last = SensorReport.Empty;
    private HardwareAccessCoordinator? _fallback;
    private bool _hardwareAvailable;
    private bool _rpcFaulted;
    private bool _disposed;
    private long _nextId;

    public bool Available => !_disposed && ((_pipe.IsConnected && _hardwareAvailable) || (_fallback?.Available ?? false));

    private HardwareServiceClient(NamedPipeClientStream pipe, Process process)
    {
        _pipe = pipe;
        _process = process;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true };
    }

    public static HardwareServiceClient? TryStart()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "VoltManager.HardwareService.exe");
        if (!File.Exists(executable))
        {
            Logger.Warn("Hardware service executable not found; using in-process monitoring.");
            return null;
        }

        string pipeName = "VoltManager_Hardware_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N");
        Process? process = null;
        NamedPipeClientStream? pipe = null;
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("--pipe");
            start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add("--parent");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            process = Process.Start(start) ?? throw new InvalidOperationException("Hardware service process did not start.");

            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(2500);
            var client = new HardwareServiceClient(pipe, process);
            pipe = null;
            process = null;
            var pong = client.Call<HardwareServicePing>("ping", null);
            if (pong == null || !pong.Ready)
            {
                client.Dispose();
                return null;
            }
            Logger.Info("Isolated hardware service connected.");
            return client;
        }
        catch (Exception ex)
        {
            Logger.Warn("Hardware service unavailable; using in-process monitoring: " + ex.Message);
            try { pipe?.Dispose(); } catch { }
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            try { process?.Dispose(); } catch { }
            return null;
        }
    }

    public SensorReport Read(bool force = false)
    {
        HardwareReadEnvelope? envelope = Call<HardwareReadEnvelope>("read", new { force });
        if (envelope == null)
        {
            EnsureFallbackIfServiceExited();
            if (_fallback != null) _last = _fallback.Read(force);
            return _last;
        }
        _rpcFaulted = false;
        _hardwareAvailable = envelope.Available;
        if (envelope.Report != null) _last = envelope.Report;
        return _last;
    }

    public void Invalidate()
    {
        _ = Call<object>("invalidate", null);
        _fallback?.Invalidate();
        _last = SensorReport.Empty;
    }

    private T? Call<T>(string method, object? payload)
    {
        lock (_gate)
        {
            if (_disposed || !_pipe.IsConnected) return default;
            try
            {
                string id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string request = JsonSerializer.Serialize(new HardwareServiceRequest
                {
                    Id = id,
                    Method = method,
                    Payload = payload == null
                        ? JsonSerializer.SerializeToElement(new { }, JsonOptions)
                        : JsonSerializer.SerializeToElement(payload, JsonOptions),
                }, JsonOptions);
                _writer.WriteLine(request);
                using var timeout = new CancellationTokenSource(RpcTimeout);
                string? line = _reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                if (line == null) throw new EndOfStreamException("Hardware service pipe closed.");
                HardwareServiceResponse? response = JsonSerializer.Deserialize<HardwareServiceResponse>(line, JsonOptions);
                if (response == null || response.Id != id) throw new InvalidDataException("Hardware service returned an invalid response.");
                if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Hardware service request failed.");
                _rpcFaulted = false;
                if (!response.Result.HasValue || response.Result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
                return response.Result.Value.Deserialize<T>(JsonOptions);
            }
            catch (Exception ex)
            {
                _hardwareAvailable = false;
                _rpcFaulted = Logger.WarnOnce(_rpcFaulted, "Hardware service RPC failed", ex);
                return default;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_pipe.IsConnected)
                {
                    string id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _writer.WriteLine(JsonSerializer.Serialize(new HardwareServiceRequest
                    {
                        Id = id,
                        Method = "shutdown",
                        Payload = JsonSerializer.SerializeToElement(new { }, JsonOptions),
                    }, JsonOptions));
                }
            }
            catch { }
            _disposed = true;
            try { _writer.Dispose(); } catch { }
            try { _reader.Dispose(); } catch { }
            try { _pipe.Dispose(); } catch { }
            try { if (!_process.HasExited) _process.WaitForExit(1000); } catch { }
            try { _process.Dispose(); } catch { }
            try { _fallback?.Dispose(); } catch { }
        }
    }

    private void EnsureFallbackIfServiceExited()
    {
        if (_fallback != null || _disposed) return;
        try
        {
            if (!_process.HasExited) return;
            _fallback = new HardwareAccessCoordinator();
            Logger.Warn("Hardware service exited; continuing with in-process monitoring.");
        }
        catch { }
    }

    private sealed class HardwareServiceRequest
    {
        public string Id { get; set; } = "";
        public string Method { get; set; } = "";
        public JsonElement Payload { get; set; }
    }

    private sealed class HardwareServiceResponse
    {
        public string Id { get; set; } = "";
        public bool Ok { get; set; }
        public JsonElement? Result { get; set; }
        public string? Error { get; set; }
    }

    private sealed class HardwareServicePing { public bool Ready { get; set; } }
    private sealed class HardwareReadEnvelope
    {
        public bool Available { get; set; }
        public SensorReport? Report { get; set; }
    }
}
''')


# 7. Make the isolated hardware service sensor-only.
write("src/VoltManager.HardwareService/Program.cs", r'''using System.Diagnostics;
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
            "read" => hardware.Read(payload.TryGetProperty("force", out JsonElement force) && force.ValueKind == JsonValueKind.True),
            "invalidate" => hardware.Invalidate(),
            "shutdown" => hardware.Shutdown(),
            _ => throw new InvalidOperationException("Unknown hardware service method: " + request.Method),
        };
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
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
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

    public object Invalidate()
    {
        lock (_gate) _lastUpdateUtc = DateTime.MinValue;
        return new { success = true };
    }

    public object Shutdown() => new { success = true };

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

    private void RefreshLocked(bool force)
    {
        if (_computer == null) return;
        if (!force && DateTime.UtcNow - _lastUpdateUtc < UpdateInterval) return;
        _lastUpdateUtc = DateTime.UtcNow;
        var readings = new List<SensorReadingDto>();
        try
        {
            foreach (IHardware hardware in _computer.Hardware)
            {
                hardware.Update();
                Collect(hardware, readings);
                foreach (IHardware sub in hardware.SubHardware)
                {
                    sub.Update();
                    Collect(sub, readings);
                }
            }
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

    private static void Collect(IHardware hardware, List<SensorReadingDto> readings)
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
                SensorType.Temperature => "temp",
                SensorType.Clock => "clock",
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
''')


# 8. Clean monitoring facade comments that referred to the removed subsystem.
sensor_path = "src/VoltManager/Services/HardwareSensorProvider.cs"
sensor = read(sensor_path)
sensor = sensor.replace(
    "/// standalone it owns a coordinator; the application injects one shared instance\n/// so monitoring and fan control never open competing LibreHardwareMonitor sessions.",
    "/// standalone it owns a coordinator; the application injects one shared instance\n/// so monitoring reuses the same LibreHardwareMonitor session.",
)
sensor = sensor.replace(
    "// data. 0 RPM stays because a stopped fan can be a valid live reading.\n",
    "// data.\n",
)
write(sensor_path, sensor)


# 9. Remove feature entry points and stylesheet from the document.
index_path = "src/VoltManager/wwwroot/index.html"
index = read(index_path)
index = "\n".join(
    line for line in index.splitlines()
    if "css/fans.css" not in line
    and "js/fan-visualizer.js" not in line
    and "js/fans.js" not in line
) + "\n"
index = index.replace("changelog.js?v=fans1", "changelog.js?v=changelog1")
write(index_path, index)


# 10. Remove the dynamic view and its sidebar navigation item.
layout_path = "src/VoltManager/wwwroot/js/ui-reorganization.layout.js"
layout = read(layout_path)
start = layout.find("    function cooling() {")
end = layout.find("    function powerPlans() {", start)
if start < 0 or end < 0:
    raise RuntimeError("Cooling view function was not found")
layout = layout[:start] + layout[end:]
layout = layout.replace(
    "[overview(), monitoring(), cooling(), powerPlans(), automations(), systemTools(), widgets(), settings()]",
    "[overview(), monitoring(), powerPlans(), automations(), systemTools(), widgets(), settings()]",
)
layout = layout.replace(
    "${item('overview', 'dashboard', 'nav_overview')}${item('monitoring', 'monitoring', 'nav_monitoring')}${item('cooling', 'mode_fan', 'nav_cooling')}",
    "${item('overview', 'dashboard', 'nav_overview')}${item('monitoring', 'monitoring', 'nav_monitoring')}",
)
write(layout_path, layout)


# 11. Remove all localized labels/titles for the deleted view.
i18n_path = "src/VoltManager/wwwroot/js/ui-reorganization.i18n.js"
i18n = read(i18n_path)
i18n = "\n".join(
    line for line in i18n.splitlines()
    if "nav_cooling:" not in line
    and "cooling_title:" not in line
    and "cooling_subtitle:" not in line
) + "\n"
write(i18n_path, i18n)


# 12. Keep unrelated lazy-loading regressions; remove deleted feature assertions.
write("tests/lazy-feature-loading.test.mjs", r'''import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function text(path) {
  return readFileSync(new URL('../' + path, import.meta.url), 'utf8');
}

const tipsLoader = text('src/VoltManager/wwwroot/js/tips.js');
const tipsFeature = text('src/VoltManager/wwwroot/js/tips.feature.js');
const tourLoader = text('src/VoltManager/wwwroot/js/tour.js');
const tourFeature = text('src/VoltManager/wwwroot/js/tour.feature.js');

test('Energy Tips implementation loads only from its first-use entry point', () => {
  assert.match(tipsLoader, /btn-energy-tips/);
  assert.match(tipsLoader, /tips\.feature\.js/);
  assert.doesNotMatch(tipsLoader, /tip1_title/);
  assert.match(tipsFeature, /tip1_title/);
});

test('Guided Tour preserves automatic first-run and manual replay triggers lazily', () => {
  assert.match(tourLoader, /welcomecompleted/);
  assert.match(tourLoader, /settingsloaded/);
  assert.match(tourLoader, /btn-show-tour/);
  assert.match(tourLoader, /tour\.feature\.js/);
  assert.doesNotMatch(tourLoader, /tour_intro_title/);
  assert.match(tourFeature, /tour_intro_title/);
});

test('lazy entry points remain materially smaller than feature implementations', () => {
  assert.ok(tipsLoader.length < tipsFeature.length / 3);
  assert.ok(tourLoader.length < tourFeature.length / 5);
});
''')

write("tests/VoltManager.Tests/HardwareServiceClientTests.cs", r'''using VoltManager.Services;
using Xunit;

namespace VoltManager.Tests;

public class HardwareServiceClientTests
{
    [Fact]
    public void Client_bootstraps_named_pipe_service_for_isolated_monitoring()
    {
        using HardwareServiceClient? client = HardwareServiceClient.TryStart();
        Assert.NotNull(client);
    }
}
''')


# 13. Remove historical/documentation lines dedicated to the removed feature.
residue = re.compile(r"\b(?:fans?|cooling)\b|ventol|raffredd", re.IGNORECASE)
for raw in ["README.md", "CHANGELOG.md"]:
    path = Path(raw)
    if not path.exists():
        continue
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    path.write_text(
        "\n".join(line for line in lines if not residue.search(line)) + "\n",
        encoding="utf-8",
        newline="\n",
    )
