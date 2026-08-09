using VoltManager.Services;
using Xunit;

namespace VoltManager.Tests;

public class HardwareServiceClientTests
{
    [Fact]
    public void Client_bootstraps_named_pipe_service_and_allows_isolated_control_transport()
    {
        using HardwareServiceClient? client = HardwareServiceClient.TryStart();

        Assert.NotNull(client);
        Assert.True(client!.ControlWritesAllowed);
    }
}
