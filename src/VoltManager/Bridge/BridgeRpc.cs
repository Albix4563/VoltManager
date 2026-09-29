using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace VoltManager.Bridge;

/// <summary>
/// Pure RPC reply formatting and dispatch-failure policy used by <see cref="HostBridge"/>.
/// Failures stay non-fatal to the process: the caller receives ok:false + error text.
/// </summary>
public static class BridgeRpc
{
    private const int MaxUiLogChars = 2048;
    private const string TruncatedSuffix = "…[truncated]";
    internal static readonly LogErrorRateLimiter ProcessLogErrorRateLimiter = new();

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

    public static string FormatEvent(string name, object data)
        => JsonSerializer.Serialize(new { @event = name, data }, JsonOpts);

    /// <summary>
    /// Safe handling of the JS <c>logError</c> method: never throws into the dispatch
    /// loop. Returns a success payload matching the existing host contract.
    /// </summary>
    public static object HandleLogError(string? message, string? stack, Action<string> log)
        => HandleLogError(message, stack, log, ProcessLogErrorRateLimiter);

    internal static object HandleLogError(
        string? message,
        string? stack,
        Action<string> log,
        LogErrorRateLimiter rateLimiter)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        try
        {
            string body = message ?? "";
            if (!string.IsNullOrEmpty(stack))
                body += "\n" + stack;

            body = SanitizeUiLogText(body);
            if (rateLimiter.TryAcquire(out bool logRateLimitNotice))
            {
                log("[UI] " + body);
            }
            else if (logRateLimitNotice)
            {
                log("[UI] logError rate limit reached");
            }
        }
        catch
        {
            // Logging must never become a new failure path for the bridge.
        }

        return new { success = true };
    }

    internal static string SanitizeUiLogText(string value)
    {
        var sanitized = new StringBuilder(value.Length);
        foreach (char ch in value)
            sanitized.Append(char.IsControl(ch) ? ' ' : ch);

        if (sanitized.Length <= MaxUiLogChars)
            return sanitized.ToString();

        sanitized.Length = MaxUiLogChars - TruncatedSuffix.Length;
        sanitized.Append(TruncatedSuffix);
        return sanitized.ToString();
    }

    internal sealed class LogErrorRateLimiter
    {
        private const int MaxEntries = 20;
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        private readonly object _gate = new();
        private readonly Queue<DateTimeOffset> _entries = new();
        private readonly Func<DateTimeOffset> _clock;
        private DateTimeOffset _nextRateLimitNotice = DateTimeOffset.MinValue;

        internal LogErrorRateLimiter(Func<DateTimeOffset>? clock = null)
            => _clock = clock ?? (() => DateTimeOffset.UtcNow);

        internal bool TryAcquire(out bool logRateLimitNotice)
        {
            lock (_gate)
            {
                DateTimeOffset now = _clock();
                DateTimeOffset cutoff = now - Window;
                while (_entries.Count > 0 && _entries.Peek() <= cutoff)
                    _entries.Dequeue();

                if (_entries.Count < MaxEntries)
                {
                    _entries.Enqueue(now);
                    logRateLimitNotice = false;
                    return true;
                }

                logRateLimitNotice = now >= _nextRateLimitNotice;
                if (logRateLimitNotice)
                    _nextRateLimitNotice = now + Window;
                return false;
            }
        }
    }
}
