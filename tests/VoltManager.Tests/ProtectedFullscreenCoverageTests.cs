using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class ProtectedFullscreenCoverageTests
{
    private static readonly IntPtr MonitorA = new(1);
    private static readonly IntPtr MonitorB = new(2);
    private static readonly PixelRect BoundsA = new(0, 0, 1920, 1080);
    private static readonly PixelRect BoundsB = new(1920, 0, 1920, 1080);

    [Fact]
    public void Protected_fullscreen_above_surface_on_same_monitor_covers_it()
    {
        IntPtr surface = new(100);
        var windows = new[]
        {
            Window(new IntPtr(200), 1, 42, MonitorA, BoundsA, BoundsA),
            Window(surface, 4, Environment.ProcessId, MonitorA, new PixelRect(200, 100, 1000, 700), BoundsA),
        };

        Assert.True(ProtectedFullscreenCoverageService.IsSurfaceCovered(
            surface, windows, new HashSet<int> { 42 }));
    }

    [Fact]
    public void Fullscreen_behind_surface_does_not_count_as_coverage()
    {
        IntPtr surface = new(100);
        var windows = new[]
        {
            Window(surface, 1, Environment.ProcessId, MonitorA, new PixelRect(200, 100, 1000, 700), BoundsA),
            Window(new IntPtr(200), 4, 42, MonitorA, BoundsA, BoundsA),
        };

        Assert.False(ProtectedFullscreenCoverageService.IsSurfaceCovered(
            surface, windows, new HashSet<int> { 42 }));
    }

    [Fact]
    public void Fullscreen_on_other_monitor_does_not_suspend_surface()
    {
        IntPtr surface = new(100);
        var windows = new[]
        {
            Window(new IntPtr(200), 1, 42, MonitorB, BoundsB, BoundsB),
            Window(surface, 4, Environment.ProcessId, MonitorA, new PixelRect(200, 100, 1000, 700), BoundsA),
        };

        Assert.False(ProtectedFullscreenCoverageService.IsSurfaceCovered(
            surface, windows, new HashSet<int> { 42 }));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Minimized_or_cloaked_fullscreen_is_ignored(bool minimized, bool cloaked)
    {
        IntPtr surface = new(100);
        var windows = new[]
        {
            Window(new IntPtr(200), 1, 42, MonitorA, BoundsA, BoundsA, minimized, cloaked),
            Window(surface, 4, Environment.ProcessId, MonitorA, new PixelRect(200, 100, 1000, 700), BoundsA),
        };

        Assert.False(ProtectedFullscreenCoverageService.IsSurfaceCovered(
            surface, windows, new HashSet<int> { 42 }));
    }

    [Fact]
    public void Uncertain_or_non_fullscreen_geometry_fails_open()
    {
        IntPtr surface = new(100);
        var windows = new[]
        {
            Window(new IntPtr(200), 1, 42, MonitorA, new PixelRect(0, 0, 1200, 900), BoundsA),
            Window(surface, 4, Environment.ProcessId, MonitorA, new PixelRect(200, 100, 1000, 700), BoundsA),
        };

        Assert.False(ProtectedFullscreenCoverageService.IsSurfaceCovered(
            surface, windows, new HashSet<int> { 42 }));
        Assert.False(ProtectedFullscreenCoverageService.IsSurfaceCovered(
            new IntPtr(999), windows, new HashSet<int> { 42 }));
    }

    [Theory]
    [InlineData(true, true, 2, true, true)]
    [InlineData(false, true, 2, true, false)]
    [InlineData(false, false, 10, true, true)]
    public void Hardware_sample_demand_preserves_thermal_reads_and_slows_accessories(
        bool visual,
        bool thermal,
        int expectedSeconds,
        bool expectedTemperatures,
        bool expectedDetails)
    {
        HardwareSampleRequest request = MonitorService.ResolveHardwareSampleRequest(
            new MonitorSamplingDemand(visual, thermal, GpuProcessDetection: false));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), request.MinimumInterval);
        Assert.Equal(expectedTemperatures, request.Temperatures);
        Assert.Equal(expectedDetails, request.VisualDetails);
    }

    private static ProtectedFullscreenCoverageService.CoverageWindow Window(
        IntPtr hwnd,
        int z,
        int pid,
        IntPtr monitor,
        PixelRect bounds,
        PixelRect monitorBounds,
        bool minimized = false,
        bool cloaked = false)
        => new(hwnd, z, pid, monitor, bounds, monitorBounds, true, minimized, cloaked);
}
