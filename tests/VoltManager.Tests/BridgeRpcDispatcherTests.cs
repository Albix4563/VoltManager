using System.Text.Json;
using VoltManager.Bridge.Rpc;

namespace VoltManager.Tests;

public class BridgeRpcDispatcherTests
{
    private sealed class FakeHandler : IBridgeRpcHandler
    {
        private readonly Func<string, JsonElement, CancellationToken, Task<object?>> _handle;
        public IReadOnlyCollection<string> Methods { get; }

        public FakeHandler(IEnumerable<string> methods,
            Func<string, JsonElement, CancellationToken, Task<object?>> handle)
        {
            Methods = methods.ToArray();
            _handle = handle;
        }

        public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
            => _handle(method, payload, cancellationToken);
    }

    [Fact]
    public async Task DispatchAsync_success_preserves_id_and_result()
    {
        var dispatcher = new BridgeRpcDispatcher(
            [new FakeHandler(["echo"], (_, payload, _) =>
                Task.FromResult<object?>(new { value = payload.GetProperty("value").GetInt32() }))],
            method => $"unknown: {method}");

        var result = await dispatcher.DispatchAsync(
            """{"id":"r1","method":"echo","payload":{"value":7}}""", CancellationToken.None);

        Assert.Equal("r1", result.Id);
        Assert.NotNull(result.ReplyJson);
        using var doc = JsonDocument.Parse(result.ReplyJson!);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(7, doc.RootElement.GetProperty("result").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task DispatchAsync_handler_exception_returns_one_failure_for_same_id()
    {
        var dispatcher = new BridgeRpcDispatcher(
            [new FakeHandler(["boom"], (_, _, _) => throw new InvalidOperationException("failure"))],
            method => $"unknown: {method}");

        var result = await dispatcher.DispatchAsync(
            """{"id":"r1","method":"boom","payload":{}}""", CancellationToken.None);

        Assert.Equal("r1", result.Id);
        Assert.NotNull(result.ReplyJson);
        using var doc = JsonDocument.Parse(result.ReplyJson!);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("failure", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DispatchAsync_unknown_method_returns_failure_for_valid_id()
    {
        var dispatcher = new BridgeRpcDispatcher([], method => $"unknown: {method}");
        var result = await dispatcher.DispatchAsync(
            """{"id":"r2","method":"missing","payload":{}}""", CancellationToken.None);

        using var doc = JsonDocument.Parse(result.ReplyJson!);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown: missing", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DispatchAsync_missing_method_returns_one_failure_for_valid_id()
    {
        var dispatcher = new BridgeRpcDispatcher([], method => $"unknown: {method}");
        var result = await dispatcher.DispatchAsync("""{"id":"r3","payload":{}}""", CancellationToken.None);

        Assert.Equal("r3", result.Id);
        Assert.NotNull(result.ReplyJson);
        using var doc = JsonDocument.Parse(result.ReplyJson!);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task DispatchAsync_malformed_json_has_no_reply()
    {
        var dispatcher = new BridgeRpcDispatcher([], method => $"unknown: {method}");
        var result = await dispatcher.DispatchAsync("{", CancellationToken.None);
        Assert.Null(result.Id);
        Assert.Null(result.ReplyJson);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public void Constructor_rejects_duplicate_method_registration()
    {
        var first = new FakeHandler(["same"], (_, _, _) => Task.FromResult<object?>(null));
        var second = new FakeHandler(["same"], (_, _, _) => Task.FromResult<object?>(null));
        Assert.Throws<ArgumentException>(() => new BridgeRpcDispatcher([first, second], m => m));
    }

    [Fact]
    public async Task DispatchAsync_concurrent_requests_keep_their_own_ids()
    {
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(["slow", "fast"], async (method, _, ct) =>
        {
            if (method == "slow")
                await releaseSlow.Task.WaitAsync(ct);
            return new { method };
        });
        var dispatcher = new BridgeRpcDispatcher([handler], m => m);

        Task<BridgeRpcDispatchResult> slow = dispatcher.DispatchAsync(
            """{"id":"slow-id","method":"slow","payload":{}}""", CancellationToken.None);
        BridgeRpcDispatchResult fast = await dispatcher.DispatchAsync(
            """{"id":"fast-id","method":"fast","payload":{}}""", CancellationToken.None);
        releaseSlow.SetResult();
        BridgeRpcDispatchResult slowResult = await slow;

        using var fastDoc = JsonDocument.Parse(fast.ReplyJson!);
        using var slowDoc = JsonDocument.Parse(slowResult.ReplyJson!);
        Assert.Equal("fast-id", fastDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal("slow-id", slowDoc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task DispatchAsync_cancelled_request_has_no_late_reply()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(["wait"], async (_, _, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new { success = true };
        });
        var dispatcher = new BridgeRpcDispatcher([handler], m => m);
        using var cts = new CancellationTokenSource();

        Task<BridgeRpcDispatchResult> task = dispatcher.DispatchAsync(
            """{"id":"cancel-id","method":"wait","payload":{}}""", cts.Token);
        cts.Cancel();
        BridgeRpcDispatchResult result = await task;

        Assert.Equal("cancel-id", result.Id);
        Assert.Null(result.ReplyJson);
    }
}
