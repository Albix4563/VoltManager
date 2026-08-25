using VoltManager.Services;
using Xunit;

namespace VoltManager.Tests;

public class HardwareServiceClientTests
{
    [Fact]
    public void Client_bootstraps_named_pipe_service_for_isolated_monitoring()
    {
        using HardwareServiceClient? client = HardwareServiceClient.TryStart();
        Assert.NotNull(client);
    }
}
