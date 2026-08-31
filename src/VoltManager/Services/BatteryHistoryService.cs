using System.IO;
using System.Globalization;
using System.Text;
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

    public static IReadOnlyList<BatteryHistorySample> SelectWindow(
        IReadOnlyList<BatteryHistorySample> samples,
        DateTime nowUtc,
        int hours,
        int maxPoints = 192)
    {
        hours = Math.Clamp(hours, 1, 48);
        maxPoints = Math.Max(2, maxPoints);
        long cutoff = new DateTimeOffset(nowUtc, TimeSpan.Zero).AddHours(-hours).ToUnixTimeSeconds();
        var filtered = samples.Where(s => s.T >= cutoff).ToList();
        if (filtered.Count <= maxPoints) return filtered;

        var slim = new List<BatteryHistorySample>(maxPoints);
        int last = filtered.Count - 1;
        for (int i = 0; i < maxPoints; i++)
            slim.Add(filtered[(int)Math.Round(i * (double)last / (maxPoints - 1))]);
        return slim;
    }

    public static string ToCsv(IEnumerable<BatteryHistorySample> samples)
    {
        var sb = new StringBuilder("timestamp_utc,battery_percent,watts,on_ac,temperature_c\r\n");
        foreach (var sample in samples)
        {
            string timestamp = DateTimeOffset.FromUnixTimeSeconds(sample.T).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            sb.Append(timestamp).Append(',')
                .Append(sample.Pct?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(sample.W?.ToString("0.###", CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(sample.Ac ? "true" : "false").Append(',')
                .Append(sample.Temp?.ToString("0.0", CultureInfo.InvariantCulture) ?? "")
                .Append("\r\n");
        }
        return sb.ToString();
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
