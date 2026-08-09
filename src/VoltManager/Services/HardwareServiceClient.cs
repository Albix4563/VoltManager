using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VoltManager.Services;

/// <summary>
/// Named-pipe client for the isolated hardware process. The child process owns
/// LibreHardwareMonitor and restores every software-owned fan channel if this
/// parent process exits unexpectedly.
/// </summary>
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
    public bool ControlWritesAllowed => !_disposed && !_rpcFaulted && _fallback == null && _pipe.IsConnected && !_process.HasExited;

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
            Logger.Warn("Hardware service executable not found; fan control will remain read-only.");
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
            Logger.Warn("Hardware service unavailable; fan control will remain read-only: " + ex.Message);
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
            EnsureReadOnlyFallbackIfServiceExited();
            if (_fallback != null) _last = _fallback.Read(force);
            return _last;
        }
        _rpcFaulted = false;
        _hardwareAvailable = envelope.Available;
        if (envelope.Report != null) _last = envelope.Report;
        return _last;
    }

    public HardwareFanControlDescriptor? GetFanControl(string controlIdentifier) =>
        Call<HardwareFanControlDescriptor>("getFanControl", new { controlIdentifier });

    public HardwareFanControlResult SetFanSoftware(string controlIdentifier, double percent) =>
        Call<HardwareFanControlResult>("setFanSoftware", new { controlIdentifier, percent })
        ?? HardwareFanControlResult.Fail("hardware_service_unavailable", "The hardware service did not return a fan-control result.");

    public HardwareFanControlResult RestoreFanDefault(string controlIdentifier) =>
        Call<HardwareFanControlResult>("restoreFanDefault", new { controlIdentifier })
        ?? HardwareFanControlResult.Fail("hardware_service_unavailable", "The hardware service did not return a restore result.");

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
            try
            {
                if (!_process.HasExited) _process.WaitForExit(1000);
            }
            catch { }
            try { _process.Dispose(); } catch { }
            try { _fallback?.Dispose(); } catch { }
        }
    }

    private void EnsureReadOnlyFallbackIfServiceExited()
    {
        if (_fallback != null || _disposed) return;
        try
        {
            if (!_process.HasExited) return;
            _fallback = new HardwareAccessCoordinator(controlWritesAllowed: false);
            Logger.Warn("Hardware service exited; continuing with read-only in-process monitoring.");
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
