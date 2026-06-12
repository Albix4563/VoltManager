using VoltManager.Services;

namespace VoltManager.Tests;

public class RemoteCommandProtocolTests
{
    [Theory]
    [InlineData("powerSaver")]
    [InlineData("balanced")]
    [InlineData("performance")]
    [InlineData("auto")]
    public void ParsePlanArg_ValidKey_ReturnsKey(string key)
    {
        Assert.Equal(key, RemoteCommandProtocol.ParsePlanArg(new[] { "--plan", key }));
    }

    [Fact]
    public void ParsePlanArg_ArgNameIsCaseInsensitive()
    {
        Assert.Equal("balanced", RemoteCommandProtocol.ParsePlanArg(new[] { "--PLAN", "balanced" }));
    }

    [Fact]
    public void ParsePlanArg_IgnoresSurroundingArgs()
    {
        var args = new[] { "--updated", "--plan", "performance", "--minimized" };
        Assert.Equal("performance", RemoteCommandProtocol.ParsePlanArg(args));
    }

    [Fact]
    public void ParsePlanArg_Invalid_ReturnsNull()
    {
        Assert.Null(RemoteCommandProtocol.ParsePlanArg(Array.Empty<string>()));
        Assert.Null(RemoteCommandProtocol.ParsePlanArg(new[] { "--plan" }));
        Assert.Null(RemoteCommandProtocol.ParsePlanArg(new[] { "--plan", "turbo" }));
        // Keys are case-sensitive (they match the settings/bridge plan keys).
        Assert.Null(RemoteCommandProtocol.ParsePlanArg(new[] { "--plan", "PowerSaver" }));
        Assert.Null(RemoteCommandProtocol.ParsePlanArg(new[] { "powerSaver" }));
    }

    [Fact]
    public void EventName_UsesKey()
    {
        Assert.Equal("VoltManager_PlanCmd_auto", RemoteCommandProtocol.EventName("auto"));
    }

    [Fact]
    public void IsValidKey_RejectsNullAndUnknown()
    {
        Assert.False(RemoteCommandProtocol.IsValidKey(null));
        Assert.False(RemoteCommandProtocol.IsValidKey("max"));
        Assert.True(RemoteCommandProtocol.IsValidKey("powerSaver"));
    }
}
