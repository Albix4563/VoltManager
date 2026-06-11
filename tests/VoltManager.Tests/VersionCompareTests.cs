using VoltManager.Services;

namespace VoltManager.Tests;

public class VersionCompareTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2.0", "1.9.9", 1)]
    [InlineData("1.0.0-beta", "1.0.0", 0)]
    [InlineData("10.0.0", "9.0.0", 1)]
    public void Compare(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(UpdateService.CompareVersions(a, b)));
    }
}
