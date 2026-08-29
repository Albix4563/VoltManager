using LibreHardwareMonitor.Hardware;
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

/// <summary>Keeps slow hardware discovery off the UI startup path.</summary>
internal sealed class DeferredHardwareAccess : IHardwareAccess
{
    private readonly Task<IHardwareAccess> _access;
    private int _disposed;

    public DeferredHardwareAccess(Func<IHardwareAccess> factory)
    {
        _access = Task.Run(() =>
        {
            try { return factory(); }
            catch (Exception ex)
            {
                Logger.Warn("Deferred hardware initialization failed; using in-process monitoring: " + ex.Message);
                return new HardwareAccessCoordinator();
            }
        });
    }

    public bool Available =>
        Volatile.Read(ref _disposed) == 0 &&
        _access.IsCompletedSuccessfully &&
        _access.Result.Available;

    public SensorReport Read(bool force = false) =>
        Volatile.Read(ref _disposed) == 0 && _access.IsCompletedSuccessfully
            ? _access.Result.Read(force)
            : SensorReport.Empty;

    public void Invalidate()
    {
        if (Volatile.Read(ref _disposed) == 0 && _access.IsCompletedSuccessfully)
            _access.Result.Invalidate();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_access.IsCompletedSuccessfully)
        {
            _access.Result.Dispose();
            return;
        }

        _ = _access.ContinueWith(
            task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
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
