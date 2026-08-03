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
}
