using VoltManager.Services;

namespace VoltManager.Tests;

public class HardwareInfoServiceTests
{
    [Fact]
    public void GetSystemInfo_ReturnsValidSystemInfo()
    {
        var service = new HardwareInfoService();
        var info = service.GetSystemInfo();

        Assert.NotNull(info);
        Assert.NotNull(info.CpuName);
        Assert.NotNull(info.GpuName);
        Assert.NotNull(info.OsVersion);
        Assert.NotNull(info.AppVersion);
        Assert.True(info.LogicalCores == Environment.ProcessorCount);
        Assert.True(info.LogicalCores >= 1);
    }
}
