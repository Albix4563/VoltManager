using System.IO;

namespace VoltManager.Tests;

public class ThemeStartupThemeContrastTests
{
    [Fact]
    public void Main_webview_registers_saved_theme_before_first_app_navigation()
    {
        string source = LocateRepoFile("src", "VoltManager", "MainWindow.ThemeBootstrap.cs");

        Assert.Contains("CoreWebView2InitializationCompleted", source);
        Assert.Contains("JsonSerializer.Serialize(_app.Theme.GetWebTheme())", source);
        Assert.Contains("AddScriptToExecuteOnDocumentCreatedAsync", source);
        Assert.Contains("core.NavigationStarting += OnThemeBootstrapNavigationStarting", source);
        Assert.Contains("e.Cancel = true;", source);
        Assert.Contains("await registration;", source);
    }

    [Fact]
    public void Theme_script_prefers_preinjected_native_state_over_html_blue_default()
    {
        string source = LocateRepoFile("src", "VoltManager", "wwwroot", "js", "theme.js");

        Assert.Contains("const bootstrapState = window.__voltThemeState", source);
        Assert.Contains("bootstrapState && bootstrapState.themeColor", source);
        Assert.Contains("bootstrapState && bootstrapState.palette", source);
    }

    [Fact]
    public void Theme_bootstrap_refreshes_when_native_theme_changes_for_future_reloads()
    {
        string source = LocateRepoFile("src", "VoltManager", "MainWindow.ThemeBootstrap.cs");

        Assert.Contains("_app.Theme.ThemeChanged += OnThemeBootstrapThemeChanged", source);
        Assert.Contains("RegisterCurrentThemeBootstrapAsync(core)", source);
        Assert.Contains("RemoveScriptToExecuteOnDocumentCreated", source);
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
