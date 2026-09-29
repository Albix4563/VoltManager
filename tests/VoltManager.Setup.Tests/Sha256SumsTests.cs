using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests;

public sealed class Sha256SumsTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string HashB = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void Parser_accepts_strict_two_space_entries()
    {
        IReadOnlyDictionary<string, string> parsed = GitHubPreviewReleaseClient.ParseSha256Sums(
            HashA + "  VoltManagerSetup.exe\n" +
            HashB.ToUpperInvariant() + "  other.exe\n");

        Assert.Equal(HashA, parsed["VoltManagerSetup.exe"]);
        Assert.Equal(HashB, parsed["other.exe"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(HashA + " VoltManagerSetup.exe\n")]
    [InlineData(HashA + " *VoltManagerSetup.exe\n")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  VoltManagerSetup.exe\n")]
    public void Parser_rejects_malformed_lines(string text)
    {
        Assert.Throws<InvalidOperationException>(() => GitHubPreviewReleaseClient.ParseSha256Sums(text));
    }

    [Fact]
    public void Parser_rejects_duplicate_asset_names()
    {
        string text =
            HashA + "  VoltManagerSetup.exe\n" +
            HashB + "  VoltManagerSetup.exe\n";

        Assert.Throws<InvalidOperationException>(() => GitHubPreviewReleaseClient.ParseSha256Sums(text));
    }
}
