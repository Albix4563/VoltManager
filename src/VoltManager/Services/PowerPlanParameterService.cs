using System.Text.RegularExpressions;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Reads and writes advanced power plan parameters (processor state, turbo boost,
/// PCI Express ASPM) via powercfg without requiring regedit or legacy control panels.
/// </summary>
public class PowerPlanParameterService
{
    // ── Sub-group GUIDs ────────────────────────────────────────────────────────
    private const string SubProcessor  = "54533251-82be-4824-96c1-47b60b740d00";
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";

    // ── Setting GUIDs ──────────────────────────────────────────────────────────
    private const string SettingProcMin   = "893dee8e-2bef-41e0-89c6-b55d0929964c"; // Processor Minimum State
    private const string SettingProcMax   = "bc5038f7-23e0-4960-96da-33abaf5935ec"; // Processor Maximum State
    private const string SettingBoost     = "be337238-0d82-4146-a960-4f3749d470c7"; // Processor Performance Boost Mode
    private const string SettingPcieLs    = "ee12f906-d166-11d0-b120-00a0c9062b5c"; // PCI Express ASPM

    // Regex to extract AC/DC index from powercfg /query output
    // Matches "Current AC Power Setting Index: 0x00000064"
    private static readonly Regex AcIndexRegex = new(
        @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DcIndexRegex = new(
        @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly PowerPlanService _power;

    public PowerPlanParameterService(PowerPlanService power)
    {
        _power = power;
    }

    /// <summary>
    /// Reads the current advanced parameters for the given plan GUID (or active plan if null).
    /// </summary>
    public PlanParameterSet GetPlanParameters(string? planGuid = null)
    {
        try
        {
            string guid = planGuid ?? GetActivePlanGuid();
            string planName = GetPlanName(guid);

            int procMinAc  = QueryAc(guid, SubProcessor,  SettingProcMin,  5);
            int procMinDc  = QueryDc(guid, SubProcessor,  SettingProcMin,  5);
            int procMaxAc  = QueryAc(guid, SubProcessor,  SettingProcMax,  100);
            int procMaxDc  = QueryDc(guid, SubProcessor,  SettingProcMax,  100);
            int boostAc    = QueryAc(guid, SubProcessor,  SettingBoost,    2);
            int boostDc    = QueryDc(guid, SubProcessor,  SettingBoost,    2);
            int pcieAc     = QueryAc(guid, SubPciExpress, SettingPcieLs,   0);
            int pcieDc     = QueryDc(guid, SubPciExpress, SettingPcieLs,   2);

            return new PlanParameterSet
            {
                PlanGuid       = guid,
                PlanName       = planName,
                ProcessorMinAc = Clamp(procMinAc,  0, 100),
                ProcessorMinDc = Clamp(procMinDc,  0, 100),
                ProcessorMaxAc = Clamp(procMaxAc,  0, 100),
                ProcessorMaxDc = Clamp(procMaxDc,  0, 100),
                BoostModeAc    = ClampBoost(boostAc),
                BoostModeDc    = ClampBoost(boostDc),
                PcieLinkStateAc = Clamp(pcieAc, 0, 2),
                PcieLinkStateDc = Clamp(pcieDc, 0, 2),
            };
        }
        catch (Exception ex)
        {
            return new PlanParameterSet { Error = ex.Message };
        }
    }

    /// <summary>
    /// Writes a single parameter (both AC and DC) and immediately activates the plan
    /// so the change takes effect without requiring a plan switch.
    /// </summary>
    public bool SetPlanParameter(string planGuid, string settingKey, int acValue, int dcValue)
    {
        try
        {
            (string subgroup, string setting, int minV, int maxV) = ResolveKey(settingKey);
            acValue = Clamp(acValue, minV, maxV);
            dcValue = Clamp(dcValue, minV, maxV);

            PowerPlanService.RunPowercfg($"/setacvalueindex {planGuid} {subgroup} {setting} {acValue}");
            PowerPlanService.RunPowercfg($"/setdcvalueindex {planGuid} {subgroup} {setting} {dcValue}");

            // Activate to apply immediately (required by powercfg design)
            string activePlan = GetActivePlanGuid();
            if (string.Equals(activePlan, planGuid, StringComparison.OrdinalIgnoreCase))
                PowerPlanService.RunPowercfg($"/setactive {planGuid}");

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private int QueryAc(string planGuid, string subgroup, string setting, int fallback)
    {
        string output = PowerPlanService.RunPowercfg($"/query {planGuid} {subgroup} {setting}");
        return ParseIndex(AcIndexRegex, output, fallback);
    }

    private int QueryDc(string planGuid, string subgroup, string setting, int fallback)
    {
        string output = PowerPlanService.RunPowercfg($"/query {planGuid} {subgroup} {setting}");
        return ParseIndex(DcIndexRegex, output, fallback);
    }

    private static int ParseIndex(Regex regex, string output, int fallback)
    {
        var m = regex.Match(output);
        if (!m.Success) return fallback;
        try { return Convert.ToInt32(m.Groups[1].Value, 16); }
        catch { return fallback; }
    }

    private string GetActivePlanGuid()
    {
        var plan = _power.GetActivePlan();
        return plan?.Guid ?? PowerPlanService.BalancedGuid;
    }

    private static string GetPlanName(string guid)
    {
        string output = PowerPlanService.RunPowercfg($"/query {guid}");
        // First line usually: "Power Scheme GUID: <guid>  (Name)"
        var m = Regex.Match(output, @"\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value.Trim() : guid;
    }

    /// <summary>Maps the JS setting key to (subgroup, setting, min, max) tuple.</summary>
    private static (string subgroup, string setting, int min, int max) ResolveKey(string key) => key switch
    {
        "processorMin"   => (SubProcessor,  SettingProcMin, 0, 100),
        "processorMax"   => (SubProcessor,  SettingProcMax, 0, 100),
        "boostMode"      => (SubProcessor,  SettingBoost,   0, 4),
        "pcieLinkState"  => (SubPciExpress, SettingPcieLs,  0, 2),
        _ => throw new ArgumentException($"Parametro sconosciuto: {key}"),
    };

    private static int Clamp(int value, int min, int max)
        => value < min ? min : value > max ? max : value;

    /// <summary>Valid boost mode values: 0,1,2,4 — clamp unknown values to nearest valid.</summary>
    private static int ClampBoost(int v) => v switch
    {
        0 => 0,
        1 => 1,
        4 => 4,
        _ => 2, // default Aggressive
    };
}
