using System.Diagnostics;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class BoundedIoTests
{
    [Fact]
    public void Wmi_timeout_executor_returns_empty_without_invoking_projector()
    {
        var executor = new FixedExecutor(timedOut: true, value: null);
        IReadOnlyList<string> values = WmiQuery.Read<string>(
            @"root\cimv2",
            "SELECT Name FROM Win32_Processor",
            _ => throw new InvalidOperationException("projector must not run"),
            TimeSpan.FromMilliseconds(1),
            executor);

        Assert.Empty(values);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public void Wmi_success_executor_preserves_returned_data()
    {
        IReadOnlyList<string> expected = ["alpha", "beta"];
        var executor = new FixedExecutor(timedOut: false, value: expected);
        IReadOnlyList<string> values = WmiQuery.Read<string>(
            @"root\cimv2",
            "SELECT Name FROM Win32_Processor",
            _ => throw new InvalidOperationException("fake executor supplies result"),
            TimeSpan.FromMilliseconds(1),
            executor);

        Assert.Equal(expected, values);
    }

    [Fact]
    public void Wmi_read_or_throw_surfaces_timeout_as_timeout_exception()
    {
        var executor = new FixedExecutor(timedOut: true, value: null);

        Assert.Throws<TimeoutException>(() => WmiQuery.ReadOrThrow<string>(
            @"root\WMI",
            "SELECT * FROM WmiMonitorBrightnessMethods",
            _ => throw new InvalidOperationException("projector must not run"),
            TimeSpan.FromMilliseconds(1),
            executor));
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public void Wmi_read_or_throw_rethrows_original_failure()
    {
        var failure = new System.Management.ManagementException("synthetic");
        var executor = new FailingExecutor(failure);

        var thrown = Assert.Throws<System.Management.ManagementException>(() => WmiQuery.ReadOrThrow<string>(
            @"root\WMI",
            "SELECT * FROM WmiMonitorBrightnessMethods",
            _ => "unused",
            TimeSpan.FromMilliseconds(1),
            executor));
        Assert.Same(failure, thrown);
    }

    [Fact]
    public void Wmi_read_swallows_failure_and_returns_empty()
    {
        IReadOnlyList<string> values = WmiQuery.Read<string>(
            @"root\WMI",
            "SELECT * FROM SyntheticFailureClass",
            _ => "unused",
            TimeSpan.FromMilliseconds(1),
            new FailingExecutor(new InvalidOperationException("synthetic")));

        Assert.Empty(values);
    }

    [Fact]
    public void Wmi_read_or_throw_preserves_returned_data()
    {
        IReadOnlyList<bool> expected = [true];
        IReadOnlyList<bool> values = WmiQuery.ReadOrThrow<bool>(
            @"root\WMI",
            "SELECT * FROM WmiMonitorBrightnessMethods",
            _ => throw new InvalidOperationException("fake executor supplies result"),
            TimeSpan.FromMilliseconds(1),
            new FixedExecutor(timedOut: false, value: expected));

        Assert.Equal(expected, values);
    }

    [Fact]
    public void Bounded_executor_returns_on_slow_operation_and_recovers_slot_after_completion()
    {
        using var release = new ManualResetEventSlim();
        var executor = new ThreadPoolBoundedOperationExecutor(1);
        var stopwatch = Stopwatch.StartNew();
        BoundedOperationResult<int> timedOut = executor.Run(() =>
        {
            release.Wait();
            return 7;
        }, TimeSpan.FromMilliseconds(25));
        stopwatch.Stop();

        Assert.True(timedOut.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(executor.Run(() => 8, TimeSpan.FromMilliseconds(25)).TimedOut);

        release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => executor.Run(() => 9, TimeSpan.FromMilliseconds(25)).Completed,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void External_process_runner_timeout_kills_tree_and_returns_fallback_result()
    {
        var session = new FakeProcessSession { WaitResult = false };
        var factory = new FakeProcessFactory(session);

        ExternalProcessResult result = ExternalProcessRunner.Run(
            new ProcessStartInfo("ignored") { RedirectStandardOutput = true, RedirectStandardError = true },
            TimeSpan.FromMilliseconds(10),
            factory);

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.True(session.Killed);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void External_process_runner_success_preserves_stdout_stderr_and_exit_code()
    {
        var session = new FakeProcessSession
        {
            WaitResult = true,
            ExitCodeValue = 3,
            Stdout = "out",
            Stderr = "err",
        };

        ExternalProcessResult result = ExternalProcessRunner.Run(
            new ProcessStartInfo("ignored") { RedirectStandardOutput = true, RedirectStandardError = true },
            TimeSpan.FromSeconds(1),
            new FakeProcessFactory(session));

        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
        Assert.False(session.Killed);
    }

    [Fact]
    public void External_process_start_failure_is_reported_without_throwing()
    {
        ExternalProcessResult result = ExternalProcessRunner.Run(
            new ProcessStartInfo("ignored"),
            TimeSpan.FromSeconds(1),
            new NullProcessFactory());

        Assert.False(result.Started);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void Hardware_timeout_returns_last_sample_and_does_not_stack_reads()
    {
        using var release = new ManualResetEventSlim();
        var inner = new SlowHardwareAccess(release);
        var executor = new ThreadPoolBoundedOperationExecutor(1);
        using var access = new BoundedHardwareAccess(inner, executor, TimeSpan.FromMilliseconds(25));

        var stopwatch = Stopwatch.StartNew();
        SensorReport first = access.Read();
        SensorReport second = access.Read();
        stopwatch.Stop();

        Assert.Same(SensorReport.Empty, first);
        Assert.Same(SensorReport.Empty, second);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        // The pool may start the timed-out read late; it must still run exactly once.
        Assert.True(SpinWait.SpinUntil(() => inner.ReadCalls >= 1, TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        Assert.Equal(1, inner.ReadCalls);

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => inner.ReadCompleted, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Hardware_success_preserves_sensor_report()
    {
        var expected = new SensorReport { CpuTemp = 51.5 };
        var inner = new ImmediateHardwareAccess(expected);
        using var access = new BoundedHardwareAccess(
            inner,
            new ThreadPoolBoundedOperationExecutor(1),
            TimeSpan.FromSeconds(1));

        SensorReport actual = access.Read();

        Assert.Same(expected, actual);
        Assert.Equal(1, inner.ReadCalls);
    }

    private sealed class FixedExecutor(bool timedOut, object? value) : IBoundedOperationExecutor
    {
        public int Calls { get; private set; }

        public BoundedOperationResult<T> Run<T>(Func<T> operation, TimeSpan timeout)
        {
            Calls++;
            if (timedOut)
                return new BoundedOperationResult<T>(false, true, default, null);
            return new BoundedOperationResult<T>(true, false, (T)value!, null);
        }
    }

    private sealed class FailingExecutor(Exception error) : IBoundedOperationExecutor
    {
        public BoundedOperationResult<T> Run<T>(Func<T> operation, TimeSpan timeout)
            => new(false, false, default, error);
    }

    private sealed class FakeProcessFactory(IExternalProcessSession session) : IExternalProcessFactory
    {
        public IExternalProcessSession? Start(ProcessStartInfo startInfo) => session;
    }

    private sealed class NullProcessFactory : IExternalProcessFactory
    {
        public IExternalProcessSession? Start(ProcessStartInfo startInfo) => null;
    }

    private sealed class FakeProcessSession : IExternalProcessSession
    {
        public bool WaitResult { get; set; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public int ExitCodeValue { get; set; }
        public string Stdout { get; set; } = string.Empty;
        public string Stderr { get; set; } = string.Empty;

        public Task<string> ReadStandardOutputAsync() => Task.FromResult(Stdout);
        public Task<string> ReadStandardErrorAsync() => Task.FromResult(Stderr);
        public bool WaitForExit(TimeSpan timeout) => WaitResult;
        public int ExitCode => ExitCodeValue;
        public void KillTree() => Killed = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class SlowHardwareAccess(ManualResetEventSlim release) : IHardwareAccess
    {
        private int _readCalls;
        private int _readCompleted;
        public bool Available => true;
        public int ReadCalls => Volatile.Read(ref _readCalls);
        public bool ReadCompleted => Volatile.Read(ref _readCompleted) != 0;
        public SensorReport Read(bool force = false) => Read(HardwareSampleRequest.Full, force);
        public SensorReport Read(HardwareSampleRequest request, bool force = false)
        {
            Interlocked.Increment(ref _readCalls);
            release.Wait();
            Volatile.Write(ref _readCompleted, 1);
            return new SensorReport { CpuTemp = 42 };
        }
        public void Invalidate() { }
        public void Dispose() { }
    }

    private sealed class ImmediateHardwareAccess(SensorReport report) : IHardwareAccess
    {
        public bool Available => true;
        public int ReadCalls { get; private set; }
        public SensorReport Read(bool force = false) => Read(HardwareSampleRequest.Full, force);
        public SensorReport Read(HardwareSampleRequest request, bool force = false)
        {
            ReadCalls++;
            return report;
        }
        public void Invalidate() { }
        public void Dispose() { }
    }
}
