using System;
using System.Diagnostics;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SchtasksRunnerTests
    {
        [Fact]
        public void Non_zero_exit_is_reported_with_output()
        {
            SchtasksResult result = SchtasksRunner.Run(
                "/query",
                TimeSpan.FromSeconds(5),
                psi =>
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/d /c \"echo simulated failure 1>&2 & exit /b 7\"";
                    return Process.Start(psi);
                });

            Assert.False(result.Success);
            Assert.False(result.TimedOut);
            Assert.Equal(7, result.ExitCode);
            Assert.Contains("simulated failure", result.Output);
        }

        [Fact]
        public void Timeout_is_reported()
        {
            SchtasksResult result = SchtasksRunner.Run(
                "/query",
                TimeSpan.FromMilliseconds(50),
                psi =>
                {
                    psi.FileName = "powershell.exe";
                    psi.Arguments = "-NoProfile -Command \"Start-Sleep -Seconds 5\"";
                    return Process.Start(psi);
                });

            Assert.False(result.Success);
            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
        }
    }
}
