using VoltManager.Bridge.Rpc;

namespace VoltManager.Tests;

public sealed class BridgeLifetimeTests
{
    [Fact]
    public void TryBeginAttach_IsIdempotent()
    {
        using var lifetime = new BridgeLifetime();

        Assert.True(lifetime.TryBeginAttach());
        Assert.False(lifetime.TryBeginAttach());
    }

    [Fact]
    public void Dispose_DetachesExactlyOnce_AndCancelsToken()
    {
        var lifetime = new BridgeLifetime();
        int detachCalls = 0;
        lifetime.RegisterDetach(() => detachCalls++);

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.Equal(1, detachCalls);
        Assert.True(lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_SuppressesReplyFromInflightDispatch()
    {
        var handler = new WaitingHandler();
        var dispatcher = new BridgeRpcDispatcher([handler], method => $"Unknown method: {method}");
        var lifetime = new BridgeLifetime();

        Task<BridgeRpcDispatchResult> dispatch = dispatcher.DispatchAsync(
            "{\"id\":\"req-1\",\"method\":\"wait\",\"payload\":{}}",
            lifetime.Token);

        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Dispose();

        BridgeRpcDispatchResult result = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.ReplyJson);
    }

    private sealed class WaitingHandler : IBridgeRpcHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<string> Methods { get; } = ["wait"];

        public async Task<object?> HandleAsync(string method, System.Text.Json.JsonElement payload, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new { success = true };
        }
    }
}
