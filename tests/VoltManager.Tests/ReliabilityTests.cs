using VoltManager.Reliability;
using VoltManager.Supervisor;

namespace VoltManager.Tests;

public sealed class ReliabilityTests
{
    [Fact]
    public void UnhandledUiException_RestartsThenStopsAfterCleanExit()
    {
        using var harness = new SupervisorHarness(
            new ChildScenario(AppExitCodes.UnhandledUiException, TimeSpan.FromSeconds(1)),
            new ChildScenario(0, TimeSpan.FromSeconds(10)));

        int result = harness.Run(RestartPolicyOptions.Default);

        Assert.Equal(SupervisorExitCodes.Success, result);
        Assert.Equal(2, harness.Processes.StartCount);
        Assert.Single(harness.Wait.Delays);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(unchecked((int)0xC000013A))]
    public void AnyAbnormalTermination_IsRestarted(int exitCode)
    {
        using var harness = new SupervisorHarness(
            new ChildScenario(exitCode, TimeSpan.FromSeconds(2)),
            new ChildScenario(0, TimeSpan.FromSeconds(2)));

        int result = harness.Run(RestartPolicyOptions.Default);

        Assert.Equal(SupervisorExitCodes.Success, result);
        Assert.Equal(2, harness.Processes.StartCount);
    }

    [Fact]
    public void StartupCrash_UsesInitialBackoff()
    {
        using var harness = new SupervisorHarness(
            new ChildScenario(AppExitCodes.StartupFailure, TimeSpan.FromMilliseconds(100)),
            new ChildScenario(0, TimeSpan.FromSeconds(1)));

        harness.Run(RestartPolicyOptions.Default);

        Assert.Equal(RestartPolicyOptions.Default.InitialDelay, harness.Wait.Delays.Single());
    }

    [Fact]
    public void RepeatedCrashes_StopAfterRestartBudgetIsExhausted()
    {
        using var harness = new SupervisorHarness(Enumerable.Range(0, 6)
            .Select(_ => new ChildScenario(11, TimeSpan.FromSeconds(1)))
            .ToArray());

        int result = harness.Run(RestartPolicyOptions.Default);

        Assert.Equal(SupervisorExitCodes.CrashLoopBlocked, result);
        Assert.Equal(6, harness.Processes.StartCount);
        Assert.Equal(RestartPolicyOptions.Default.MaximumRestarts, harness.Wait.Delays.Count);
        Assert.Contains("restart_budget_exhausted", harness.Events.Names);
    }

    [Fact]
    public void Backoff_IsExponentialAndCapped()
    {
        var options = new RestartPolicyOptions(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(4),
            JitterRatio: 0,
            MaximumRestarts: 5,
            AttemptWindow: TimeSpan.FromMinutes(10),
            StablePeriod: TimeSpan.FromMinutes(5));

        using var harness = new SupervisorHarness(
            new ChildScenario(11, TimeSpan.FromSeconds(1)),
            new ChildScenario(11, TimeSpan.FromSeconds(1)),
            new ChildScenario(11, TimeSpan.FromSeconds(1)),
            new ChildScenario(11, TimeSpan.FromSeconds(1)),
            new ChildScenario(0, TimeSpan.FromSeconds(1)));

        int result = harness.Run(options);

        Assert.Equal(SupervisorExitCodes.Success, result);
        Assert.Equal(
            new[] { 1d, 2d, 4d, 4d },
            harness.Wait.Delays.Select(delay => delay.TotalSeconds).ToArray());
    }

    [Fact]
    public void StableRun_ResetsAttemptCounter()
    {
        using var harness = new SupervisorHarness(
            new ChildScenario(11, TimeSpan.FromSeconds(1)),
            new ChildScenario(11, TimeSpan.FromSeconds(1)),
            new ChildScenario(11, TimeSpan.FromMinutes(6)),
            new ChildScenario(0, TimeSpan.FromSeconds(1)));

        int result = harness.Run(RestartPolicyOptions.Default);

        Assert.Equal(SupervisorExitCodes.Success, result);
        Assert.Equal(
            new[] { 2d, 4d, 2d },
            harness.Wait.Delays.Select(delay => delay.TotalSeconds).ToArray());
        Assert.Contains("restart_counter_reset_after_stable_run", harness.Events.Names);
    }

    [Fact]
    public void SupervisorMutex_PreventsConcurrentSupervisorsAndReleasesCleanly()
    {
        string name = "VoltManager_Test_Supervisor_" + Guid.NewGuid().ToString("N");
        using SingleInstanceGuard? first = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstanceGuard.TryAcquire(name));

        first.Dispose();
        using SingleInstanceGuard? afterRelease = SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public void StateStore_RoundTripsAtomicallyAndQuarantinesCorruption()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "state.json");
            var events = new RecordingEvents();
            var store = new FileSupervisorStateStore(path, events);
            var expected = new SupervisorState
            {
                CrashTimesUtc = new List<DateTimeOffset> { DateTimeOffset.UtcNow },
                BlockedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                ChildVersion = "1.2.3",
            };

            store.Save(expected);
            SupervisorState loaded = store.Load();
            Assert.Equal(expected.ChildVersion, loaded.ChildVersion);
            Assert.Single(loaded.CrashTimesUtc);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp.*"));

            File.WriteAllText(path, "{not-json");
            SupervisorState recovered = store.Load();
            Assert.Empty(recovered.CrashTimesUtc);
            Assert.Single(Directory.GetFiles(directory, "state.json.corrupt.*"));
            Assert.Contains("state_corrupt_quarantined", events.Names);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BoundedCleanup_ReleasesLocksAndDoesNotHangOnBlockedStep()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "lock.bin");
        var releaseBlockedStep = new ManualResetEventSlim(false);
        FileStream locked = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        try
        {
            IReadOnlyList<CleanupStepResult> results = BoundedCleanup.Run(
                new[]
                {
                    new CleanupStep("file lock", locked.Dispose),
                    new CleanupStep("blocked resource", () => releaseBlockedStep.Wait()),
                },
                totalTimeout: TimeSpan.FromMilliseconds(500),
                maximumPerStep: TimeSpan.FromMilliseconds(150));

            releaseBlockedStep.Set();
            Assert.Equal(CleanupOutcome.Completed, results[0].Outcome);
            Assert.Equal(CleanupOutcome.TimedOut, results[1].Outcome);
            using FileStream reopened = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            releaseBlockedStep.Set();
            locked.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CrashDiagnostic_DoesNotPersistExceptionMessagesOrSecrets()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            Exception exception;
            try { throw new InvalidOperationException("token=super-secret-value"); }
            catch (Exception ex) { exception = ex; }

            string? path = CrashDiagnostics.CaptureToDirectory(
                directory,
                "test_crash",
                exception,
                AppExitCodes.UnhandledUiException);

            Assert.NotNull(path);
            string json = File.ReadAllText(path!);
            Assert.Contains(typeof(InvalidOperationException).FullName!, json);
            Assert.DoesNotContain("super-secret-value", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ChildScenario(int ExitCode, TimeSpan Uptime);

    private sealed class SupervisorHarness : IDisposable
    {
        private readonly string _directory;
        private readonly string _childPath;

        public SupervisorHarness(params ChildScenario[] scenarios)
        {
            _directory = CreateTemporaryDirectory();
            _childPath = Path.Combine(_directory, "VoltManager.exe");
            File.WriteAllText(_childPath, "test child");
            Clock = new FakeClock(DateTimeOffset.Parse("2026-07-18T10:00:00Z"));
            Wait = new FakeWaitStrategy(Clock);
            Events = new RecordingEvents();
            Processes = new FakeProcessFactory(Clock, scenarios);
        }

        public FakeClock Clock { get; }
        public FakeWaitStrategy Wait { get; }
        public RecordingEvents Events { get; }
        public FakeProcessFactory Processes { get; }

        public int Run(RestartPolicyOptions options)
        {
            var engine = new SupervisorEngine(
                Clock,
                new FixedJitter(0.5),
                Wait,
                new InMemoryStateStore(),
                Events,
                Processes,
                new RestartPolicy(options));

            return engine.Run(new SupervisorOptions(_childPath, Array.Empty<string>(), ResetState: false));
        }

        public void Dispose()
        {
            Wait.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; private set; }
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FixedJitter : IJitterSource
    {
        private readonly double _value;
        public FixedJitter(double value) => _value = value;
        public double NextUnit() => _value;
    }

    private sealed class FakeWaitStrategy : IWaitStrategy
    {
        private readonly FakeClock _clock;
        public FakeWaitStrategy(FakeClock clock) => _clock = clock;
        public List<TimeSpan> Delays { get; } = new();

        public bool Wait(TimeSpan delay)
        {
            Delays.Add(delay);
            _clock.Advance(delay);
            return false;
        }

        public void Dispose() { }
    }

    private sealed class InMemoryStateStore : ISupervisorStateStore
    {
        private SupervisorState _state = new();
        public SupervisorState Load() => _state;
        public void Save(SupervisorState state) => _state = state;
        public void Reset() => _state = new SupervisorState();
    }

    private sealed class RecordingEvents : ISupervisorEventSink
    {
        public List<string> Names { get; } = new();
        public void Write(string eventName, object? fields = null) => Names.Add(eventName);
    }

    private sealed class FakeProcessFactory : IChildProcessFactory
    {
        private readonly FakeClock _clock;
        private readonly Queue<ChildScenario> _scenarios;

        public FakeProcessFactory(FakeClock clock, IEnumerable<ChildScenario> scenarios)
        {
            _clock = clock;
            _scenarios = new Queue<ChildScenario>(scenarios);
        }

        public int StartCount { get; private set; }

        public IChildProcess Start(string childPath, IReadOnlyList<string> childArguments)
        {
            StartCount++;
            if (_scenarios.Count == 0)
                throw new InvalidOperationException("No fake process scenario remains.");
            return new FakeChild(StartCount, _clock, _scenarios.Dequeue());
        }

        public void StopCurrent(TimeSpan gracefulTimeout) { }
    }

    private sealed class FakeChild : IChildProcess
    {
        private readonly FakeClock _clock;
        private readonly ChildScenario _scenario;

        public FakeChild(int id, FakeClock clock, ChildScenario scenario)
        {
            Id = id;
            _clock = clock;
            _scenario = scenario;
        }

        public int Id { get; }

        public int WaitForExit()
        {
            _clock.Advance(_scenario.Uptime);
            return _scenario.ExitCode;
        }

        public void Dispose() { }
    }
}
