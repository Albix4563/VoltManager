namespace VoltManager.Services;

internal sealed class PeriodicScanRunner
{
    private readonly TimeSpan _interval;
    private readonly Action _scan;
    private readonly string _failureLogMessage;
    private int _scanRunning;
    private bool _scanFaulted; // throttles scan-failure logging to once per streak

    public PeriodicScanRunner(TimeSpan interval, Action scan, string failureLogMessage)
    {
        _interval = interval;
        _scan = scan;
        _failureLogMessage = failureLogMessage;
    }

    public void Start(object gate, ref Timer? timer, TimeSpan delay)
    {
        lock (gate)
            timer ??= new Timer(_ => RunOnce(), null, delay, _interval);
    }

    public static void Stop(object gate, ref Timer? timer)
    {
        Timer? active;
        lock (gate)
        {
            active = timer;
            timer = null;
        }
        active?.Dispose();
    }

    public void RunOnce()
    {
        if (Interlocked.Exchange(ref _scanRunning, 1) != 0) return;
        try
        {
            _scan();
            _scanFaulted = false;
        }
        catch (Exception ex)
        {
            // Background scans must never crash the automation loop;
            // log the first failure of a streak so a real bug isn't hidden.
            _scanFaulted = Logger.WarnOnce(_scanFaulted, _failureLogMessage, ex);
        }
        finally
        {
            Volatile.Write(ref _scanRunning, 0);
        }
    }
}
