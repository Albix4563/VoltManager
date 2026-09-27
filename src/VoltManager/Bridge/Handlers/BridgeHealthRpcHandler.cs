using System.Text.Json;
using VoltManager.Bridge.Rpc;

namespace VoltManager.Bridge.Handlers;

public sealed class BridgeHealthRpcHandler : IBridgeRpcHandler
{
    private static readonly string[] RegisteredMethods = ["bridge.ping", "bridge.requestFreshState"];
    private readonly Action _requestFreshState;

    public BridgeHealthRpcHandler(Action requestFreshState)
        => _requestFreshState = requestFreshState ?? throw new ArgumentNullException(nameof(requestFreshState));

    public IReadOnlyCollection<string> Methods => RegisteredMethods;

    public Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(method, "bridge.ping", StringComparison.Ordinal))
            return Task.FromResult<object?>(new { alive = true });
        if (string.Equals(method, "bridge.requestFreshState", StringComparison.Ordinal))
        {
            _requestFreshState();
            return Task.FromResult<object?>(new { published = true });
        }
        throw new ArgumentException("Unknown bridge health method.", nameof(method));
    }
}
