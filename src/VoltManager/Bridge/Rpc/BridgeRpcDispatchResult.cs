namespace VoltManager.Bridge.Rpc;

public sealed record BridgeRpcDispatchResult(
    string? Id,
    string? ReplyJson,
    string? LogMessage,
    Exception? Exception)
{
    public bool ShouldReply => ReplyJson != null;

    public static BridgeRpcDispatchResult Success(string id, string replyJson)
        => new(id, replyJson, null, null);

    public static BridgeRpcDispatchResult Failure(string id, string replyJson, string logMessage, Exception exception)
        => new(id, replyJson, logMessage, exception);

    public static BridgeRpcDispatchResult UncorrelatedFailure(string logMessage, Exception exception)
        => new(null, null, logMessage, exception);

    public static BridgeRpcDispatchResult Cancelled(string? id)
        => new(id, null, null, null);
}
