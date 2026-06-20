using System;
using System.IO;
using System.Linq;
using Xunit;
using VoltManager.Services;

namespace VoltManager.Tests;

// Logger underpins all error handling, so its three contracts are pinned here:
// it must never throw, it must rotate at the size cap, and WarnOnce must log
// only on the first failure of a streak.
public class LoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LoggerTests_" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "voltmanager.log");
    private string RolledPath => Path.Combine(_dir, "voltmanager.1.log");

    public LoggerTests() => Logger.ResetForTests(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void NeverThrows_OnAnyInput()
    {
        // Including the case that used to throw before Write's guard: a null exception.
        var ex = Record.Exception(() =>
        {
            Logger.Info("info");
            Logger.Warn("warn");
            Logger.Error("error");
            Logger.Error("with exception", new InvalidOperationException("boom"));
            Logger.Error(new InvalidOperationException("boom"));
            Logger.Error((Exception?)null);
            Logger.WarnOnce(false, "once", null);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void RotatesWhenLogExceedsSizeCap()
    {
        // One oversized entry pushes the file past the 1 MB cap; the next write
        // must roll it to voltmanager.1.log and start a fresh current file.
        Logger.Info(new string('x', 1_100_000));
        Assert.False(File.Exists(RolledPath));

        Logger.Info("after rotation");

        Assert.True(File.Exists(RolledPath), "previous generation should be rolled aside");
        Assert.True(new FileInfo(RolledPath).Length >= 1_000_000);
        Assert.Contains("after rotation", File.ReadAllText(LogPath));
        Assert.True(new FileInfo(LogPath).Length < 1_000_000, "current file should restart small after rotation");
    }

    [Fact]
    public void WarnOnce_LogsOnlyFirstFailureOfStreak()
    {
        bool faulted = false;
        faulted = Logger.WarnOnce(faulted, "MARKER first failure");
        faulted = Logger.WarnOnce(faulted, "MARKER second failure");
        faulted = Logger.WarnOnce(faulted, "MARKER third failure");

        Assert.True(faulted); // streak flag stays set so callers skip further logs
        int markers = File.ReadLines(LogPath).Count(l => l.Contains("MARKER"));
        Assert.Equal(1, markers);
    }
}
