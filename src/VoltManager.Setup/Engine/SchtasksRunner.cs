using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace VoltManager.Setup.Engine
{
    internal sealed class SchtasksResult
    {
        internal SchtasksResult(int exitCode, bool timedOut, string output)
        {
            ExitCode = exitCode;
            TimedOut = timedOut;
            Output = output ?? "";
        }

        public int ExitCode { get; }
        public bool TimedOut { get; }
        public string Output { get; }
        public bool Success => !TimedOut && ExitCode == 0;
    }

    internal static class SchtasksRunner
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        internal static SchtasksResult Run(
            string arguments,
            TimeSpan? timeout = null,
            Func<ProcessStartInfo, Process?>? startProcess = null)
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using (Process? process = (startProcess ?? Process.Start)(psi))
                {
                    if (process == null)
                        return new SchtasksResult(-1, false, "Unable to start schtasks.exe.");

                    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = process.StandardError.ReadToEndAsync();
                    int timeoutMs = checked((int)Math.Min(int.MaxValue, Math.Max(1, (timeout ?? DefaultTimeout).TotalMilliseconds)));
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(1000); } catch { }
                        return new SchtasksResult(-1, true, ReadOutput(stdout, stderr));
                    }

                    return new SchtasksResult(process.ExitCode, false, ReadOutput(stdout, stderr));
                }
            }
            catch (Exception ex)
            {
                return new SchtasksResult(-1, false, ex.Message);
            }
        }

        internal static bool IsTaskNotFound(SchtasksResult result)
        {
            if (result.Success || result.TimedOut) return false;
            string output = result.Output ?? "";
            string[] phrases =
            {
                "cannot find the file specified",
                "cannot find the task",
                "does not exist",
                "impossibile trovare il file specificato",
                "impossibile trovare l'attività",
                "non esiste",
            };
            foreach (string phrase in phrases)
                if (output.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static string ReadOutput(Task<string> stdout, Task<string> stderr)
        {
            try
            {
                Task.WaitAll(new Task[] { stdout, stderr }, 1000);
                string outText = stdout.Status == TaskStatus.RanToCompletion ? stdout.Result : "";
                string errText = stderr.Status == TaskStatus.RanToCompletion ? stderr.Result : "";
                return (outText + Environment.NewLine + errText).Trim();
            }
            catch
            {
                return "";
            }
        }
    }
}
