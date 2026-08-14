using System.IO;

namespace VoltManager.Tests;

public class ThemeStartupThemeContrastTests
{
    [Fact]
    public void Main_webview_registers_saved_theme_before_first_app_navigation()
    {
        string source = LocateRepoFile("src", "VoltManager", "MainWindow.xaml.cs");

        int bootstrap = source.IndexOf("AddScriptToExecuteOnDocumentCreatedAsync", StringComparison.Ordinal);
        int navigation = source.IndexOf("core.Navigate(\"https://app.local/index.html", StringComparison.Ordinal);

        Assert.True(bootstrap >= 0,
            "MainWindow must inject the saved native theme before the WebView parses index.html.");
        Assert.True(navigation > bootstrap,
            "The theme bootstrap must be registered before navigating to index.html.");
    }

    [Fact]
    public void Theme_script_prefers_preinjected_native_state_over_html_blue_default()
    {
        string source = LocateRepoFile("src", "VoltManager", "wwwroot", "js", "theme.js");

        Assert.Contains("const bootstrapState = window.__voltThemeState", source);
        Assert.Contains("bootstrapState && bootstrapState.themeColor", source);
        Assert.Contains("bootstrapState && bootstrapState.palette", source);
    }

    private static string LocateRepoFile(params string[] pathParts)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate " + string.Join('/', pathParts));
    }
}
