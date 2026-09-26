using VoltManager.Bridge;

namespace VoltManager.Tests;

public sealed class WebViewNavigationPolicyTests
{
    [Theory]
    [InlineData("https://app.local/index.html", true)]
    [InlineData("https://APP.LOCAL/widget.html?type=usage", true)]
    [InlineData("https://app.local.evil.example/", false)]
    [InlineData("http://app.local/", false)]
    [InlineData("https://app.local:444/", false)]
    [InlineData("https://github.com/Albix4563/power_efficency", false)]
    public void Trusted_origin_requires_exact_https_app_host(string url, bool expected)
        => Assert.Equal(expected, WebViewNavigationPolicy.IsTrustedAppUri(url));

    [Theory]
    [InlineData("https://github.com/Albix4563/power_efficency", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("https://app.local/index.html", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    public void External_navigation_only_accepts_http_and_https(string url, bool expected)
        => Assert.Equal(expected, WebViewNavigationPolicy.IsExternalHttpUri(url));
}
