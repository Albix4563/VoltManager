using VoltManager.Models;
using VoltManager.Services;
using VoltManager.Services.GameDetection;

namespace VoltManager.Tests.GameDetection;

public sealed class HeavyAppPipelineTests
{
    [Fact]
    public void Collector_reads_sources_once_and_expires_gpu_preferences()
    {
        DateTime now = new(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc);
        int snapshots = 0, paths = 0, prefs = 0;
        var sample = new ProcessSample(4242, 100, "Title", 700L * 1024 * 1024, TimeSpan.Zero, now.AddMinutes(-2));
        var snapshot = new ProcessSnapshot(now, new[] { sample });
        var collector = new HeavyAppEvidenceCollector(
            utcNow: () => now,
            snapshotProvider: _ => { snapshots++; return snapshot; },
            pathResolver: _ => { paths++; return @"D:\SteamLibrary\steamapps\common\Title\Title.exe"; },
            presentationProcessIds: () => new HashSet<int> { 4242 },
            foregroundProcessId: () => 4242,
            d3dFullscreenActive: () => true,
            gpu3DByProcess: () => new Dictionary<int, double> { [4242] = 31 },
            gpuPreferenceReader: () => { prefs++; return new HashSet<string>(StringComparer.OrdinalIgnoreCase); });

        var config = new HeavyAppDetectionSettings { UseWindowsGpuPreferences = true };
        var first = collector.Capture(config);
        var process = Assert.Single(first.Processes);
        Assert.True(process.IsForeground);
        Assert.True(process.D3dFullscreen);
        Assert.Equal(31, process.Gpu3DPercent);
        Assert.Equal((1, 1, 1), (snapshots, paths, prefs));

        now = now.AddSeconds(HeavyAppEvidenceCollector.GpuPreferencesCacheDuration.TotalSeconds - 1);
        collector.Capture(config);
        Assert.Equal(1, prefs);
        now = now.AddSeconds(2);
        collector.Capture(config);
        Assert.Equal(2, prefs);
        Assert.Equal(3, snapshots);
    }
}
