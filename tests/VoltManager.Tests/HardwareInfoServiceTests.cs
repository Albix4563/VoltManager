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
        // HasBattery should be a boolean (which it is, value type)
        // We verify that it doesn't throw and runs correctly.
    }
}
