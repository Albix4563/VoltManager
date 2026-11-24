using System;
using System.Diagnostics;
using System.IO;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SchtasksRunnerTests
    {
        [Fact]
        public void Run_uses_trusted_system32_schtasks_path_and_preserves_arguments()
        {
            ProcessStartInfo? observed = null;
            SchtasksResult result = SchtasksRunner.Run(
                "/query /tn \"VoltManagerAutostart\"",
                TimeSpan.FromSeconds(5),
                psi =>
                {
                    observed = psi;
                    return null;
                });

            Assert.NotNull(observed);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                observed!.FileName);
            Assert.Equal("/query /tn \"VoltManagerAutostart\"", observed.Arguments);
            Assert.False(observed.UseShellExecute);
            Assert.False(result.Success);
            Assert.Equal(-1, result.ExitCode);
        }

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
