using System.Text.Json;

namespace VoltManager.Bridge.Rpc;

public interface IBridgeRpcHandler
{
    IReadOnlyCollection<string> Methods { get; }
    Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken);
}
