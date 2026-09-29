using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace VoltManager.Tests;

public sealed class NoEmptyCatchTests
{
    private static readonly Regex EmptyCatch = new(
        @"catch(\s*\([^)]*\))?\s*\{\s*\}",
        RegexOptions.Compiled);

    [Fact]
    public void Source_has_no_empty_catch_blocks()
    {
        string root = FindRepoRoot();
        string src = Path.Combine(root, "src");
        var failures = new List<string>();

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/bin/") || normalized.Contains("/obj/"))
                continue;

            string text = File.ReadAllText(file);
            foreach (Match match in EmptyCatch.Matches(text))
            {
                int line = text.Take(match.Index).Count(ch => ch == '\n') + 1;
                failures.Add(Path.GetRelativePath(root, file) + ":" + line);
            }
        }

        Assert.True(failures.Count == 0,
            "Empty catch blocks found:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static string FindRepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (string start in new[]
        {
            Path.GetDirectoryName(sourceFile) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        })
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;
            string? directory = start;
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory, "src")) && File.Exists(Path.Combine(directory, "VoltManager.sln")))
                    return directory;
                directory = Directory.GetParent(directory)?.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
