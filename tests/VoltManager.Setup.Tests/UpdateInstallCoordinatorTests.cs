using System;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class UpdateInstallCoordinatorTests
    {
        [Fact]
        public async Task Timeout_stops_before_cache_cleanup_and_engine_update()
        {
            var engine = new FakeEngine();
            var processes = new FakeProcessOperations { WaitResult = false };
            int cacheCalls = 0;
            var coordinator = new UpdateInstallCoordinator(engine, processes, () => { cacheCalls++; return null; });

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.UpdateAsync(42, "1.2.3"));

            Assert.Contains("timeout", error.Message.ToLowerInvariant());
            Assert.Equal(0, cacheCalls);
            Assert.Equal(0, engine.Calls);
        }

        [Fact]
        public async Task Cache_failure_stops_before_payload_update()
        {
            var engine = new FakeEngine();
            var processes = new FakeProcessOperations { WaitResult = true };
            var coordinator = new UpdateInstallCoordinator(engine, processes, () => "cache locked");

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.UpdateAsync(42, "1.2.3"));

            Assert.Contains("cache locked", error.Message);
            Assert.Equal(0, engine.Calls);
        }

        [Fact]
        public async Task Successful_preparation_delegates_update_without_waiting_twice()
        {
            var engine = new FakeEngine();
            var processes = new FakeProcessOperations { WaitResult = true };
            var coordinator = new UpdateInstallCoordinator(engine, processes, () => null);

            await coordinator.UpdateAsync(42, "1.2.3");

            Assert.Equal(1, processes.WaitCalls);
            Assert.Equal(1, engine.Calls);
            Assert.Equal(0, engine.LastWaitPid);
            Assert.Equal("1.2.3", engine.LastVersion);
        }

        private sealed class FakeEngine : IInstallUpdateEngine
        {
            public int Calls { get; private set; }
            public int LastWaitPid { get; private set; }
            public string LastVersion { get; private set; } = "";

            public Task UpdateAsync(int waitPid, string version, CancellationToken ct = default)
            {
                Calls++;
                LastWaitPid = waitPid;
                LastVersion = version;
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
