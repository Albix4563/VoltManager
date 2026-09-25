using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class UpdateInstallCoordinatorTests
    {
        [Fact]
        public async Task Timeout_is_logged_and_update_continues_so_installer_can_stop_the_app()
        {
            var engine = new FakeEngine();
            var processes = new FakeProcessOperations { WaitResult = false };
            var warnings = new List<string>();
            int cacheCalls = 0;
            var coordinator = new UpdateInstallCoordinator(
                engine, processes, () => { cacheCalls++; return null; }, warnings.Add);

            await coordinator.UpdateAsync(42, "1.2.3");

            Assert.Contains(warnings, warning => warning.Contains("pid 42"));
            Assert.Equal(1, cacheCalls);
            Assert.Equal(1, engine.Calls);
            Assert.Equal(0, engine.LastWaitPid);
        }

        [Fact]
        public async Task Cache_failure_is_logged_and_does_not_block_payload_update()
        {
            var engine = new FakeEngine();
            var processes = new FakeProcessOperations { WaitResult = true };
            var warnings = new List<string>();
            var coordinator = new UpdateInstallCoordinator(engine, processes, () => "cache locked", warnings.Add);

            await coordinator.UpdateAsync(42, "1.2.3");

            Assert.Contains(warnings, warning => warning.Contains("cache locked"));
            Assert.Equal(1, engine.Calls);
        }

        [Fact]
        public async Task Engine_failure_is_propagated()
        {
            var engine = new FakeEngine { Failure = new InvalidOperationException("stop-processes failed") };
            var processes = new FakeProcessOperations { WaitResult = true };
            var coordinator = new UpdateInstallCoordinator(engine, processes, () => null);

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.UpdateAsync(42, "1.2.3"));

            Assert.Contains("stop-processes", error.Message);
        }

        [Fact]
        public async Task Successful_preparation_delegates_update_without_waiting_twice()
        {
            var engine = new FakeEngine();
            var processes = new FakeProcessOperations { WaitResult = true };
            var warnings = new List<string>();
            var coordinator = new UpdateInstallCoordinator(engine, processes, () => null, warnings.Add);

            await coordinator.UpdateAsync(42, "1.2.3");

            Assert.Equal(1, processes.WaitCalls);
            Assert.Equal(1, engine.Calls);
            Assert.Equal(0, engine.LastWaitPid);
            Assert.Equal("1.2.3", engine.LastVersion);
            Assert.Empty(warnings);
        }

        private sealed class FakeEngine : IInstallUpdateEngine
        {
            public int Calls { get; private set; }
            public int LastWaitPid { get; private set; }
            public string LastVersion { get; private set; } = "";
            public Exception? Failure { get; set; }

            public Task UpdateAsync(int waitPid, string version, CancellationToken ct = default)
            {
                Calls++;
                LastWaitPid = waitPid;
                LastVersion = version;
                if (Failure != null)
                    throw Failure;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeProcessOperations : IInstallProcessOperations
        {
            public bool WaitResult { get; set; }
            public int WaitCalls { get; private set; }
            public bool StopForInstall(string installDir) => true;
            public bool StopInstalled(string installDir) => true;

            public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
            {
                WaitCalls++;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(WaitResult);
            }

            public void Start(string fileName, string arguments) { }
        }
    }
}
