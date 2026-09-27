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
    public async Task Permissions_AreDispatchedAsFiveIndependentFlags()
    {
        (bool Plan, bool Shutdown, bool Restart, bool Sleep, bool Hibernate)? received = null;
        var expected = new LanRemoteControlState { Enabled = true, Port = 51737 };
        var handler = new LanRemoteControlRpcHandler(new LanRemoteControlRpcActions(
            () => expected,
            (_, _) => Task.FromResult(new LanRemoteEnableResult(expected, null)),
            (plan, shutdown, restart, sleep, hibernate) =>
            {
                received = (plan, shutdown, restart, sleep, hibernate);
                return expected;
            },
            () => "1234",
            _ => expected));

        using JsonDocument document = JsonDocument.Parse("""{"allowPlanChange":true,"allowShutdown":false,"allowRestart":true,"allowSleep":true,"allowHibernate":false}""");
        object? result = await handler.HandleAsync(
            "setLanRemoteControlPermissions",
            document.RootElement,
            CancellationToken.None);

        Assert.Equal((true, false, true, true, false), received);
        Assert.Same(expected, result);
    }
}
