using System.Text.RegularExpressions;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Reads and writes Windows power-plan settings through powercfg. All reads use
/// GUIDs and locale-independent hexadecimal indexes so localized Windows output
/// does not affect parsing.
/// </summary>
public class PowerPlanParameterService
{
    // Sub-group GUIDs.
    private const string SubDisk       = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string SubSleep      = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string SubProcessor  = "54533251-82be-4824-96c1-47b60b740d00";
    private const string SubDisplay    = "7516b95f-f776-4464-8c53-06167f40cc99";

    // Standard display/sleep settings.
    private const string SettingDisplayIdle = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
    private const string SettingSleepIdle   = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";

    // Processor / device settings.
    private const string SettingProcMin        = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string SettingProcMax        = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string SettingBoost          = "be337238-0d82-4146-a960-4f3749d470c7";
    private const string SettingProcessorEpp   = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string SettingCoreParkingMin = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string SettingPcieLs         = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string SettingDiskIdle       = "6738e2c4-e8a5-4a42-b16a-e040e769756e";
    private const string SettingWakeTimers     = "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d";

    // powercfg /qh prints several hexadecimal values (min/max/step/current AC/DC).
    // The final two 0x... values are the current AC and DC indexes on every
    // supported locale because the numeric representation itself is invariant.
    private static readonly Regex HexIndexRegex = new(
        @"0x([0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly PowerPlanService _power;

    public PowerPlanParameterService(PowerPlanService power)
    {
        _power = power;
    }

    /// <summary>Returns all selectable power plans reported by Windows.</summary>
    public List<PowerPlan> ListPlans() => _power.ListPlans();

    /// <summary>
    /// Reads display-off and system-sleep inactivity timeouts for the selected
    /// plan. Values are seconds; zero means never.
    /// </summary>
    public PowerPlanTimeoutSet GetPlanTimeouts(string? planGuid = null)
    {
        try
        {
            string guid = ResolvePlanGuid(planGuid);
            var display = QueryIndexes(guid, SubDisplay, SettingDisplayIdle);
            var sleep = QueryIndexes(guid, SubSleep, SettingSleepIdle);

            if (!display.Supported || !sleep.Supported)
                throw new InvalidOperationException("Windows non ha restituito i timeout del piano selezionato.");

            return new PowerPlanTimeoutSet
            {
                PlanGuid = guid,
                PlanName = GetPlanName(guid),
                DisplayTimeoutAc = Math.Max(0, display.Ac),
                DisplayTimeoutDc = Math.Max(0, display.Dc),
                SleepTimeoutAc = Math.Max(0, sleep.Ac),
                SleepTimeoutDc = Math.Max(0, sleep.Dc),
            };
        }
        catch (Exception ex)
        {
            return new PowerPlanTimeoutSet { Error = ex.Message };
        }
    }

    /// <summary>
    /// Reads advanced parameters for the given plan GUID, or the active plan when
    /// no GUID is supplied. Hidden settings are queried with /qh without changing
    /// their visibility in the Windows Control Panel.
    /// </summary>
    public PlanParameterSet GetPlanParameters(string? planGuid = null)
    {
        try
        {
            string guid = ResolvePlanGuid(planGuid);

            var procMin = QueryIndexes(guid, SubProcessor, SettingProcMin);
            var procMax = QueryIndexes(guid, SubProcessor, SettingProcMax);
            var boost = QueryIndexes(guid, SubProcessor, SettingBoost);
            var pcie = QueryIndexes(guid, SubPciExpress, SettingPcieLs);
            var epp = QueryIndexes(guid, SubProcessor, SettingProcessorEpp);
            var coreParkingMin = QueryIndexes(guid, SubProcessor, SettingCoreParkingMin);
            var diskIdle = QueryIndexes(guid, SubDisk, SettingDiskIdle);
            var wakeTimers = QueryIndexes(guid, SubSleep, SettingWakeTimers);

            return new PlanParameterSet
            {
                PlanGuid = guid,
                PlanName = GetPlanName(guid),
                ProcessorMinAc = ClampOrFallback(procMin, true, 5, 0, 100),
                ProcessorMinDc = ClampOrFallback(procMin, false, 5, 0, 100),
                ProcessorMaxAc = ClampOrFallback(procMax, true, 100, 0, 100),
                ProcessorMaxDc = ClampOrFallback(procMax, false, 100, 0, 100),
                BoostModeAc = ClampOrFallback(boost, true, 2, 0, 6),
                BoostModeDc = ClampOrFallback(boost, false, 2, 0, 6),
                PcieLinkStateAc = ClampOrFallback(pcie, true, 0, 0, 2),
                PcieLinkStateDc = ClampOrFallback(pcie, false, 2, 0, 2),
                ProcessorEppAc = ClampOrFallback(epp, true, 50, 0, 100),
                ProcessorEppDc = ClampOrFallback(epp, false, 50, 0, 100),
                ProcessorEppSupported = epp.Supported,
                CoreParkingMinAc = ClampOrFallback(coreParkingMin, true, 10, 0, 100),
                CoreParkingMinDc = ClampOrFallback(coreParkingMin, false, 10, 0, 100),
                CoreParkingSupported = coreParkingMin.Supported,
                DiskIdleAc = diskIdle.Supported ? Math.Max(0, diskIdle.Ac) : 0,
                DiskIdleDc = diskIdle.Supported ? Math.Max(0, diskIdle.Dc) : 0,
                DiskIdleSupported = diskIdle.Supported,
                WakeTimersAc = ClampOrFallback(wakeTimers, true, 2, 0, 2),
                WakeTimersDc = ClampOrFallback(wakeTimers, false, 0, 0, 2),
                WakeTimersSupported = wakeTimers.Supported,
            };
        }
        catch (Exception ex)
        {
            return new PlanParameterSet { Error = ex.Message };
        }
    }

    /// <summary>
    /// Writes one setting for both AC and DC. Editing an inactive plan never
    /// activates it; when the target is already active, the plan is re-applied so
    /// Windows picks up the new values immediately.
    /// </summary>
    public bool SetPlanParameter(string planGuid, string settingKey, int acValue, int dcValue)
    {
        try
        {
            string guid = ResolvePlanGuid(planGuid);
            var spec = ResolveKey(settingKey);
            acValue = Clamp(acValue, spec.Min, spec.Max);
            dcValue = Clamp(dcValue, spec.Min, spec.Max);

            PowerPlanService.RunPowercfg($"/setacvalueindex {guid} {spec.Subgroup} {spec.Setting} {acValue}");
            PowerPlanService.RunPowercfg($"/setdcvalueindex {guid} {spec.Subgroup} {spec.Setting} {dcValue}");

            if (string.Equals(GetActivePlanGuid(), guid, StringComparison.OrdinalIgnoreCase))
                PowerPlanService.RunPowercfg($"/setactive {guid}");

            // Verify instead of assuming that powercfg accepted the setting.
            var updated = QueryIndexes(guid, spec.Subgroup, spec.Setting);
            return updated.Supported && updated.Ac == acValue && updated.Dc == dcValue;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParseCurrentIndexes(string output, out int ac, out int dc)
    {
        ac = 0;
        dc = 0;
        if (string.IsNullOrWhiteSpace(output)) return false;

        var matches = HexIndexRegex.Matches(output);
        if (matches.Count < 2) return false;

        try
        {
            ac = Convert.ToInt32(matches[^2].Groups[1].Value, 16);
            dc = Convert.ToInt32(matches[^1].Groups[1].Value, 16);
            return true;
        }
        catch
        {
            ac = 0;
            dc = 0;
            return false;
        }
    }

    private SettingIndexes QueryIndexes(string planGuid, string subgroup, string setting)
    {
        string output = PowerPlanService.RunPowercfg($"/qh {planGuid} {subgroup} {setting}");
        return TryParseCurrentIndexes(output, out int ac, out int dc)
            ? new SettingIndexes(ac, dc, true)
            : new SettingIndexes(0, 0, false);
    }

    private string ResolvePlanGuid(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return GetActivePlanGuid();

        if (!Guid.TryParse(requested, out var parsed))
            throw new ArgumentException("GUID piano energetico non valido.");

        string guid = parsed.ToString("D").ToLowerInvariant();
        if (!_power.ListPlans().Any(p => p.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Il piano energetico selezionato non esiste più.");
        return guid;
    }

    private string GetActivePlanGuid()
    {
        var plan = _power.GetActivePlan();
        return plan?.Guid ?? PowerPlanService.BalancedGuid;
    }

    private string GetPlanName(string guid)
    {
        var plan = _power.ListPlans().FirstOrDefault(p =>
            p.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrWhiteSpace(plan?.Name) ? plan.Name : guid;
    }

    private static SettingSpec ResolveKey(string key) => key switch
    {
        "displayTimeout" => new(SubDisplay, SettingDisplayIdle, 0, int.MaxValue),
        "sleepTimeout" => new(SubSleep, SettingSleepIdle, 0, int.MaxValue),
        "processorMin" => new(SubProcessor, SettingProcMin, 0, 100),
        "processorMax" => new(SubProcessor, SettingProcMax, 0, 100),
        "boostMode" => new(SubProcessor, SettingBoost, 0, 6),
        "processorEpp" => new(SubProcessor, SettingProcessorEpp, 0, 100),
        "coreParkingMin" => new(SubProcessor, SettingCoreParkingMin, 0, 100),
        "pcieLinkState" => new(SubPciExpress, SettingPcieLs, 0, 2),
        "diskIdle" => new(SubDisk, SettingDiskIdle, 0, int.MaxValue),
        "wakeTimers" => new(SubSleep, SettingWakeTimers, 0, 2),
        _ => throw new ArgumentException($"Parametro sconosciuto: {key}"),
    };

    private static int ClampOrFallback(SettingIndexes indexes, bool ac, int fallback, int min, int max)
        => indexes.Supported ? Clamp(ac ? indexes.Ac : indexes.Dc, min, max) : fallback;

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;

    private readonly record struct SettingIndexes(int Ac, int Dc, bool Supported);
    private readonly record struct SettingSpec(string Subgroup, string Setting, int Min, int Max);
}
