using System.IO;

namespace VoltManager.Tests;

public class UiReorganizationContractTests
{
    [Fact]
    public void Reorganized_ui_uses_the_single_rounded_token_scale()
    {
        string css = LocateWebAsset("css", "ui-reorganization.css");

        string[] expectedTokens =
        {
            "--vm-radius-panel: 16px;",
            "--vm-radius-card: 12px;",
            "--vm-radius-control: 10px;",
            "--vm-radius-pill: 999px;",
            "--vm-space-1: 4px;",
            "--vm-space-2: 8px;",
            "--vm-space-3: 12px;",
            "--vm-space-4: 16px;",
            "--vm-space-5: 24px;",
            "--vm-space-6: 32px;",
            "--vm-section-gap: 24px;",
            "--vm-panel-padding: 24px;",
        };

        foreach (string token in expectedTokens)
            Assert.Contains(token, css);

        Assert.DoesNotContain("technical desktop-tool redesign", css);
        Assert.DoesNotContain("--vm-panel-radius: 8px", css);
        Assert.DoesNotContain("--vm-control-radius: 6px", css);
        Assert.DoesNotContain("margin-inline: -4px", css);
    }

    [Fact]
    public void Reorganized_views_use_semantic_shell_classes_and_mark_embedded_legacy_content()
    {
        string layout = LocateWebAsset("js", "ui-reorganization.layout.js");
        string runtime = LocateWebAsset("js", "ui-reorganization.js");

        Assert.Contains("vm-ui-panel vm-section-card", layout);
        Assert.Contains("vm-ui-section", layout);
        Assert.Contains("vm-ui-group", layout);
        Assert.DoesNotContain("glass-panel rounded-xl p-lg vm-section-card", layout);

        Assert.Contains("node.classList.add('vm-legacy-embedded')", runtime);
        Assert.Contains(".vm-legacy-embedded", LocateWebAsset("css", "ui-reorganization.css"));
    }

    [Fact]
    public void Reorganized_typography_and_responsive_shell_match_the_design_contract()
    {
        string css = LocateWebAsset("css", "ui-reorganization.css");

        Assert.Contains("font-size: 28px;", css);
        Assert.Contains("line-height: 34px;", css);
        Assert.Contains("font-size: 15px;", css);
        Assert.Contains("line-height: 22px;", css);
        Assert.Contains("font-size: 17px;", css);
        Assert.Contains("line-height: 24px;", css);
        Assert.Contains("font-size: 14px;", css);
        Assert.Contains("line-height: 20px;", css);
        Assert.Contains("font-size: 12px;", css);
        Assert.Contains("line-height: 16px;", css);
        Assert.Contains("grid-template-columns: 220px minmax(0, 1fr);", css);
        Assert.Contains("@media (max-width: 780px)", css);
        Assert.Contains("@media (max-width: 700px)", css);
    }

    [Fact]
    public void Reorganized_views_prevent_embedded_legacy_overflow_during_intermediate_resize()
    {
        string css = LocateWebAsset("css", "ui-reorganization.css");

        Assert.Contains("@media (max-width: 1100px)", css);
        Assert.Contains(".vm-reorg-view .vm-legacy-embedded :is(.flex, .grid) > *", css);
        Assert.Contains(".vm-reorg-view .vm-legacy-embedded .whitespace-nowrap", css);
        Assert.Contains(".vm-settings-row > :first-child", css);
        Assert.Contains(".vm-settings-row > :last-child", css);
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
