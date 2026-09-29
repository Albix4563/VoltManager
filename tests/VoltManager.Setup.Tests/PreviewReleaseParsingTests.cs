using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests;

public sealed class PreviewReleaseParsingTests
{
    [Fact]
    public void Json_parser_preserves_beta_release_and_first_exe_selection_semantics()
    {
        const string json = """
            [
              {
                "tag_name": "v1.9.0",
                "prerelease": false,
                "assets": [
                  { "name": "VoltManagerSetup-1.9.0.exe", "browser_download_url": "https://example.test/stable.exe" },
                  { "name": "SHA256SUMS", "browser_download_url": "https://example.test/stable-sums" }
                ]
              },
              {
                "tag_name": "v2.0.0-alpha-beta",
                "prerelease": true,
                "assets": [
                  { "name": "alpha.exe", "browser_download_url": "https://example.test/alpha.exe" },
                  { "name": "SHA256SUMS", "browser_download_url": "https://example.test/alpha-sums" }
                ]
              },
              {
                "tag_name": "v2.0.0-beta.2",
                "prerelease": true,
                "assets": [
                  { "name": "notes.zip", "browser_download_url": "https://example.test/notes.zip" },
                  { "name": "VoltManagerSetup-2.0.0-beta.2.exe", "browser_download_url": "https://example.test/first.exe" },
                  { "name": "VoltManagerPortable.exe", "browser_download_url": "https://example.test/second.exe" },
                  { "name": "SHA256SUMS", "browser_download_url": "https://example.test/SHA256SUMS" }
                ]
              }
            ]
            """;

        PreviewReleaseSelection selected = GitHubPreviewReleaseClient.SelectPreviewRelease(json);

        Assert.Equal("2.0.0-beta.2", selected.Version);
        Assert.Equal("VoltManagerSetup-2.0.0-beta.2.exe", selected.ExeName);
        Assert.Equal("https://example.test/first.exe", selected.ExeUrl);
        Assert.Equal("https://example.test/SHA256SUMS", selected.ChecksumsUrl);
    }
}
