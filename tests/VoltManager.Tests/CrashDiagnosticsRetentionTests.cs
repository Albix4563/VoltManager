using System.IO;
using VoltManager.Reliability;

namespace VoltManager.Tests;

public sealed class CrashDiagnosticsRetentionTests
{
    [Fact]
    public void More_than_twenty_crash_files_are_pruned_to_newest_twenty()
    {
        string root = CreateRoot();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < 25; i++)
                WriteCrash(root, now.AddMinutes(-i), i);

            CrashDiagnostics.PruneDirectory(root, now: now);

            string[] remaining = Directory.GetFiles(root, "crash-*.json");
            Assert.Equal(20, remaining.Length);
            Assert.DoesNotContain(remaining, path => Path.GetFileName(path).Contains("-24-", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Crash_files_older_than_thirty_days_are_deleted()
    {
        string root = CreateRoot();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string old = WriteCrash(root, now.AddDays(-31), 1);
            string recent = WriteCrash(root, now.AddDays(-2), 2);

            CrashDiagnostics.PruneDirectory(root, now: now);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Unrelated_and_malformed_files_are_untouched()
    {
        string root = CreateRoot();
        try
        {
            string unrelated = Path.Combine(root, "notes.json");
            string malformed = Path.Combine(root, "crash-not-ours.json");
            File.WriteAllText(unrelated, "x");
            File.WriteAllText(malformed, "x");

            CrashDiagnostics.PruneDirectory(root, now: DateTimeOffset.UtcNow.AddYears(1));

            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(malformed));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Just_written_file_is_never_deleted()
    {
        string root = CreateRoot();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string protectedPath = WriteCrash(root, now.AddDays(-60), 999);
            for (int i = 0; i < 25; i++) WriteCrash(root, now.AddMinutes(-i), i);

            CrashDiagnostics.PruneDirectory(root, protectedPath, now);

            Assert.True(File.Exists(protectedPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Locked_undeletable_file_does_not_throw()
    {
        string root = CreateRoot();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string locked = WriteCrash(root, now.AddDays(-60), 1);
            using var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Exception? error = Record.Exception(() => CrashDiagnostics.PruneDirectory(root, now: now));

            Assert.Null(error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Missing_directory_is_ignored()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        Assert.Null(Record.Exception(() => CrashDiagnostics.PruneDirectory(root)));
    }

    [Fact]
    public void Capture_keeps_new_report_while_pruning_existing_files()
    {
        string root = CreateRoot();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < 24; i++) WriteCrash(root, now.AddMinutes(-i - 1), i);

            string? written = CrashDiagnostics.CaptureToDirectory(root, "test", new InvalidOperationException("expected"), 1);

            Assert.NotNull(written);
            Assert.True(File.Exists(written));
            Assert.True(Directory.GetFiles(root, "crash-*.json").Length <= 20);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteCrash(string root, DateTimeOffset timestamp, int pid)
    {
        string path = Path.Combine(root, $"crash-{timestamp.UtcDateTime:yyyyMMdd-HHmmssfff}-{pid}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        return path;
    }
}
