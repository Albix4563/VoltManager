using System.Text.Json;
using VoltManager.Bridge.Handlers;
using VoltManager.Models;
using VoltManager.Services.LanRemote;

namespace VoltManager.Tests;

public sealed class LanRemoteControlRpcHandlerTests
{
    [Fact]
    public void RegistersDedicatedRemoteControlMethods()
    {
        Assert.Equal(
            [
                "getLanRemoteControlState",
                "setLanRemoteControlEnabled",
                "setLanRemoteControlPermissions",
                "generateLanRemoteControlPin",
                "setLanRemoteControlPin",
            ],
            LanRemoteControlRpcHandler.MethodNames);
    }

    [Fact]
    public async Task Permissions_AreDispatchedAsThreeIndependentFlags()
    {
        (bool Plan, bool Shutdown, bool Restart)? received = null;
        var expected = new LanRemoteControlState { Enabled = true, Port = 51737 };
        var handler = new LanRemoteControlRpcHandler(new LanRemoteControlRpcActions(
            () => expected,
            (_, _) => Task.FromResult(new LanRemoteEnableResult(expected, null)),
            (plan, shutdown, restart) =>
            {
                received = (plan, shutdown, restart);
                return expected;
            },
            () => "123456789012",
            _ => expected));

        using JsonDocument document = JsonDocument.Parse("""{"allowPlanChange":true,"allowShutdown":false,"allowRestart":true}""");
        object? result = await handler.HandleAsync(
            "setLanRemoteControlPermissions",
            document.RootElement,
            CancellationToken.None);

        Assert.Equal((true, false, true), received);
        Assert.Same(expected, result);
    }
}
