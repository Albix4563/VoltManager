using System.Diagnostics;
using VoltManager.Services;

namespace VoltManager.Fans;

/// <summary>
/// Best-effort process evidence for software that can participate in hardware/fan control.
/// Process-name detection is intentionally classified as Possible, never Confirmed, because
/// Windows does not expose a universal API that maps a process to ownership of a fan header.
/// </summary>
public sealed class FanExternalConflictDetector
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);
    private readonly object _cacheGate = new();
    private DateTime _lastScanUtc = DateTime.MinValue;
    private IReadOnlyList<FanExternalSoftwareNotice> _lastResult = Array.Empty<FanExternalSoftwareNotice>();

    private static readonly (string Token, string Product)[] KnownProcessTokens =
    {
        ("fancontrol", "Fan Control"),
        ("armourycrate", "ASUS Armoury Crate"),
        ("aisuite", "ASUS AI Suite"),
        ("msiafterburner", "MSI Afterburner"),
        ("gigabytecontrolcenter", "Gigabyte Control Center"),
        ("aorus", "Gigabyte / AORUS utility"),
        ("icue", "Corsair iCUE"),
        ("corsair", "Corsair utility"),
        ("l-connect", "Lian Li L-Connect"),
        ("lconnect", "Lian Li L-Connect"),
        ("nzxtcam", "NZXT CAM"),
        ("signalrgb", "SignalRGB"),
        ("openrgb", "OpenRGB"),
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
            var names = Process.GetProcesses()
                .Select(process =>
                {
                    try { return process.ProcessName; }
                    catch { return null; }
                    finally { process.Dispose(); }
                })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToList();
            var result = DetectFromProcessNames(names);
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
        var results = new List<FanExternalSoftwareNotice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in processNames)
        {
            string normalized = raw.Trim().ToLowerInvariant();
            if (normalized.Length == 0) continue;

            foreach (var known in KnownProcessTokens)
            {
                if (!normalized.Contains(known.Token, StringComparison.OrdinalIgnoreCase)) continue;
                string key = known.Product + "|" + raw;
                if (!seen.Add(key)) break;

                results.Add(new FanExternalSoftwareNotice
                {
                    SoftwareName = known.Product,
                    ProcessName = raw,
                    Confidence = FanConflictConfidence.Possible,
                    Evidence = "A process associated with hardware/fan utilities is running. Device ownership cannot be confirmed from the process name alone.",
                    BlocksControl = false,
                });
                break;
            }
        }

        return results.OrderBy(x => x.SoftwareName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
