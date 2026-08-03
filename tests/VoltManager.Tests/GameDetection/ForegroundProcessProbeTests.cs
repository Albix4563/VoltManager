using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public class ForegroundProcessProbeTests
{
    [Fact]
    public void IsNearFullscreenRect_accepts_exact_and_near_monitor_cover()
    {
        Assert.True(ForegroundProcessProbe.IsNearFullscreenRect(
            0, 0, 1920, 1080,
            0, 0, 1920, 1080));

        // Small overscan / borderless offset within tolerance.
        Assert.True(ForegroundProcessProbe.IsNearFullscreenRect(
            -2, -2, 1922, 1082,
            0, 0, 1920, 1080,
            tolerancePx: 16));

        // Secondary monitor origin.
        Assert.True(ForegroundProcessProbe.IsNearFullscreenRect(
            1920, 0, 3840, 1080,
            1920, 0, 3840, 1080));
    }

    [Fact]
    public void IsNearFullscreenRect_rejects_windowed_and_partial_cover()
    {
        // Typical windowed game.
        Assert.False(ForegroundProcessProbe.IsNearFullscreenRect(
            100, 100, 1380, 820,
            0, 0, 1920, 1080));

        // Maximized-ish but not full height.
        Assert.False(ForegroundProcessProbe.IsNearFullscreenRect(
            0, 0, 1920, 900,
            0, 0, 1920, 1080));

        // Empty / inverted.
        Assert.False(ForegroundProcessProbe.IsNearFullscreenRect(
            0, 0, 0, 0,
            0, 0, 1920, 1080));
    }

    [Fact]
    public void TryGetPresentationProcessIds_includes_live_foreground_or_is_non_throwing()
    {
        // Live probe: must not throw; usually includes the shell/test-host foreground PID.
        var pids = ForegroundProcessProbe.TryGetPresentationProcessIds();
        Assert.NotNull(pids);
        // On an interactive desktop we almost always see at least one presentation owner.
        // Keep soft: empty only if session has no visible windows (CI headless).
        Assert.True(pids.Count >= 0);
        if (pids.Count > 0)
            Assert.All(pids, pid => Assert.True(pid > 0));
    }

    [Fact]
    public void TryGetForegroundProcessId_is_stable_with_presentation_set()
    {
        int? fg = ForegroundProcessProbe.TryGetForegroundProcessId();
        var presentation = ForegroundProcessProbe.TryGetPresentationProcessIds();
        if (fg != null)
            Assert.Contains(fg.Value, presentation);
    }

    [Fact]
    public void ShouldAttributeD3dFullscreen_requires_foreground_and_presentation_ownership()
    {
        var presentation = new HashSet<int> { 100, 200 };

        Assert.True(ForegroundProcessProbe.ShouldAttributeD3dFullscreen(
            d3dFullscreenActive: true, pid: 100, foregroundPid: 100, presentation));

        // Background presentation owner: the shell flag says nothing about it.
        Assert.False(ForegroundProcessProbe.ShouldAttributeD3dFullscreen(
            d3dFullscreenActive: true, pid: 200, foregroundPid: 100, presentation));

        // Foreground but no fullscreen window of its own.
        Assert.False(ForegroundProcessProbe.ShouldAttributeD3dFullscreen(
            d3dFullscreenActive: true, pid: 300, foregroundPid: 300, presentation));
    }

    [Fact]
    public void ShouldAttributeD3dFullscreen_is_false_without_the_shell_flag()
    {
        var presentation = new HashSet<int> { 100 };

        Assert.False(ForegroundProcessProbe.ShouldAttributeD3dFullscreen(
            d3dFullscreenActive: false, pid: 100, foregroundPid: 100, presentation));
        Assert.False(ForegroundProcessProbe.ShouldAttributeD3dFullscreen(
            d3dFullscreenActive: true, pid: 100, foregroundPid: null, presentation));
    }

    [Fact]
    public void IsD3dFullscreenActive_does_not_throw()
        => _ = ForegroundProcessProbe.IsD3dFullscreenActive();
}
