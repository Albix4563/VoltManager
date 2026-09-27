using VoltManager.Services;

namespace VoltManager.Tests.GameDetection;

public class GpuCounterProviderTests
{
    [Theory]
    [InlineData("pid_9184_luid_0x00000000_0x0000C1DA_phys_0_eng_0_engtype_3D", 9184)]
    [InlineData("pid_4_luid_0x00000000_0x00009C4A_phys_0_eng_3_engtype_High Priority 3D", 4)]
    [InlineData("pid_23516_luid_0x00000000_0x0000FA31_phys_1_eng_0_engtype_VideoDecode", 23516)]
    [InlineData("PID_9184_luid_0x00000000_phys_0_eng_0_engtype_3D", 9184)]
    public void TryParsePidFromInstanceName_reads_the_pid_prefix(string instance, int expected)
        => Assert.Equal(expected, GpuCounterProvider.TryParsePidFromInstanceName(instance));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("luid_0x00000000_0x0000C1DA_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_luid_0x00000000_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_abcd_luid_0x00000000_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_0_luid_0x00000000_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_-12_luid_0x00000000_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_99999999999999_luid_0x00000000_phys_0_eng_0_engtype_3D")]
    public void TryParsePidFromInstanceName_returns_zero_for_unusable_names(string instance)
        => Assert.Equal(0, GpuCounterProvider.TryParsePidFromInstanceName(instance));

    [Fact]
    public void AccumulatePerProcess_sums_every_engine_of_the_same_pid()
    {
        var map = new Dictionary<int, double>();

        GpuCounterProvider.AccumulatePerProcess(map, "pid_9184_luid_0x0_phys_0_eng_0_engtype_3D", 34.5);
        GpuCounterProvider.AccumulatePerProcess(map, "pid_9184_luid_0x0_phys_0_eng_1_engtype_High Priority 3D", 12.25);
        GpuCounterProvider.AccumulatePerProcess(map, "pid_1200_luid_0x0_phys_0_eng_0_engtype_3D", 4);

        Assert.Equal(46.75, map[9184]);
        Assert.Equal(4, map[1200]);
    }

    [Fact]
    public void AccumulatePerProcess_ignores_unusable_names_and_non_positive_values()
    {
        var map = new Dictionary<int, double>();

        GpuCounterProvider.AccumulatePerProcess(map, "engtype_3D", 40);
        GpuCounterProvider.AccumulatePerProcess(map, "pid_9184_luid_0x0_phys_0_eng_0_engtype_3D", 0);
        GpuCounterProvider.AccumulatePerProcess(map, "pid_9184_luid_0x0_phys_0_eng_0_engtype_3D", -3);

        Assert.Empty(map);
    }

    [Fact]
    public void AccumulatePerProcess_clamps_each_pid_at_one_hundred()
    {
        var map = new Dictionary<int, double>();

        GpuCounterProvider.AccumulatePerProcess(map, "pid_9184_luid_0x0_phys_0_eng_0_engtype_3D", 80);
        GpuCounterProvider.AccumulatePerProcess(map, "pid_9184_luid_0x0_phys_0_eng_1_engtype_3D", 80);

        Assert.Equal(100, map[9184]);
    }

    [Fact]
    public void Gpu_sample_is_reused_for_two_seconds()
    {
        var t0 = DateTime.UnixEpoch;

        Assert.False(GpuCounterProvider.IsSampleFresh(DateTime.MinValue, t0));
        Assert.True(GpuCounterProvider.IsSampleFresh(t0, t0.AddMilliseconds(1999)));
        Assert.False(GpuCounterProvider.IsSampleFresh(t0, t0.AddSeconds(2)));
    }

    [Fact]
    public void Pressure_only_gpu_sample_is_reused_for_five_seconds()
    {
        var t0 = DateTime.UnixEpoch;
        var interval = TimeSpan.FromSeconds(5);

        Assert.True(GpuCounterProvider.IsSampleFresh(t0, t0.AddMilliseconds(4999), interval));
        Assert.False(GpuCounterProvider.IsSampleFresh(t0, t0.AddSeconds(5), interval));
    }

    [Fact]
    public void Dispose_does_not_wait_for_a_gate_held_by_a_slow_enumeration()
    {
        var provider = new GpuCounterProvider();
        object gate = typeof(GpuCounterProvider)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(provider)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        }) { IsBackground = true };
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        provider.Dispose();
        watch.Stop();
        release.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(5)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Dispose blocked for {watch.Elapsed}");
        Assert.Equal(0, provider.Read(TimeSpan.Zero, collectPerProcess: false, force: true));
    }

    [Fact]
    public void Dispose_is_idempotent_and_read_after_dispose_returns_zero()
    {
        var provider = new GpuCounterProvider();

        provider.Dispose();
        provider.Dispose();
        provider.Reset();

        Assert.Equal(0, provider.Read(TimeSpan.Zero, collectPerProcess: true, force: true));
    }
}
