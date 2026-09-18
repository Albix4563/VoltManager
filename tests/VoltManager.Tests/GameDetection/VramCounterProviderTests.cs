using VoltManager.Services;

namespace VoltManager.Tests.GameDetection;

public sealed class VramCounterProviderTests
{
    [Fact]
    public void Failed_discovery_is_throttled_and_can_recover()
    {
        using var provider = new VramCounterProvider(initialize: false);
        int attempts = 0;
        void Unavailable() { attempts++; throw new InvalidOperationException("No GPU counters"); }
        var start = DateTime.UnixEpoch;
        for (int second = 0; second < 30; second++)
            provider.RefreshIfDue(start.AddSeconds(second), Unavailable);
        Assert.Equal(1, attempts);
        provider.RefreshIfDue(start.AddSeconds(30), Unavailable);
        Assert.Equal(2, attempts);
        provider.RefreshIfDue(start.AddSeconds(60), () => attempts++);
        provider.RefreshIfDue(start.AddSeconds(61), Unavailable);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData("luid_0x00000000_0x0000C1DA_phys_0")]
    [InlineData("pid_9184_luid_0x00000000_0x0000C1DA_phys_0")]
    public void ParseLuid_reads_windows_gpu_instance_names(string instance)
    {
        Assert.True(VramCounterProvider.TryParseLuid(instance, out var luid));
        Assert.Equal(0xC1DAu, luid.LowPart);
        Assert.Equal(0, luid.HighPart);
    }

    [Fact]
    public void Snapshot_uses_max_pressure_across_dedicated_adapters()
    {
        var a = new VramCounterProvider.GpuLuid(1, 0);
        var b = new VramCounterProvider.GpuLuid(2, 0);
        long gib = 1024L * 1024 * 1024;
        var snapshot = VramCounterProvider.BuildSnapshot(
            new[]
            {
                new VramCounterProvider.UsageSample(a, "a", 4 * gib),
                new VramCounterProvider.UsageSample(b, "b", 3 * gib),
            },
            new Dictionary<VramCounterProvider.GpuLuid, long>
            {
                [a] = 8 * gib,
                [b] = 4 * gib,
            },
            DateTime.UnixEpoch);

        Assert.True(snapshot.Available);
        Assert.Equal(75, snapshot.PressurePercent);
        Assert.Equal(2, snapshot.Adapters.Count);
    }

    [Fact]
    public void Integrated_or_ambiguous_adapter_data_is_not_reported_as_dedicated_vram()
    {
        var integrated = new VramCounterProvider.GpuLuid(1, 0);
        var ambiguous = new VramCounterProvider.GpuLuid(2, 0);
        long mib = 1024L * 1024;
        var snapshot = VramCounterProvider.BuildSnapshot(
            new[]
            {
                new VramCounterProvider.UsageSample(integrated, "integrated", 64 * mib),
                new VramCounterProvider.UsageSample(ambiguous, "node0", 900 * mib),
                new VramCounterProvider.UsageSample(ambiguous, "node1", 900 * mib),
            },
            new Dictionary<VramCounterProvider.GpuLuid, long>
            {
                [integrated] = 128 * mib,
                [ambiguous] = 2L * 1024 * mib,
            },
            DateTime.UnixEpoch);

        Assert.False(snapshot.Available);
        Assert.Null(snapshot.PressurePercent);
        Assert.Empty(snapshot.Adapters);
    }
}
