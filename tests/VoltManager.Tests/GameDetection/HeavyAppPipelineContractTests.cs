using System.IO;

namespace VoltManager.Tests.GameDetection;

public sealed class HeavyAppPipelineContractTests
{
    [Fact]
    public void Service_coordinates_pipeline_instead_of_reading_process_and_window_sources_directly()
    {
        string source = LocateSource("Services", "HeavyAppDetectionService.cs");

        Assert.Contains("HeavyAppEvidenceCollector", source);
        Assert.Contains("HeavyAppClassifier.Classify", source);
        Assert.Contains("HeavyAppTracker", source);
        Assert.DoesNotContain("ProcessSnapshotProvider.Get(", source);
        Assert.DoesNotContain("ForegroundProcessProbe.TryGetPresentationProcessIds", source);
        Assert.DoesNotContain("ForegroundProcessProbe.TryGetForegroundProcessId", source);
        Assert.DoesNotContain("ForegroundProcessProbe.IsD3dFullscreenActive", source);
    }

    [Fact]
    public void Evidence_collector_owns_one_shared_snapshot_read_per_capture()
    {
        string source = LocateSource("Services", "GameDetection", "HeavyAppEvidenceCollector.cs");

        Assert.Equal(1, Count(source, "_snapshotProvider(SnapshotMaxAge)"));
        Assert.Contains("GpuPreferencesCacheDuration = TimeSpan.FromSeconds(30)", source);
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string LocateSource(params string[] relativeParts)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(new[] { dir, "src", "VoltManager" }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException("Could not locate source from " + AppContext.BaseDirectory);
    }
}
