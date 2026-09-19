using System.Text.Json.Serialization;

namespace VoltManager.Models;

/// <summary>
/// Capacità batteria lette dal firmware (mWh). Null = nessuna batteria/non leggibile.
/// </summary>
public record BatteryCapacitySnapshot
{
    [JsonPropertyName("designedCapacityMwh")] public int? DesignedCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
}

/// <summary>
/// Stato di salute (usura) della batteria calcolato da capacità progettata vs attuale.
/// </summary>
public record BatteryHealthState
{
    // available=false quando non c'è batteria o i dati firmware sono assenti/invalidi.
    [JsonPropertyName("available")] public bool Available { get; init; }
    [JsonPropertyName("designedCapacityMwh")] public int? DesignedCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
    [JsonPropertyName("healthPercent")] public double? HealthPercent { get; init; }
    [JsonPropertyName("wearPercent")] public double? WearPercent { get; init; }
    // excellent|good|fair|poor|unknown
    [JsonPropertyName("rating")] public string Rating { get; init; } = "unknown";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>
/// Lettura istantanea del flusso energetico della batteria dal firmware (WMI BatteryStatus).
/// Tutte le grandezze in unità firmware (mW, mWh, mV). Null = dato assente/non leggibile.
/// </summary>
public record BatteryPowerSnapshot
{
    [JsonPropertyName("powerOnline")] public bool PowerOnline { get; init; }     // alimentatore collegato
    [JsonPropertyName("charging")] public bool Charging { get; init; }
    [JsonPropertyName("discharging")] public bool Discharging { get; init; }
    [JsonPropertyName("chargeRateMw")] public int? ChargeRateMw { get; init; }
    [JsonPropertyName("dischargeRateMw")] public int? DischargeRateMw { get; init; }
    [JsonPropertyName("remainingCapacityMwh")] public int? RemainingCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
    [JsonPropertyName("voltageMv")] public int? VoltageMv { get; init; }
}

/// <summary>
/// Stato del flusso energetico della batteria: potenza in carica/scarica (W),
/// percentuale e stima del tempo rimanente (a vuoto o a piena carica).
/// </summary>
public record BatteryPowerState
{
    // available=false quando non c'è batteria o i dati firmware sono assenti.
    [JsonPropertyName("available")] public bool Available { get; init; }
    [JsonPropertyName("onAc")] public bool OnAc { get; init; }
    // charging|discharging|idle|full|unknown
    [JsonPropertyName("status")] public string Status { get; init; } = "unknown";
    // Potenza con segno: positiva in carica, negativa in scarica (W).
    // Dopo smoothing host: valore stabilizzato (EMA ± mediana storia); istantaneo in InstantPowerWatts.
    [JsonPropertyName("powerWatts")] public double? PowerWatts { get; init; }
    // Lettura firmware grezza prima dello smoothing (null se non applicato).
    [JsonPropertyName("instantPowerWatts")] public double? InstantPowerWatts { get; init; }
    [JsonPropertyName("batteryPercent")] public int? BatteryPercent { get; init; }
    [JsonPropertyName("remainingCapacityMwh")] public int? RemainingCapacityMwh { get; init; }
    [JsonPropertyName("fullChargedCapacityMwh")] public int? FullChargedCapacityMwh { get; init; }
    [JsonPropertyName("voltageVolts")] public double? VoltageVolts { get; init; }
    // Minuti stimati al traguardo indicato da timeKind.
    [JsonPropertyName("minutesRemaining")] public int? MinutesRemaining { get; init; }
    // toEmpty|toFull|none
    [JsonPropertyName("timeKind")] public string TimeKind { get; init; } = "none";
    // true se potenza/tempo derivano da EMA e/o mediana storica (meno jitter).
    [JsonPropertyName("estimateStable")] public bool EstimateStable { get; init; }
    // Wh scaricati nella sessione a batteria corrente (null se non calcolabile).
    [JsonPropertyName("sessionWh")] public double? SessionWh { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>
/// Campione storico della batteria: istante (epoch secondi UTC), percentuale di carica,
/// potenza con segno (W, + in carica / - in scarica), stato AC e temperatura (°C, opzionale).
/// Chiavi JSON brevi per contenere la dimensione del file di cronologia.
/// </summary>
public record BatteryHistorySample
{
    [JsonPropertyName("t")] public long T { get; init; }
    [JsonPropertyName("pct")] public int? Pct { get; init; }
    [JsonPropertyName("w")] public double? W { get; init; }
    [JsonPropertyName("ac")] public bool Ac { get; init; }
    [JsonPropertyName("temp")] public double? Temp { get; init; }
}
