using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VoltManager.Services;

/// <summary>Named-pipe client for the isolated hardware sensor process.</summary>
public sealed class HardwareServiceClient : IHardwareAccess
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(45);
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
    private int _consecutiveRpcFailures;
    private const int MaxConsecutiveRpcFailures = 3;
    private bool _disposed;
    private long _nextId;
    private DateTime _lastReadUtc = DateTime.MinValue;
    private HardwareSampleRequest _lastRequest;

    public bool Available => !_disposed && ((_pipe.IsConnected && _hardwareAvailable) || (_fallback?.Available ?? false));
    internal long RequestCount => Interlocked.Read(ref _nextId);
    internal bool UsingFallback => _fallback != null;
    internal int? ServiceProcessId
    {
        get
        {
            lock (_gate)
            {
                try { return _process.HasExited ? null : _process.Id; }
                catch { return null; }
            }
        }
    }

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
            var initWatch = Stopwatch.StartNew();
            while (true)
            {
                var pong = client.Call<HardwareServicePing>("ping", null);
                if (pong?.Ready == true)
                    break;
                if (pong?.Failed == true || client.ServiceGone)
                {
                    client.EnableFallback("Hardware service initialization failed; continuing with in-process monitoring.");
                    return client;
                }
                if (initWatch.Elapsed >= InitializationTimeout)
                {
                    client.EnableFallback("Hardware service initialization timed out; continuing with in-process monitoring.");
                    return client;
                }
                Thread.Sleep(100);
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

    public SensorReport Read(bool force = false) => Read(HardwareSampleRequest.Full, force);

    public SensorReport Read(HardwareSampleRequest request, bool force = false)
    {
        lock (_gate)
        {
            if (_disposed) return _last;

            DateTime nowUtc = DateTime.UtcNow;
            if (!force && IsReadFresh(_lastReadUtc, nowUtc, _lastRequest, request)) return _last;
            if (_fallback != null)
            {
                _last = _fallback.Read(request, force);
                _lastReadUtc = nowUtc;
                _lastRequest = request;
                return _last;
            }

            HardwareReadEnvelope? envelope = Call<HardwareReadEnvelope>("read", new
            {
                force,
                temperatures = request.Temperatures,
                visualDetails = request.VisualDetails,
                minimumIntervalMs = Math.Max(0, (long)request.MinimumInterval.TotalMilliseconds),
            });
            if (envelope == null)
            {
                EnsureFallbackIfServiceUnusable();
                if (_fallback != null)
                {
                    _last = _fallback.Read(request, force);
                    _lastReadUtc = nowUtc;
                    _lastRequest = request;
                }
                return _last;
            }

            _rpcFaulted = false;
            _hardwareAvailable = envelope.Available;
            if (envelope.Report != null) _last = envelope.Report;
            _lastReadUtc = nowUtc;
            _lastRequest = request;
            return _last;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _lastReadUtc = DateTime.MinValue;
            _lastRequest = default;
            // Once in fallback the service is gone or hung: an RPC would only stall resume.
            if (_fallback == null) _ = Call<object>("invalidate", null);
            _fallback?.Invalidate();
            _last = SensorReport.Empty;
        }
    }

    internal static bool IsReadFresh(
        DateTime lastReadUtc,
        DateTime nowUtc,
        HardwareSampleRequest lastRequest,
        HardwareSampleRequest requested)
        => lastReadUtc != DateTime.MinValue
            && lastRequest.Covers(requested)
            && nowUtc - lastReadUtc < requested.MinimumInterval;

    private T? Call<T>(string method, object? payload)
    {
        lock (_gate)
        {
            if (_disposed || !_pipe.IsConnected) return default;
            try
            {
                if (string.Equals(method, "read", StringComparison.Ordinal))
                    ValidationMetrics.Increment(ValidationCounter.HardwareRpcReads);
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
                HardwareServiceResponse? response;
                while (true)
                {
                    string? line = _reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                    if (line == null) throw new EndOfStreamException("Hardware service pipe closed.");
                    response = JsonSerializer.Deserialize<HardwareServiceResponse>(line, JsonOptions);
                    // A reply that arrived after its caller timed out is still in the pipe:
                    // drop it instead of failing every later call on an id mismatch.
                    if (response != null && IsStaleResponseId(response.Id, id)) continue;
                    break;
                }
                if (response == null || response.Id != id) throw new InvalidDataException("Hardware service returned an invalid response.");
                if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Hardware service request failed.");
                _rpcFaulted = false;
                _consecutiveRpcFailures = 0;
                if (!response.Result.HasValue || response.Result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
                return response.Result.Value.Deserialize<T>(JsonOptions);
            }
            catch (Exception ex)
            {
                _hardwareAvailable = false;
                _consecutiveRpcFailures++;
                _rpcFaulted = Logger.WarnOnce(_rpcFaulted, "Hardware service RPC failed", ex);
                return default;
            }
        }
    }

    internal static bool IsStaleResponseId(string? responseId, string requestId)
        => long.TryParse(responseId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long response)
           && long.TryParse(requestId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long request)
           && response < request;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            WriteShutdownRequest();
            _disposed = true;
            try { _writer.Dispose(); } catch { }
            try { _reader.Dispose(); } catch { }
            try { _pipe.Dispose(); } catch { }
            try { if (!_process.HasExited) _process.WaitForExit(1000); } catch { }
            try { _process.Dispose(); } catch { }
            try { _fallback?.Dispose(); } catch { }
        }
    }

    private void EnsureFallbackIfServiceUnusable()
    {
        if (_fallback != null || _disposed) return;
        try
        {
            if (_process.HasExited)
                EnableFallback("Hardware service exited; continuing with in-process monitoring.");
            // Call() returns early without counting a failure once the pipe is gone.
            else if (!_pipe.IsConnected)
                EnableFallback("Hardware service pipe disconnected; continuing with in-process monitoring.");
            // A live but hung/broken service would otherwise leave readings stale forever.
            else if (_consecutiveRpcFailures >= MaxConsecutiveRpcFailures)
                EnableFallback("Hardware service stopped answering; continuing with in-process monitoring.");
        }
        catch { }
    }

    // True when the service process died or the pipe dropped: pinging it again is pointless.
    private bool ServiceGone
    {
        get
        {
            try { return _process.HasExited || !_pipe.IsConnected; }
            catch { return true; }
        }
    }

    private void EnableFallback(string message)
    {
        if (_fallback != null || _disposed) return;
        Logger.Warn(message);
        StopServiceProcess();
        _fallback = new HardwareAccessCoordinator();
        _hardwareAvailable = false;
    }

    // The service may be hung: never wait on an RPC reply here. Ask it to exit, then make
    // sure it does not keep its own hardware driver session alive next to the fallback.
    private void StopServiceProcess()
    {
        WriteShutdownRequest();
        try
        {
            if (!_process.HasExited && !_process.WaitForExit(1000))
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not stop hardware service: " + ex.Message);
        }
    }

    private void WriteShutdownRequest()
    {
        try
        {
            if (!_pipe.IsConnected) return;
            string id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _writer.WriteLine(JsonSerializer.Serialize(new HardwareServiceRequest
            {
                Id = id,
                Method = "shutdown",
                Payload = JsonSerializer.SerializeToElement(new { }, JsonOptions),
            }, JsonOptions));
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

    private sealed class HardwareServicePing
    {
        public bool Ready { get; set; }
        public bool Failed { get; set; }
    }
    private sealed class HardwareReadEnvelope
    {
        public bool Available { get; set; }
        public SensorReport? Report { get; set; }
    }
}
