using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Services;

namespace VoltManager.Setup.Engine
{
    /// <summary>
    /// Prepares an in-app update before delegating the actual payload replacement
    /// to <see cref="InstallEngine"/>. The WebView2 profile is disposable cache/state;
    /// the surrounding VoltManager AppData directory is deliberately preserved.
    /// </summary>
    public sealed class UpdateInstallCoordinator
    {
        private readonly InstallEngine _engine;

        public UpdateInstallCoordinator(InstallEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public async Task UpdateAsync(int waitPid, string version, CancellationToken ct = default)
        {
            await WaitForMainProcessExitAsync(waitPid, ct);

            if (!WebView2UpdateCacheCleaner.TryClearDefault(out string cacheError))
            {
                throw new InvalidOperationException(
                    "Unable to reset VoltManager WebView2 data before update: " + cacheError);
            }

            // The main process has already been awaited above. InstallEngine still performs
            // its own process-safety checks for the supervisor/hardware service before files
            // are replaced, so pass 0 to avoid waiting on the same PID twice.
            await _engine.UpdateAsync(0, version, ct);
        }

        private static async Task WaitForMainProcessExitAsync(int waitPid, CancellationToken ct)
        {
            if (waitPid <= 0)
                return;

            try
            {
                using (var process = Process.GetProcessById(waitPid))
                {
                    bool exited = await Task.Run(() => process.WaitForExit(30_000), ct);
                    if (!exited)
                        throw new InvalidOperationException("VoltManager did not exit before the update timeout.");
                }
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
        }
    }
}
