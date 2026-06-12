using VoltManager.Setup.Engine;

namespace VoltManager.Tests;

public class SetupArgsTests
{
    [Fact]
    public void NoArgs_IsWizard()
    {
        var a = SetupArgs.Parse([]);
        Assert.Equal(SetupMode.Wizard, a.Mode);
    }

    [Theory]
    [InlineData("/SILENT")]
    [InlineData("/VERYSILENT")]
    [InlineData("/silent")]
    public void SilentFlags_AreSilent(string flag)
    {
        var a = SetupArgs.Parse([flag]);
        Assert.Equal(SetupMode.Silent, a.Mode);
    }

    [Fact]
    public void UpdateFlag_ParsesPid()
    {
        var a = SetupArgs.Parse(["/update", "--pid", "1234"]);
        Assert.Equal(SetupMode.Update, a.Mode);
        Assert.Equal(1234, a.WaitPid);
    }

    [Fact]
    public void UpdateFlag_NoPid_ZeroPid()
    {
        var a = SetupArgs.Parse(["/update"]);
        Assert.Equal(SetupMode.Update, a.Mode);
        Assert.Equal(0, a.WaitPid);
    }

    [Fact]
    public void UpdateFlag_ParsesTargetDir()
    {
        var a = SetupArgs.Parse(["/update", "--target", @"C:\Program Files\VoltManager"]);
        Assert.Equal(@"C:\Program Files\VoltManager", a.TargetDir);
    }

    [Fact]
    public void UninstallFlag_IsUninstall()
    {
        var a = SetupArgs.Parse(["/uninstall"]);
        Assert.Equal(SetupMode.Uninstall, a.Mode);
        Assert.False(a.SilentUninstall);
    }

    [Fact]
    public void UninstallSilent_IsSilentUninstall()
    {
        var a = SetupArgs.Parse(["/uninstall", "/SILENT"]);
        Assert.Equal(SetupMode.Uninstall, a.Mode);
        Assert.True(a.SilentUninstall);
    }

    [Fact]
    public void NullArgs_IsWizard()
    {
        var a = SetupArgs.Parse(null!);
        Assert.Equal(SetupMode.Wizard, a.Mode);
    }
}
