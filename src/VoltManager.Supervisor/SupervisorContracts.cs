using System.Diagnostics;

namespace VoltManager.Supervisor;

public static class SupervisorNames
{
    public const string SupervisorMutex = "VoltManager_Supervisor_Mutex";
    public const string WakeEvent = "VoltManager_Supervisor_Wake_Event";
}

public static class SupervisorExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 20;
    public const int CrashLoopBlocked = 21;
    public const int ChildStartFailed = 30;
}

public sealed record SupervisorOptions(string ChildPath, string[] ChildArguments, bool ResetState)
{
    public static bool TryParse(string[] args, out SupervisorOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        int separator = Array.IndexOf(args, "--");
        int childIndex = Array.IndexOf(args, "--child");
        bool resetState = args.Take(separator >= 0 ? separator : args.Length)
            .Any(a => string.Equals(a, "--reset-state", StringComparison.OrdinalIgnoreCase));

        if (childIndex < 0 || childIndex + 1 >= args.Length || (separator >= 0 && childIndex > separator))
        {
            error = "Missing --child <path>.";
            return false;
        }

        string childPath;
        try { childPath = Path.GetFullPath(args[childIndex + 1]); }
        catch
        {
            error = "Invalid child path.";
            return false;
        }

        if (!File.Exists(childPath))
        {
            error = "Child executable not found.";
            return false;
        }

        string[] childArguments = separator >= 0 && separator + 1 < args.Length
            ? args[(separator + 1)..]
            : Array.Empty<string>();

        options = new SupervisorOptions(childPath, childArguments, resetState);
        return true;
    }
}

public sealed record RestartPolicyOptions(
    TimeSpan InitialDelay,
    TimeSpan MaximumDelay,
    double JitterRatio,
    int MaximumRestarts,
    TimeSpan AttemptWindow,
    TimeSpan StablePeriod)
{
    public static RestartPolicyOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMinutes(1),
        0.20,
        5,
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(5));
}

public sealed class SupervisorState
{
    public List<DateTimeOffset> CrashTimesUtc { get; set; } = new();
    public DateTimeOffset? BlockedUntilUtc { get; set; }
    public string? ChildVersion { get; set; }

    public void Reset(string? childVersion)
    {
        CrashTimesUtc.Clear();
        BlockedUntilUtc = null;
        ChildVersion = childVersion;
    }
}

public sealed record RestartDecision(
    bool ShouldRestart,
    int Attempt,
    TimeSpan Delay,
    bool StableCounterReset,
    DateTimeOffset? BlockedUntilUtc);

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IJitterSource
{
    double NextUnit();
}

public interface IWaitStrategy : IDisposable
{
    bool Wait(TimeSpan delay);
}

public interface ISupervisorStateStore
{
    SupervisorState Load();
    void Save(SupervisorState state);
    void Reset();
}

public interface ISupervisorEventSink
{
    void Write(string eventName, object? fields = null);
}

public interface IChildProcess : IDisposable
{
    int Id { get; }
    int WaitForExit();
}

public interface IChildProcessFactory
{
    IChildProcess Start(string childPath, IReadOnlyList<string> childArguments);
    void StopCurrent(TimeSpan gracefulTimeout);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class ProcessChild : IChildProcess
{
    private readonly Process _process;

    public ProcessChild(Process process) => _process = process;

    public int Id => _process.Id;

    public int WaitForExit()
    {
        _process.WaitForExit();
        return _process.ExitCode;
    }

    public void Dispose() => _process.Dispose();
}
