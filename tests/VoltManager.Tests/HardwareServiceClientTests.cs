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

    [Fact]
    public void Deferred_access_is_non_blocking_and_forwards_when_ready()
    {
        using var release = new ManualResetEventSlim();
        var inner = new StubHardwareAccess();
        using var access = new DeferredHardwareAccess(() =>
        {
            release.Wait();
            return inner;
        });

        Assert.False(access.Available);
        Assert.Same(SensorReport.Empty, access.Read());

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => access.Available, TimeSpan.FromSeconds(2)));
        Assert.Same(inner.Report, access.Read());
    }

    private sealed class StubHardwareAccess : IHardwareAccess
    {
        public SensorReport Report { get; } = new();
        public bool Available => true;
        public SensorReport Read(bool force = false) => Report;
        public void Invalidate() { }
        public void Dispose() { }
    }
}
