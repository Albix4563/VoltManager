namespace VoltManager.Services;

internal static class Sha256SumsParser
{
    internal static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string normalized = (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (string rawLine in normalized.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            int separator = FindSeparator(line);
            if (separator < 64)
                throw new FormatException("Malformed SHA256SUMS line.");

            string hash = line[..64];
            if (!IsHex(hash))
                throw new FormatException("Malformed SHA256SUMS hash.");

            string fileName = line[separator..].Trim();
            if (fileName.Length == 0)
                throw new FormatException("Missing SHA256SUMS file name.");
            if (!result.TryAdd(fileName, hash.ToLowerInvariant()))
                throw new FormatException("Duplicate SHA256SUMS file name.");
        }

        if (result.Count == 0)
            throw new FormatException("Empty SHA256SUMS file.");
        return result;
    }

    private static int FindSeparator(string line)
    {
        if (line.Length <= 65)
            return -1;
        int index = 64;
        int whitespace = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            whitespace++;
            index++;
        }
        return whitespace >= 2 ? index : -1;
    }

    private static bool IsHex(string value)
        => value.Length == 64 && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
