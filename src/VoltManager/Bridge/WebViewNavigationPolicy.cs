using System.Diagnostics;

namespace VoltManager.Bridge;

internal static class WebViewNavigationPolicy
{
    private const string AppHost = "app.local";

    internal static bool IsTrustedAppUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
           && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           && string.Equals(uri.Host, AppHost, StringComparison.OrdinalIgnoreCase)
           && uri.IsDefaultPort;

    internal static bool IsAllowedTopLevelUri(string? value)
        => IsTrustedAppUri(value)
           || (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
               && string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase));

    internal static bool IsExternalHttpUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
           && !IsTrustedAppUri(value);

    internal static void OpenExternal(string url)
    {
        // Called from WebView2 event handlers: a failing shell launch must never
        // escalate into the UI unhandled-exception policy.
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Services.Logger.Warn("Could not open external link: " + ex.Message); }
    }
}
