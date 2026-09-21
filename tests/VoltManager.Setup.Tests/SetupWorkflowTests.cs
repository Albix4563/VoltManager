using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SetupWorkflowTests
    {
        [Fact]
        public async Task Runner_stops_at_first_failure_and_records_diagnostic()
        {
            var calls = new List<string>();
            var runner = new SetupWorkflowRunner();
            var steps = new[]
            {
                new SetupWorkflowStep("first", _ => { calls.Add("first"); return Task.CompletedTask; }),
                new SetupWorkflowStep("broken", _ => throw new InvalidOperationException("boom")),
                new SetupWorkflowStep("never", _ => { calls.Add("never"); return Task.CompletedTask; }),
            };

            SetupWorkflowResult result = await runner.RunAsync(steps, CancellationToken.None);

            Assert.False(result.Success);
            Assert.False(result.Cancelled);
            Assert.Equal("broken", result.FailedStep);
            Assert.Contains("boom", result.Summary);
            Assert.Equal(new[] { "first" }, calls);
        }

        [Fact]
        public async Task Runner_records_cancellation_without_executing_later_steps()
        {
            var cts = new CancellationTokenSource();
            var runner = new SetupWorkflowRunner();
            var calls = new List<string>();
            var steps = new[]
            {
                new SetupWorkflowStep("cancel", token =>
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("never", _ => { calls.Add("never"); return Task.CompletedTask; }),
            };

            SetupWorkflowResult result = await runner.RunAsync(steps, cts.Token);

            Assert.True(result.Cancelled);
            Assert.False(result.Success);
            Assert.Equal("cancel", result.FailedStep);
            Assert.Empty(calls);
        }

        [Fact]
        public async Task Install_workflow_reports_processes_still_active_before_destructive_steps()
        {
            var processOperations = new FakeProcessOperations { StopForInstallResult = false };
            var engine = new InstallEngine(processOperations);
            var options = new InstallOptions { InstallDir = @"C:\VoltManagerTests\NeverTouched" };

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.InstallAsync(options, "1.2.3"));

            Assert.Contains("stop-processes", error.Message);
            Assert.NotNull(engine.LastOperationResult);
            Assert.Equal("stop-processes", engine.LastOperationResult!.FailedStep);
            Assert.Equal(1, processOperations.StopForInstallCalls);
        }

        private sealed class FakeProcessOperations : IInstallProcessOperations
        {
            public bool StopForInstallResult { get; set; } = true;
            public int StopForInstallCalls { get; private set; }

            public bool StopForInstall(string installDir)
            {
                StopForInstallCalls++;
                return StopForInstallResult;
            }

            public bool StopInstalled(string installDir) => true;

            public Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
                => Task.FromResult(true);

            public void Start(string fileName, string arguments) { }
        }
    }
}
