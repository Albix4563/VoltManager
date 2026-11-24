using System.Diagnostics;
using System.IO;
using VoltManager.Services;

namespace VoltManager.Tests;

[Collection("RuntimeServiceValidationEnvironment")]
public sealed class DiagnosticsReportServiceTests
{
    [Fact]
    public void TryOpenLogFolder_uses_trusted_windows_explorer_path_and_preserves_arguments()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "VoltManager.DiagTests", Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable(ValidationEnvironment.RootVariable);
        try
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, tempRoot);
            ProcessStartInfo? observed = null;

            bool ok = DiagnosticsReportService.TryOpenLogFolder(out string? path, out string? error, psi =>
            {
                observed = psi;
                return null;
            });

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(Path.Combine(Path.GetFullPath(tempRoot), "VoltManager", "logs"), path);
            Assert.True(Directory.Exists(path));
            Assert.NotNull(observed);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                observed!.FileName);
            Assert.Equal("\"" + path + "\"", observed.Arguments);
            Assert.True(observed.UseShellExecute);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, previousRoot);
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryOpenLogFolder_returns_false_with_error_when_launch_throws()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "VoltManager.DiagTests", Guid.NewGuid().ToString("N"));
        string? previousRoot = Environment.GetEnvironmentVariable(ValidationEnvironment.RootVariable);
        try
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, tempRoot);

            bool ok = DiagnosticsReportService.TryOpenLogFolder(
                out _,
                out string? error,
                _ => throw new InvalidOperationException("simulated launch failure"));

            Assert.False(ok);
            Assert.Equal("simulated launch failure", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, previousRoot);
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
