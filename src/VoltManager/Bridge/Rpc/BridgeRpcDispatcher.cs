using System.Collections.ObjectModel;
using System.Text.Json;

namespace VoltManager.Bridge.Rpc;

public sealed class BridgeRpcDispatcher
{
    private readonly IReadOnlyDictionary<string, IBridgeRpcHandler> _handlers;
    private readonly IReadOnlyCollection<string> _methods;
    private readonly Func<string, string> _unknownMethodMessage;

    public BridgeRpcDispatcher(IEnumerable<IBridgeRpcHandler> handlers, Func<string, string> unknownMethodMessage)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(unknownMethodMessage);

        var map = new Dictionary<string, IBridgeRpcHandler>(StringComparer.Ordinal);
        foreach (IBridgeRpcHandler handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            foreach (string method in handler.Methods)
            {
                if (string.IsNullOrWhiteSpace(method))
                    throw new ArgumentException("RPC method names cannot be empty.", nameof(handlers));
                if (!map.TryAdd(method, handler))
                    throw new ArgumentException($"RPC method '{method}' is registered more than once.", nameof(handlers));
            }
        }

        _handlers = new ReadOnlyDictionary<string, IBridgeRpcHandler>(map);
        _methods = Array.AsReadOnly(map.Keys.ToArray());
        _unknownMethodMessage = unknownMethodMessage;
    }

    public IReadOnlyCollection<string> Methods => _methods;

    public async Task<BridgeRpcDispatchResult> DispatchAsync(string json, CancellationToken cancellationToken)
    {
        string? id = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Invalid RPC request.");

            if (!root.TryGetProperty("id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id = idElement.GetString()))
            {
                throw new ArgumentException("Missing RPC id.");
            }

            if (!root.TryGetProperty("method", out JsonElement methodElement)
                || methodElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(methodElement.GetString()))
            {
                throw new ArgumentException("Missing RPC method.");
            }

            string method = methodElement.GetString()!;
            JsonElement payload = root.TryGetProperty("payload", out JsonElement rawPayload)
                ? rawPayload.Clone()
                : default;

            if (!_handlers.TryGetValue(method, out IBridgeRpcHandler? handler))
                throw new ArgumentException(_unknownMethodMessage(method));

            object? result = await handler.HandleAsync(method, payload, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return BridgeRpcDispatchResult.Success(id, BridgeRpc.FormatSuccess(id, result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BridgeRpcDispatchResult.Cancelled(id);
        }
        catch (Exception ex)
        {
            BridgeRpc.DispatchFailure failure = BridgeRpc.OnDispatchException(id, ex);
            return failure.ShouldReply
                ? BridgeRpcDispatchResult.Failure(
                    failure.Id!,
                    BridgeRpc.FormatFailure(failure.Id!, failure.ErrorMessage),
                    failure.LogMessage,
                    ex)
                : BridgeRpcDispatchResult.UncorrelatedFailure(failure.LogMessage, ex);
        }
    }
}
