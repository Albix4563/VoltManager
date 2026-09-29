using System.Diagnostics;

namespace VoltManager.Services;

internal sealed record ExternalProcessResult(
    bool Started,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    Exception? Error = null);

internal interface IExternalProcessSession : IDisposable
{
    Task<string> ReadStandardOutputAsync();
    Task<string> ReadStandardErrorAsync();
    bool WaitForExit(TimeSpan timeout);
    int ExitCode { get; }
    void KillTree();
}

internal interface IExternalProcessFactory
{
    IExternalProcessSession? Start(ProcessStartInfo startInfo);
}

internal sealed class SystemExternalProcessFactory : IExternalProcessFactory
{
    public IExternalProcessSession? Start(ProcessStartInfo startInfo)
    {
        Process? process = Process.Start(startInfo);
        return process == null ? null : new SystemExternalProcessSession(process);
    }

    private sealed class SystemExternalProcessSession(Process process) : IExternalProcessSession
    {
        public Task<string> ReadStandardOutputAsync()
            => process.StartInfo.RedirectStandardOutput
                ? process.StandardOutput.ReadToEndAsync()
                : Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorAsync()
            => process.StartInfo.RedirectStandardError
                ? process.StandardError.ReadToEndAsync()
                : Task.FromResult(string.Empty);

        public bool WaitForExit(TimeSpan timeout)
            => process.WaitForExit(checked((int)Math.Min(int.MaxValue, Math.Max(0, timeout.TotalMilliseconds))));

        public int ExitCode => process.ExitCode;

        public void KillTree() => process.Kill(entireProcessTree: true);

        public void Dispose() => process.Dispose();
    }
}

internal static class ExternalProcessRunner
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(2);

    public static ExternalProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        IExternalProcessFactory? factory = null)
    {
        try
        {
            using IExternalProcessSession? process = (factory ?? new SystemExternalProcessFactory()).Start(startInfo);
            if (process == null)
                return new ExternalProcessResult(false, false, null, string.Empty, string.Empty);

            Task<string> stdout = process.ReadStandardOutputAsync();
            Task<string> stderr = process.ReadStandardErrorAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.KillTree(); } catch { /* best-effort: timed-out process may already have exited. */ }
                try { process.WaitForExit(KillWaitTimeout); } catch { /* best-effort: timeout cleanup must not mask the primary result. */ }
                return new ExternalProcessResult(true, true, null, CompletedText(stdout), CompletedText(stderr));
            }

            try
            {
                Task allOutput = Task.WhenAll(stdout, stderr);
                if (!allOutput.Wait(DrainTimeout))
                    return new ExternalProcessResult(true, true, process.ExitCode, CompletedText(stdout), CompletedText(stderr));
            }
            catch (Exception ex)
            {
                Exception root = ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
                return new ExternalProcessResult(true, false, process.ExitCode, CompletedText(stdout), CompletedText(stderr), root);
            }

            return new ExternalProcessResult(true, false, process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex)
        {
            return new ExternalProcessResult(false, false, null, string.Empty, string.Empty, ex);
        }
    }

    private static string CompletedText(Task<string> task)
        => task.Status == TaskStatus.RanToCompletion ? task.Result : string.Empty;
}
