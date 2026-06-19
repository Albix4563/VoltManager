using System;
using System.Management;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Calcola il flusso energetico istantaneo della batteria (potenza in carica/scarica
/// e tempo stimato rimanente) leggendo BatteryStatus + BatteryFullChargedCapacity da
/// WMI root\WMI. Il reader è iniettabile per i test sintetici.
/// </summary>
public sealed class PowerFlowService
{
    private readonly Func<BatteryPowerSnapshot?> _reader;

    public PowerFlowService(Func<BatteryPowerSnapshot?>? reader = null)
    {
        _reader = reader ?? ReadFromWmi;
    }

    public BatteryPowerState GetState() => Compute(_reader());

    /// <summary>
    /// Logica pura: snapshot firmware → stato del flusso energetico. Nessun I/O (testabile).
    /// </summary>
    public static BatteryPowerState Compute(BatteryPowerSnapshot? s)
    {
        if (s == null)
            return new BatteryPowerState { Available = false, Status = "unknown", TimeKind = "none", Message = "no_battery" };

        int? remaining = s.RemainingCapacityMwh;
        int? full = s.FullChargedCapacityMwh;

        int? percent = (remaining is >= 0 && full is > 0)
            ? (int)Math.Clamp(Math.Round((double)remaining.Value / full.Value * 100.0), 0, 100)
            : null;

        double? voltage = s.VoltageMv is > 0
            ? Math.Round(s.VoltageMv.Value / 1000.0, 2)
            : null;

        // Le rate firmware possono arrivare con segno: la magnitudine è ciò che conta.
        int charge = Math.Abs(s.ChargeRateMw ?? 0);
        int discharge = Math.Abs(s.DischargeRateMw ?? 0);

        string status;
        double? powerWatts;
        int? minutes = null;
        string timeKind = "none";

        if (s.Discharging && discharge > 0)
        {
            status = "discharging";
            powerWatts = -Math.Round(discharge / 1000.0, 1);
            if (remaining is > 0)
            {
                minutes = (int)Math.Round((double)remaining.Value / discharge * 60.0);
                timeKind = "toEmpty";
            }
        }
        else if (s.Charging && charge > 0)
        {
            status = "charging";
            powerWatts = Math.Round(charge / 1000.0, 1);
            if (remaining is >= 0 && full is > 0 && full.Value > remaining.Value)
            {
                minutes = (int)Math.Round((double)(full.Value - remaining.Value) / charge * 60.0);
                timeKind = "toFull";
            }
        }
        else
        {
            // Nessuna corrente attiva: collegato a piena carica oppure semplicemente a riposo.
            powerWatts = 0.0;
            status = s.PowerOnline && percent is >= 99 ? "full" : "idle";
        }

        return new BatteryPowerState
        {
            Available = true,
            OnAc = s.PowerOnline,
            Status = status,
            PowerWatts = powerWatts,
            BatteryPercent = percent,
            RemainingCapacityMwh = remaining,
            FullChargedCapacityMwh = full,
            VoltageVolts = voltage,
            MinutesRemaining = minutes,
            TimeKind = timeKind,
            Message = "ok",
        };
    }

    private static BatteryPowerSnapshot? ReadFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    return new BatteryPowerSnapshot
                    {
                        PowerOnline = ToBool(obj["PowerOnline"]),
                        Charging = ToBool(obj["Charging"]),
                        Discharging = ToBool(obj["Discharging"]),
                        ChargeRateMw = ToInt(obj["ChargeRate"]),
                        DischargeRateMw = ToInt(obj["DischargeRate"]),
                        RemainingCapacityMwh = ToInt(obj["RemainingCapacity"]),
                        VoltageMv = ToInt(obj["Voltage"]),
                        FullChargedCapacityMwh = ReadFullChargedCapacity(),
                    };
                }
            }
            return null;
        }
        catch
        {
            // Desktop senza batteria o WMI bloccato: nessun dato.
            return null;
        }
    }

    private static int? ReadFullChargedCapacity()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI",
                "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    int? value = ToInt(obj["FullChargedCapacity"]);
                    if (value is > 0) return value;
                }
            }
        }
        catch
        {
            // Lettura best-effort: l'assenza degrada solo la stima del tempo-a-pieno.
        }
        return null;
    }

    private static int? ToInt(object? v)
    {
        if (v == null) return null;
        try { return Convert.ToInt32(v); }
        catch { return null; }
    }

    private static bool ToBool(object? v)
    {
        try { return v != null && Convert.ToBoolean(v); }
        catch { return false; }
    }
}
