using System;
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
        private readonly IInstallUpdateEngine _engine;
        private readonly IInstallProcessOperations _processOperations;
        private readonly Func<string?> _clearWebView2Cache;

        public UpdateInstallCoordinator(InstallEngine engine)
            : this(engine, new SystemInstallProcessOperations(), ClearWebView2Cache)
        {
        }

        internal UpdateInstallCoordinator(
            IInstallUpdateEngine engine,
            IInstallProcessOperations processOperations,
            Func<string?> clearWebView2Cache)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _processOperations = processOperations ?? throw new ArgumentNullException(nameof(processOperations));
            _clearWebView2Cache = clearWebView2Cache ?? throw new ArgumentNullException(nameof(clearWebView2Cache));
        }

        public async Task UpdateAsync(int waitPid, string version, CancellationToken ct = default)
        {
            if (waitPid > 0)
            {
                bool exited = await _processOperations.WaitForExitAsync(
                    waitPid, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (!exited)
                    throw new InvalidOperationException("VoltManager did not exit before the update timeout.");
            }

            string? cacheError = _clearWebView2Cache();
            if (!string.IsNullOrWhiteSpace(cacheError))
            {
                throw new InvalidOperationException(
                    "Unable to reset VoltManager WebView2 data before update: " + cacheError);
            }

            // The main process has already been awaited above. InstallEngine still performs
            // its own process-safety checks for the supervisor/hardware service before files
            // are replaced, so pass 0 to avoid waiting on the same PID twice.
            await _engine.UpdateAsync(0, version, ct);
        }

        private static string? ClearWebView2Cache()
            => WebView2UpdateCacheCleaner.TryClearDefault(out string cacheError) ? null : cacheError;

    }
}
