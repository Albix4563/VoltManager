using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoltManager.Bridge;

/// <summary>
/// Pure RPC reply formatting and dispatch-failure policy used by <see cref="HostBridge"/>.
/// Failures stay non-fatal to the process: the caller receives ok:false + error text.
/// </summary>
public static class BridgeRpc
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Outcome of a failed bridge dispatch. Logging is the caller's responsibility;
    /// this type only decides the reply shape so the process never crashes solely
    /// because a method threw.
    /// </summary>
    public sealed record DispatchFailure(string? Id, string ErrorMessage, string LogMessage)
    {
        public bool ShouldReply => Id != null;
    }

    public static DispatchFailure OnDispatchException(string? id, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        return new DispatchFailure(
            Id: id,
            ErrorMessage: message,
            LogMessage: "Bridge message handling failed (id: " + (id ?? "none") + ")");
    }

    public static string FormatSuccess(string id, object? result)
        => JsonSerializer.Serialize(new { id, ok = true, result }, JsonOpts);

    public static string FormatFailure(string id, string? errorMessage)
        => JsonSerializer.Serialize(new
        {
            id,
            ok = false,
            error = string.IsNullOrWhiteSpace(errorMessage) ? "errore" : errorMessage,
        }, JsonOpts);

    public static string FormatFailure(string id, Exception ex)
        => FormatFailure(id, OnDispatchException(id, ex).ErrorMessage);

    /// <summary>
    /// Safe handling of the JS <c>logError</c> method: never throws into the dispatch
    /// loop. Returns a success payload matching the existing host contract.
    /// </summary>
    public static object HandleLogError(string? message, string? stack, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            string body = "[UI] " + (message ?? "");
            if (!string.IsNullOrEmpty(stack))
                body += "\n" + stack;
            log(body);
        }
        catch
        {
            // Logging must never become a new failure path for the bridge.
        }

        return new { success = true };
    }
}
