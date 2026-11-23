using System.IO;
using System.Text.RegularExpressions;
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

    [Fact]
    public void SetStartWithWindows_enable_creates_task_with_temp_xml_and_cleans_up_file()
    {
        string? observedArgs = null;
        string? capturedXmlPath = null;
        bool xmlExistedDuringCall = false;

        var service = new StartupService(
            args =>
            {
                observedArgs = args;
                Match match = Regex.Match(args, "/xml \"(?<path>[^\"]+)\"");
                if (match.Success)
                {
                    capturedXmlPath = match.Groups["path"].Value;
                    xmlExistedDuringCall = File.Exists(capturedXmlPath);
                }
                return 0;
            },
            () => @"C:\Program Files\VoltManager\VoltManager.exe");

        bool ok = service.SetStartWithWindows(true);

        Assert.True(ok);
        Assert.NotNull(observedArgs);
        Assert.StartsWith("/create /f /tn \"VoltManagerAutostart\" /xml \"", observedArgs);
        Assert.True(xmlExistedDuringCall);
        Assert.NotNull(capturedXmlPath);
        Assert.False(File.Exists(capturedXmlPath));
    }

    [Fact]
    public void SetStartWithWindows_enable_returns_false_when_create_fails()
    {
        var service = new StartupService(
            _ => 1,
            () => @"C:\Program Files\VoltManager\VoltManager.exe");

        Assert.False(service.SetStartWithWindows(true));
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
