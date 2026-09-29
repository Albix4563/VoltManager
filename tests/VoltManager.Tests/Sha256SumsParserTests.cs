using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class Sha256SumsParserTests
{
    [Fact]
    public void Parses_valid_lines_extra_whitespace_crlf_and_uppercase_hex()
    {
        string lower = new('a', 64);
        string upper = new('B', 64);
        string content = $"{lower}  VoltManagerSetup.exe\r\n  {upper}    PreviewSetup.exe  \r\n";

        IReadOnlyDictionary<string, string> parsed = Sha256SumsParser.Parse(content);

        Assert.Equal(lower, parsed["VoltManagerSetup.exe"]);
        Assert.Equal(upper.ToLowerInvariant(), parsed["PreviewSetup.exe"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc  VoltManagerSetup.exe")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz  VoltManagerSetup.exe")]
    public void Rejects_malformed_content(string content)
        => Assert.Throws<FormatException>(() => Sha256SumsParser.Parse(content));

    [Fact]
    public void Rejects_single_space_separator()
    {
        string content = new string('a', 64) + " VoltManagerSetup.exe";
        Assert.Throws<FormatException>(() => Sha256SumsParser.Parse(content));
    }
}
