using System.IO;
using System.Text.RegularExpressions;

namespace VoltManager.Tests;

public class ContentSecurityPolicyTests
{
    [Theory]
    [InlineData("index.html")]
    [InlineData("widgets.html")]
    public void Web_pages_enforce_script_safe_content_security_policy(string page)
    {
        string html = LocateWebAsset(page);
        Match headStart = Regex.Match(html, @"<head>\s*(?<first><meta\b[^>]*>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(headStart.Success, $"{page} must have a meta element as the first element in head.");
        string cspMeta = headStart.Groups["first"].Value;
        Assert.True(cspMeta.Contains("http-equiv=\"Content-Security-Policy\"", StringComparison.OrdinalIgnoreCase),
            $"{page} must place the CSP meta first in head.");

        Match content = Regex.Match(cspMeta, @"\bcontent=""(?<policy>[^""]*)""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.True(content.Success, $"{page} CSP meta must have a content attribute.");
        string policy = content.Groups["policy"].Value;

        Match scriptDirective = Regex.Match(policy, @"(?:^|;)\s*script-src\s+(?<value>[^;]+)",
            RegexOptions.IgnoreCase);
        Assert.True(scriptDirective.Success, $"{page} CSP must define script-src.");
        string scriptSources = scriptDirective.Groups["value"].Value;
        Assert.Contains("'self'", scriptSources);
        Assert.False(scriptSources.Contains("'unsafe-inline'", StringComparison.OrdinalIgnoreCase));
        Assert.False(scriptSources.Contains("'unsafe-eval'", StringComparison.OrdinalIgnoreCase));

        foreach (Match script in Regex.Matches(html, @"<script\b(?<attrs>[^>]*)>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline).Cast<Match>())
        {
            Assert.Matches(@"\bsrc\s*=", script.Groups["attrs"].Value);
        }

        Assert.False(Regex.IsMatch(html, @"\son[a-z]+\s*=\s*""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline));
    }

    private static string LocateWebAsset(params string[] pathParts)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory, "src", "VoltManager", "wwwroot" }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate src/VoltManager/wwwroot/" + string.Join('/', pathParts));
    }
}
