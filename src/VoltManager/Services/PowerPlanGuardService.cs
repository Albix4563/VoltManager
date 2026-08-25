using System.Diagnostics;
using System.Text.Json.Serialization;
using VoltManager.Models;

namespace VoltManager.Services;

public enum PowerPlanInterferenceConfidence
{
    Known,
    Probable,
}

public record PowerPlanProcessSnapshot(int ProcessId, string Name, string Path);

public record PowerPlanInterferingProcess
{
    [JsonPropertyName("processId")] public int ProcessId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("confidence")] public PowerPlanInterferenceConfidence Confidence { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

public record PowerPlanConflictNotification
{
    [JsonPropertyName("expectedPlan")] public PlanId ExpectedPlan { get; init; }
    [JsonPropertyName("actualPlan")] public PlanId? ActualPlan { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
    [JsonPropertyName("shouldNotifyUser")] public bool ShouldNotifyUser { get; init; }
    [JsonPropertyName("suspects")] public List<PowerPlanInterferingProcess> Suspects { get; init; } = new();
    [JsonPropertyName("detectedAtUtc")] public DateTime DetectedAtUtc { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

public record PowerPlanExpectation(PlanId Plan, string Source, string Detail);

public sealed class PowerPlanGuardService
{
    private static readonly string[] KnownPowerManagerKeys =
    {
        "processlasso", "processlassosrv", "armourycrate", "asusframework", "ghelper",
        "msicenter", "dragoncenter", "lenovovantage", "commercialvantage", "dellpowermanager",
        "alienwarecommandcenter", "awcc", "omengaminghub", "omencommandcenter", "razercortex",
        "gigabytecontrolcenter", "aorusengine", "nzxtcam", "corsairicue"
    };

    private static readonly string[] ProbablePowerManagerMarkers =
    {
        "power", "battery", "energy", "performance", "optimizer", "tuning", "boost", "turbo",
        "eco", "profile", "thermal"
    };

    private readonly TimeSpan _notificationInterval;
    private readonly object _lock = new();
    private PowerPlanExpectation? _expectation;
    private DateTime _nextNotificationAllowedUtc = DateTime.MinValue;

    public PowerPlanGuardService(TimeSpan? notificationInterval = null)
    {
        _notificationInterval = notificationInterval ?? TimeSpan.FromMinutes(2);
    }

    public PowerPlanExpectation? Expectation
    {
        get { lock (_lock) return _expectation; }
    }

    public void SetExpected(PlanId plan, string source, string detail = "")
    {
        lock (_lock)
        {
            if (_expectation?.Plan == plan && _expectation.Source == source && _expectation.Detail == detail)
                return;

            _expectation = new PowerPlanExpectation(plan, source, detail);
        }
    }

    public void ClearExpected(string? source = null)
    {
        lock (_lock)
        {
            if (source != null && _expectation?.Source != source)
                return;

            _expectation = null;
            _nextNotificationAllowedUtc = DateTime.MinValue;
        }
    }

    public void RefreshManualOverride(ManualOverride? manualOverride, DateTime nowUtc)
    {
        var plan = PlanFromManualOverride(manualOverride, nowUtc);
        if (plan != null)
            SetExpected(plan.Value, "manualOverride", PlanKey(plan.Value));
        else
            ClearExpected("manualOverride");
    }

    public static PlanId? PlanFromManualOverride(ManualOverride? manualOverride, DateTime nowUtc)
    {
        if (manualOverride?.IsActive(nowUtc) != true)
            return null;

        return manualOverride.Plan switch
        {
            "powerSaver" => PlanId.PowerSaver,
            "balanced" => PlanId.Balanced,
            "performance" => PlanId.Performance,
            _ => null,
        };
    }

    public bool ShouldReassert(PlanId? activePlan, DateTime nowUtc, out PowerPlanConflictNotification? conflict)
    {
        lock (_lock)
        {
            conflict = null;
            if (_expectation == null || activePlan == _expectation.Plan)
                return false;

            bool notify = nowUtc >= _nextNotificationAllowedUtc;
            if (notify)
                _nextNotificationAllowedUtc = nowUtc.Add(_notificationInterval);

            conflict = new PowerPlanConflictNotification
            {
                ExpectedPlan = _expectation.Plan,
                ActualPlan = activePlan,
                Source = _expectation.Source,
                Detail = _expectation.Detail,
                ShouldNotifyUser = notify,
                DetectedAtUtc = nowUtc,
            };
            return true;
        }
    }

    public static List<PowerPlanInterferingProcess> FindLikelyInterferingProcesses()
    {
        // Runs only when a plan conflict is detected, so a snapshot a few seconds old
        // is fine — and usually already captured by the periodic scanners.
        var snapshot = ProcessSnapshotProvider.Get(TimeSpan.FromSeconds(10));
        var processes = new List<PowerPlanProcessSnapshot>();
        foreach (var process in snapshot.Processes)
        {
            try
            {
                if (process.Pid == Environment.ProcessId) continue;
                processes.Add(new PowerPlanProcessSnapshot(
                    process.Pid, process.Name, ProcessSnapshotProvider.GetPath(process)));
            }
            catch
            {
                // Process enumeration can race with exits or protected processes.
            }
        }

        return FindLikelyInterferingProcesses(processes);
    }

    public static List<PowerPlanInterferingProcess> FindLikelyInterferingProcesses(IEnumerable<PowerPlanProcessSnapshot> processes)
        => processes
            .Select(ClassifyProcess)
            .Where(p => p != null)
            .Select(p => p!)
            .OrderBy(p => p.Confidence == PowerPlanInterferenceConfidence.Known ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(5)
            .ToList();

    public static PowerPlanConflictNotification WithSuspectsAndMessage(PowerPlanConflictNotification conflict,
        List<PowerPlanInterferingProcess> suspects)
        => conflict with
        {
            Suspects = suspects,
            Message = BuildMessage(conflict, suspects),
        };

    private static PowerPlanInterferingProcess? ClassifyProcess(PowerPlanProcessSnapshot process)
    {
        string key = Normalize(process.Name);
        string pathKey = Normalize(process.Path);
        if (string.IsNullOrWhiteSpace(key) || key == "voltmanager")
            return null;

        if (KnownPowerManagerKeys.Any(known => key.Contains(known, StringComparison.OrdinalIgnoreCase) ||
                                               pathKey.Contains(known, StringComparison.OrdinalIgnoreCase)))
        {
            return new PowerPlanInterferingProcess
            {
                ProcessId = process.ProcessId,
                Name = process.Name,
                Path = process.Path,
                Confidence = PowerPlanInterferenceConfidence.Known,
                Reason = "knownPowerManager",
            };
        }

        if (ProbablePowerManagerMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return new PowerPlanInterferingProcess
            {
                ProcessId = process.ProcessId,
                Name = process.Name,
                Path = process.Path,
                Confidence = PowerPlanInterferenceConfidence.Probable,
                Reason = "probablePowerManager",
            };
        }

        return null;
    }

    private static string BuildMessage(PowerPlanConflictNotification conflict, IReadOnlyList<PowerPlanInterferingProcess> suspects)
    {
        string suspectText = suspects.Count == 0
            ? "un processo esterno"
            : suspects[0].Confidence == PowerPlanInterferenceConfidence.Known
                ? suspects[0].Name
                : suspects[0].Name + " (probabile)";
        return $"VoltManager ha ripristinato il piano {conflict.ExpectedPlan}: {suspectText} stava imponendo un piano energetico diverso.";
    }

    private static string PlanKey(PlanId plan) => plan switch
    {
        PlanId.PowerSaver => "powerSaver",
        PlanId.Balanced => "balanced",
        PlanId.Performance => "performance",
        _ => "",
    };

    private static string Normalize(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

}
