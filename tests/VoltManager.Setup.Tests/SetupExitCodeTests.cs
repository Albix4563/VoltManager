using System;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SetupExitCodeTests
    {
        [Fact]
        public async Task RunAndExitAsync_maps_exception_to_nonzero_exit_code()
        {
            int? exitCode = null;

            await App.RunAndExitAsync(
                () => Task.FromException<int>(new InvalidOperationException("boom")),
                code => exitCode = code,
                "test");

            Assert.Equal(1, exitCode);
        }

        [Fact]
        public void Silent_uninstall_partial_failure_maps_to_distinct_nonzero_exit_code()
        {
            var result = new UninstallResult();
            result.Add("registry cleanup failed");

            Assert.Equal(3, App.GetSilentUninstallExitCode(result, Array.Empty<string>()));
            Assert.Equal(3, App.GetSilentUninstallExitCode(new UninstallResult(), new[] { "warning" }));
            Assert.Equal(0, App.GetSilentUninstallExitCode(new UninstallResult(), Array.Empty<string>()));
        }
    }
}
