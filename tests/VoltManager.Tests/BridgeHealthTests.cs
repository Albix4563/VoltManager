using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Bridge.Rpc;

namespace VoltManager.Tests;

public sealed class BridgeHealthTests
{
    [Fact]
    public async Task Ping_is_lightweight_and_does_not_publish_state()
    {
        int freshStateRequests = 0;
        var handler = new BridgeHealthRpcHandler(() => freshStateRequests++);

        object? result = await handler.HandleAsync("bridge.ping", default, CancellationToken.None);

        Assert.Equal(0, freshStateRequests);
        Assert.Contains("\"alive\":true", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Fresh_state_request_invokes_host_callback_once()
    {
        int freshStateRequests = 0;
        var handler = new BridgeHealthRpcHandler(() => freshStateRequests++);

        object? result = await handler.HandleAsync(
            "bridge.requestFreshState",
            default,
            CancellationToken.None);

        Assert.Equal(1, freshStateRequests);
        Assert.Contains("\"published\":true", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Background_reply_is_enqueued_without_blocking_caller()
    {
        int posts = 0;
        Action? queued = null;

        HostBridge.DispatchReply(false, () => posts++, action => queued = action);

        Assert.Equal(0, posts);
        Assert.NotNull(queued);
        queued();
        Assert.Equal(1, posts);
    }

    [Fact]
    public void Ui_thread_reply_posts_inline()
    {
        int posts = 0;
        int queued = 0;

        HostBridge.DispatchReply(true, () => posts++, _ => queued++);

        Assert.Equal(1, posts);
        Assert.Equal(0, queued);
    }

    [Fact]
    public async Task Handler_exception_does_not_poison_dispatcher_for_next_message()
    {
        var dispatcher = new BridgeRpcDispatcher([new FaultThenSuccessHandler()], method => method);

        BridgeRpcDispatchResult failed = await dispatcher.DispatchAsync(
            """{"id":"first","method":"fail","payload":{}}""",
            CancellationToken.None);
        BridgeRpcDispatchResult succeeded = await dispatcher.DispatchAsync(
            """{"id":"second","method":"ok","payload":{}}""",
            CancellationToken.None);

        Assert.NotNull(failed.Exception);
        Assert.Contains("\"ok\":false", failed.ReplyJson);
        Assert.Null(succeeded.Exception);
        Assert.Contains("\"ok\":true", succeeded.ReplyJson);
    }

    private sealed class FaultThenSuccessHandler : IBridgeRpcHandler
    {
        public IReadOnlyCollection<string> Methods => ["fail", "ok"];

        public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
            => method == "fail"
                ? Task.FromException<object?>(new InvalidOperationException("synthetic handler failure"))
                : Task.FromResult<object?>("ok");
    }
}
