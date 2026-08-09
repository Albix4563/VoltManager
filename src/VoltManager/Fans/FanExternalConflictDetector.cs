using System.Diagnostics;
using System.Management;
using VoltManager.Services;

namespace VoltManager.Fans;

/// <summary>
/// Conservative coexistence detector for software that may own fan/controller
/// hardware. Windows has no universal fan-header ownership API, so evidence is
/// explicitly labelled. Utilities known to perform fan control block VoltManager
/// writes while running; RGB-only evidence remains informational.
/// </summary>
public sealed class FanExternalConflictDetector
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);
    private readonly object _cacheGate = new();
    private DateTime _lastScanUtc = DateTime.MinValue;
    private IReadOnlyList<FanExternalSoftwareNotice> _lastResult = Array.Empty<FanExternalSoftwareNotice>();

    private static readonly KnownUtility[] KnownUtilities =
    {
        new("Fan Control", true, "fancontrol"),
        new("ASUS Armoury Crate", true, "armourycrate", "asusfancontrol", "asus_framework"),
        new("ASUS AI Suite", true, "aisuite", "ascomsvc"),
        new("MSI Afterburner", true, "msiafterburner"),
        new("MSI Center", true, "msicenter", "msi.center", "msi central server"),
        new("Gigabyte Control Center", true, "gigabytecontrolcenter", "gservice"),
        new("Gigabyte / AORUS utility", true, "aorus"),
        new("Corsair iCUE", true, "icue", "corsairservice"),
        new("Lian Li L-Connect", true, "l-connect", "lconnect"),
        new("NZXT CAM", true, "nzxtcam", "nzxt cam"),
        new("Alienware Command Center", true, "awcc", "alienwarecommandcenter", "awccservice"),
        new("Lenovo Legion Toolkit/Vantage", true, "legiontoolkit", "lenovovantage", "imcontroller"),
        new("HP OMEN Gaming Hub", true, "omen gaming hub", "omencommandcenter"),
        // These can access hardware but fan ownership is not intrinsic to their core
        // purpose, so process/service evidence is surfaced without blocking by itself.
        new("SignalRGB", false, "signalrgb"),
        new("OpenRGB", false, "openrgb"),
    };

    public IReadOnlyList<FanExternalSoftwareNotice> Scan(bool force = false)
    {
        lock (_cacheGate)
        {
            if (!force && DateTime.UtcNow - _lastScanUtc < CacheDuration)
                return _lastResult;
        }

        try
        {
            var evidence = new List<Evidence>();
            evidence.AddRange(ReadProcesses());
            evidence.AddRange(ReadRunningServices());
            IReadOnlyList<FanExternalSoftwareNotice> result = MergeEvidence(evidence);

            lock (_cacheGate)
            {
                _lastResult = result;
                _lastScanUtc = DateTime.UtcNow;
            }
            return result;
        }
        catch (Exception ex)
        {
            Logger.Warn("Fan external software scan failed: " + ex.Message);
            return Array.Empty<FanExternalSoftwareNotice>();
        }
    }

    public IReadOnlyList<FanExternalSoftwareNotice> DetectFromProcessNames(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        return MergeEvidence(processNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(name => Match(name, EvidenceType.Process, name))
            .ToList());
    }

    internal IReadOnlyList<FanExternalSoftwareNotice> DetectFromEvidenceForTests(
        IEnumerable<string> processNames,
        IEnumerable<string> serviceNames)
    {
        var evidence = processNames.SelectMany(name => Match(name, EvidenceType.Process, name))
            .Concat(serviceNames.SelectMany(name => Match(name, EvidenceType.Service, name)))
            .ToList();
        return MergeEvidence(evidence);
    }

    private static IEnumerable<Evidence> ReadProcesses()
    {
        foreach (Process process in Process.GetProcesses())
        {
            string? name = null;
            try { name = process.ProcessName; }
            catch { }
            finally { process.Dispose(); }
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (Evidence item in Match(name, EvidenceType.Process, name)) yield return item;
        }
    }

    private static IReadOnlyList<Evidence> ReadRunningServices()
    {
        var evidence = new List<Evidence>();
        ManagementObjectCollection? results = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName FROM Win32_Service WHERE State='Running'");
            results = searcher.Get();
            foreach (ManagementObject service in results)
            {
                using (service)
                {
                    string name = Convert.ToString(service["Name"]) ?? "";
                    string display = Convert.ToString(service["DisplayName"]) ?? "";
                    string searchable = name + " " + display;
                    evidence.AddRange(Match(searchable, EvidenceType.Service, name));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Fan controller service scan unavailable: " + ex.Message);
        }
        finally
        {
            results?.Dispose();
        }
        return evidence;
    }

    private static IEnumerable<Evidence> Match(string value, EvidenceType type, string sourceName)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0) yield break;

        foreach (KnownUtility utility in KnownUtilities)
        {
            if (!utility.Tokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase))) continue;
            yield return new Evidence(utility, type, sourceName);
        }
    }

    private static IReadOnlyList<FanExternalSoftwareNotice> MergeEvidence(IReadOnlyList<Evidence> evidence)
    {
        return evidence
            .GroupBy(x => x.Utility.Product, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                KnownUtility utility = group.First().Utility;
                string process = group.FirstOrDefault(x => x.Type == EvidenceType.Process)?.SourceName ?? "";
                string? service = group.FirstOrDefault(x => x.Type == EvidenceType.Service)?.SourceName;
                bool hasProcess = process.Length > 0;
                bool hasService = !string.IsNullOrWhiteSpace(service);
                FanConflictConfidence confidence = hasProcess && hasService
                    ? FanConflictConfidence.High
                    : FanConflictConfidence.Possible;

                string evidenceText = hasProcess && hasService
                    ? "A matching application process and running hardware service were detected. Windows still cannot prove ownership of a specific fan header."
                    : hasService
                        ? "A running hardware service associated with this utility was detected. Device ownership cannot be mapped to a specific fan header universally."
                        : "A process associated with this hardware/fan utility is running. Device ownership cannot be confirmed from the process name alone.";

                return new FanExternalSoftwareNotice
                {
                    SoftwareName = utility.Product,
                    ProcessName = process,
                    ServiceName = service,
                    Confidence = confidence,
                    Evidence = evidenceText,
                    BlocksControl = utility.BlocksControl,
                };
            })
            .OrderByDescending(x => x.BlocksControl)
            .ThenBy(x => x.SoftwareName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record KnownUtility(string Product, bool BlocksControl, params string[] Tokens);
    private sealed record Evidence(KnownUtility Utility, EvidenceType Type, string SourceName);
    private enum EvidenceType { Process, Service }
}
