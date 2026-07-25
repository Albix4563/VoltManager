using System;
using System.Management;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Calcola lo stato di salute (usura) della batteria confrontando la capacità
/// progettata col massimo attuale a piena carica. Lettura via WMI root\WMI;
/// </summary>
public sealed class BatteryHealthService
{
    private readonly Func<BatteryCapacitySnapshot?> _reader;

    public BatteryHealthService(Func<BatteryCapacitySnapshot?>? reader = null)
    {
        _reader = reader ?? ReadFromWmi;
    }

    public BatteryHealthState GetHealth() => Compute(_reader());

    /// <summary>
    /// Logica pura: snapshot capacità → stato salute. Nessun I/O.
    /// </summary>
    public static BatteryHealthState Compute(BatteryCapacitySnapshot? snapshot)
    {
        if (snapshot == null)
            return new BatteryHealthState { Available = false, Rating = "unknown", Message = "no_battery" };

        var design = snapshot.DesignedCapacityMwh;
        var full = snapshot.FullChargedCapacityMwh;

        // Servono entrambe le capacità e una capacità progettata > 0.
        if (design is not > 0 || full is not >= 0)
        {
            return new BatteryHealthState
            {
                Available = false,
                DesignedCapacityMwh = design,
                FullChargedCapacityMwh = full,
                Rating = "unknown",
                Message = "capacity_unreadable",
            };
        }

        // Alcuni firmware riportano full > design dopo una calibrazione: clamp a 100% salute.
        double healthRaw = (double)full.Value / design.Value * 100.0;
        double health = Math.Clamp(Math.Round(healthRaw, 1), 0.0, 100.0);
        double wear = Math.Round(100.0 - health, 1);

        return new BatteryHealthState
        {
            Available = true,
            DesignedCapacityMwh = design,
            FullChargedCapacityMwh = full,
            HealthPercent = health,
            WearPercent = wear,
            Rating = RatingFor(health),
            Message = "ok",
        };
    }

    private static string RatingFor(double health) => health switch
    {
        >= 90 => "excellent",
        >= 80 => "good",
        >= 60 => "fair",
        _ => "poor",
    };

    private static BatteryCapacitySnapshot? ReadFromWmi()
    {
        try
        {
            int? designed = QueryFirst("SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
            int? full = QueryFirst("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");

            if (designed == null && full == null)
                return null;

            return new BatteryCapacitySnapshot
            {
                DesignedCapacityMwh = designed,
                FullChargedCapacityMwh = full,
            };
        }
        catch
        {
            // Desktop senza batteria o WMI bloccato: nessun dato.
            return null;
        }
    }

    private static int? QueryFirst(string query, string property)
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", query);
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                var value = obj[property];
                if (value != null && uint.TryParse(value.ToString(), out var parsed) && parsed > 0)
                    return (int)Math.Min(parsed, int.MaxValue);
            }
        }
        return null;
    }
}
