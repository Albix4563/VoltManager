using VoltManager.Models;

namespace VoltManager.Performance;

/// <summary>Pure thresholds and transition policy for adaptive resource pressure.</summary>
public static class ResourcePressurePolicy
{
    public const double CriticalRamEnterPct = 92;
    public const double CriticalRamExitPct = 85;
    public const double CriticalCpuPct = 95;
    public const double CriticalGpuPct = 95;

    public static readonly TimeSpan CriticalEnterDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CriticalExitDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan GameExitCooldown = TimeSpan.FromSeconds(15);

    public static ResourceProfile BaselineProfile(double ramTotalGb, int logicalCores)
        => ramTotalGb < 16 || logicalCores <= 4
            ? ResourceProfile.Balanced
            : ResourceProfile.Full;

    public static bool IsExtremeGameLoad(MetricsSnapshot metrics, bool gameActive)
    {
        if (!gameActive) return false;
        if (metrics.Cpu >= CriticalCpuPct) return true;
        if (metrics.GpuAvailable && metrics.Gpu >= CriticalGpuPct) return true;
        return metrics.GpuAvailable && metrics.Cpu >= 90 && metrics.Gpu >= 90;
    }
}
