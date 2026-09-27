using VoltManager.Services;

namespace VoltManager.Reliability;

public sealed class SingleFlightGate
{
    private readonly string _operationName;
    private readonly TimeSpan _warningThreshold;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _warn;
    private readonly Action<string> _info;
    private int _entered;
    private int _slowWarningLogged;
    private long _startedTicks;
    private long _skippedTicks;

    public SingleFlightGate(string operationName, TimeSpan? warningThreshold = null)
        : this(operationName, warningThreshold ?? TimeSpan.FromSeconds(30),
            () => DateTime.UtcNow, Logger.Warn, Logger.Info)
    {
    }

    internal SingleFlightGate(
        string operationName,
        TimeSpan warningThreshold,
        Func<DateTime> utcNow,
        Action<string> warn,
        Action<string> info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        if (warningThreshold < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(warningThreshold));
        _operationName = operationName;
        _warningThreshold = warningThreshold;
        _utcNow = utcNow;
        _warn = warn;
        _info = info;
    }

    public long SkippedTickCount => Interlocked.Read(ref _skippedTicks);

    public bool TryEnter()
    {
        DateTime now = _utcNow();
        if (Interlocked.CompareExchange(ref _entered, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _startedTicks, now.Ticks);
            Interlocked.Exchange(ref _slowWarningLogged, 0);
            return true;
        }

        Interlocked.Increment(ref _skippedTicks);
        long startedTicks = Volatile.Read(ref _startedTicks);
        if (startedTicks != 0 && now.Ticks - startedTicks >= _warningThreshold.Ticks &&
            Interlocked.CompareExchange(ref _slowWarningLogged, 1, 0) == 0)
        {
            _warn($"{_operationName} is still in flight after {(now - new DateTime(startedTicks, DateTimeKind.Utc)).TotalSeconds:F0}s; skipping overlapping ticks.");
        }

        return false;
    }

    public void Exit()
    {
        if (Volatile.Read(ref _entered) == 0)
            return;

        long startedTicks = Interlocked.Exchange(ref _startedTicks, 0);
        bool wasSlow = Interlocked.Exchange(ref _slowWarningLogged, 0) != 0;
        Volatile.Write(ref _entered, 0);

        if (wasSlow)
        {
            DateTime now = _utcNow();
            double seconds = startedTicks == 0 ? 0 : (now - new DateTime(startedTicks, DateTimeKind.Utc)).TotalSeconds;
            _info($"{_operationName} recovered after {Math.Max(0, seconds):F0}s.");
        }
    }
}

internal static class SingleFlightCallback
{
    public static void Run(SingleFlightGate gate, Action callback)
    {
        if (!gate.TryEnter()) return;
        try { callback(); }
        finally { gate.Exit(); }
    }
}
