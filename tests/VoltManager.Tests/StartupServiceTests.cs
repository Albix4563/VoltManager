using System.IO;
using System.Text;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class StartupServiceTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void IsEnabled_returns_true_only_when_query_exits_zero(int exitCode, bool expected)
    {
        string? observedArgs = null;
        var service = new StartupService(args =>
        {
            observedArgs = args;
            return exitCode;
        });

        bool actual = service.IsEnabled();

        Assert.Equal(expected, actual);
        Assert.Equal("/query /tn \"VoltManagerAutostart\"", observedArgs);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void SetStartWithWindows_enable_passes_utf16_xml_and_removes_its_temp_directory(int exitCode, bool expected)
    {
        const string prefix = "/create /f /tn \"VoltManagerAutostart\" /xml \"";
        const string exe = @"C:\Program Files\VoltManager\VoltManager.exe";
        string? xmlPath = null;
        var service = new StartupService(args =>
        {
            Assert.StartsWith(prefix, args);
            Assert.EndsWith("\"", args);
            xmlPath = args[prefix.Length..^1];
            Assert.NotEqual(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(xmlPath));
            byte[] xml = File.ReadAllBytes(xmlPath);
            Assert.True(xml.AsSpan().StartsWith(Encoding.Unicode.Preamble));
            Assert.Contains($"<Command>{exe}</Command>", Encoding.Unicode.GetString(xml));
            return exitCode;
        }, () => exe);

        Assert.Equal(expected, service.SetStartWithWindows(true));
        Assert.NotNull(xmlPath);
        Assert.False(File.Exists(xmlPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(xmlPath)));
    }

    [Fact]
    public void SetStartWithWindows_enable_returns_false_when_process_path_is_empty()
    {
        int calls = 0;
        var service = new StartupService(
            _ =>
            {
                calls++;
                return 0;
            },
            () => "");

        Assert.False(service.SetStartWithWindows(true));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SetStartWithWindows_disable_returns_true_when_delete_exits_zero()
    {
        var calls = new List<string>();
        var service = new StartupService(args =>
        {
            calls.Add(args);
            return 0;
        });

        Assert.True(service.SetStartWithWindows(false));
        Assert.Equal(["/delete /f /tn \"VoltManagerAutostart\""], calls);
    }

    [Fact]
    public void SetStartWithWindows_disable_returns_true_when_task_is_already_absent()
    {
        var calls = new List<string>();
        var service = new StartupService(args =>
        {
            calls.Add(args);
            return args.StartsWith("/delete", StringComparison.Ordinal) ? 1 : 1;
        });

        Assert.True(service.SetStartWithWindows(false));
        Assert.Equal(
        [
            "/delete /f /tn \"VoltManagerAutostart\"",
            "/query /tn \"VoltManagerAutostart\"",
        ], calls);
    }

    [Fact]
    public void SetStartWithWindows_disable_returns_false_when_task_remains_active_after_delete_failure()
    {
        var calls = new List<string>();
        var service = new StartupService(args =>
        {
            calls.Add(args);
            return args.StartsWith("/delete", StringComparison.Ordinal) ? 1 : 0;
        });

        Assert.False(service.SetStartWithWindows(false));
        Assert.Equal(
        [
            "/delete /f /tn \"VoltManagerAutostart\"",
            "/query /tn \"VoltManagerAutostart\"",
        ], calls);
    }

    [Fact]
    public void SetStartWithWindows_disable_returns_false_when_schtasks_cannot_run_or_times_out()
    {
        var deleteTimedOut = new StartupService(_ => -1);
        Assert.False(deleteTimedOut.SetStartWithWindows(false));

        var queryTimedOut = new StartupService(args =>
            args.StartsWith("/delete", StringComparison.Ordinal) ? 1 : -1);
        Assert.False(queryTimedOut.SetStartWithWindows(false));
    }
}
