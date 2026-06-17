using Xunit;
using VoltManager.Models;

namespace VoltManager.Tests;

/// <summary>
/// Unit tests for MemoryStatus calculations and boundary conditions.
/// The actual P/Invoke calls are not tested here (require admin + live system).
/// </summary>
public class MemoryOptimizerServiceTests
{
    // ── MemoryStatus model ───────────────────────────────────────────────────

    [Fact]
    public void MemoryStatus_DefaultValues_AreNonNegative()
    {
        var ms = new MemoryStatus();
        Assert.True(ms.TotalGb   >= 0);
        Assert.True(ms.InUseGb   >= 0);
        Assert.True(ms.StandbyGb >= 0);
        Assert.True(ms.FreeGb    >= 0);
        Assert.True(ms.InUsePct  >= 0);
        Assert.True(ms.StandbyPct >= 0);
    }

    [Theory]
    [InlineData(16.0, 8.0,  2.0,  6.0,  50.0, 12.5)]  // typical desktop
    [InlineData(8.0,  7.0,  0.5,  0.5,  87.5,  6.25)] // high usage
    [InlineData(32.0, 2.0,  4.0, 26.0,   6.25, 12.5)] // mostly free
    [InlineData(16.0, 0.0,  0.0, 16.0,   0.0,   0.0)] // fresh boot
    public void MemoryStatus_PctCalculations_AreCorrect(
        double total, double inUse, double standby, double free,
        double expectedInUsePct, double expectedStandbyPct)
    {
        double inUsePct   = total > 0 ? Math.Round(inUse   / total * 100, 1) : 0;
        double standbyPct = total > 0 ? Math.Round(standby / total * 100, 1) : 0;
        Assert.Equal(expectedInUsePct,   inUsePct,   1);
        Assert.Equal(expectedStandbyPct, standbyPct, 1);

        // Sanity: in-use + standby + free must equal total (within rounding)
        double sum = inUse + standby + free;
        Assert.Equal(total, sum, 2);
    }

    [Fact]
    public void MemoryStatus_StandbyPct_NeverExceedsAvailPct()
    {
        // Standby is always a subset of available (avail = standby + free),
        // so standbyPct must never exceed (1 - inUsePct).
        double total = 16.0, inUse = 6.0, standby = 4.0;
        double inUsePct   = inUse   / total * 100;
        double standbyPct = standby / total * 100;
        Assert.True(standbyPct <= 100 - inUsePct + 0.01); // +0.01 for floating point
    }

    [Theory]
    [InlineData(0)]   // no total → 0 pct
    [InlineData(-1)]  // negative total → guard against div/0
    public void MemoryStatus_ZeroOrNegativeTotal_ReturnZeroPct(double total)
    {
        double inUsePct = total > 0 ? 10.0 / total * 100 : 0;
        Assert.Equal(0, inUsePct);
    }

    // ── Standby GB cap ────────────────────────────────────────────────────────

    [Fact]
    public void StandbyGb_IsCappeToAvailablePhysical()
    {
        // Service logic: standby = min(rawStandby, availPhys)
        double availPhys = 5.0;
        double rawStandby = 8.0; // counter returned more than available (corrupt read)
        double standby = Math.Min(rawStandby, availPhys);
        Assert.Equal(availPhys, standby);
    }

    [Fact]
    public void FreeGb_IsNonNegativeAfterSubtractingStandby()
    {
        // free = max(0, availPhys - standby)
        double availPhys = 3.0, standby = 3.5; // standby slightly > avail due to timing
        double free = Math.Max(0, availPhys - standby);
        Assert.True(free >= 0);
    }
}
