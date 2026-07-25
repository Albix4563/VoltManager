using System.IO;
using System.Text.Json;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Registra una cronologia campionata della batteria (%, potenza con segno, AC, temperatura)
/// in un ring buffer persistito su disco. Il throttle fra i campioni permette al loop di
/// chiamare Record() a ogni tick senza gonfiare il file. La logica di accodamento (Append)
/// </summary>
public sealed class BatteryHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly int _capacity;
    private readonly TimeSpan _minInterval;
    private readonly object _lock = new();
    private readonly List<BatteryHistorySample> _samples;

    /// <param name="capacity">Numero massimo di campioni mantenuti (default 2880 ≈ 48h a 1/min).</param>
    /// <param name="minInterval">Intervallo minimo fra due campioni accettati (default 1 min).</param>
    public BatteryHistoryService(string? path = null, int capacity = 2880, TimeSpan? minInterval = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager", "battery-history.json");
        _capacity = Math.Max(16, capacity);
        _minInterval = minInterval ?? TimeSpan.FromMinutes(1);
        _samples = Load();
    }

    /// <summary>Campioni in ordine cronologico (dal più vecchio al più recente).</summary>
    public IReadOnlyList<BatteryHistorySample> GetHistory()
    {
        lock (_lock) return _samples.ToList();
    }

    /// <summary>
    /// Converte lo stato del flusso energetico in un campione e lo accoda se è trascorso
    /// abbastanza tempo dall'ultimo. Ritorna true se il campione è stato registrato.
    /// Stato assente/senza batteria → nessuna registrazione.
    /// </summary>
    public bool Record(BatteryPowerState? state, double? temp, DateTime nowUtc)
    {
        if (state is not { Available: true }) return false;

        var sample = new BatteryHistorySample
        {
            T = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
            Pct = state.BatteryPercent,
            W = state.PowerWatts,
            Ac = state.OnAc,
            Temp = temp is > 0 ? Math.Round(temp.Value, 1) : null,
        };

        lock (_lock)
        {
            if (!Append(_samples, sample, _capacity, _minInterval)) return false;
            Persist();
        }
        return true;
    }

    /// <summary>
    /// Logica pura di accodamento: rispetta il throttle rispetto all'ultimo
    /// timestamp e mantiene il buffer entro <paramref name="capacity"/> scartando i più
    /// vecchi. Muta <paramref name="samples"/> in place. Ritorna true se ha accodato.
    /// </summary>
    public static bool Append(List<BatteryHistorySample> samples, BatteryHistorySample sample,
        int capacity, TimeSpan minInterval)
    {
        if (samples.Count > 0)
        {
            var last = samples[^1];
            if (sample.T - last.T < (long)minInterval.TotalSeconds) return false;
        }

        samples.Add(sample);

        int overflow = samples.Count - capacity;
        if (overflow > 0) samples.RemoveRange(0, overflow);
        return true;
    }

    private List<BatteryHistorySample> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<List<BatteryHistorySample>>(json, JsonOpts);
                if (loaded != null)
                {
                    if (loaded.Count > _capacity) loaded.RemoveRange(0, loaded.Count - _capacity);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // File corrotto/illeggibile: riparti vuoto, la cronologia non è critica.
            Logger.Error("Failed to load battery history from " + _path + "; starting empty.", ex);
        }
        return new List<BatteryHistorySample>();
    }

    private void Persist()
    {
        string tmp = _path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(_samples, JsonOpts));
            // Replace in one filesystem operation. Never delete the last valid history
            // before the replacement is ready, so a crash cannot create an empty gap.
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Scrittura best-effort: un disco pieno non deve far crashare il loop.
            Logger.Error("Failed to persist battery history to " + _path, ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
