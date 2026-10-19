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
        private static readonly TimeSpan MainProcessExitTimeout = TimeSpan.FromSeconds(30);

        private readonly IInstallUpdateEngine _engine;
        private readonly IInstallProcessOperations _processOperations;
        private readonly Func<string?> _clearWebView2Cache;
        private readonly Action<string> _warn;

        public UpdateInstallCoordinator(InstallEngine engine)
            : this(engine, new SystemInstallProcessOperations(), ClearWebView2Cache, SetupUpdateLog.Warn)
        {
        }

        internal UpdateInstallCoordinator(
            IInstallUpdateEngine engine,
            IInstallProcessOperations processOperations,
            Func<string?> clearWebView2Cache,
            Action<string>? warn = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _processOperations = processOperations ?? throw new ArgumentNullException(nameof(processOperations));
            _clearWebView2Cache = clearWebView2Cache ?? throw new ArgumentNullException(nameof(clearWebView2Cache));
            _warn = warn ?? (_ => { });
        }

        public async Task UpdateAsync(
            int waitPid, string version, CancellationToken ct = default,
            bool requireProcessExit = false,
            Action<string, bool>? onStage = null,
            string? updateSessionId = null,
            Func<bool>? isHandoffAborted = null)
        {
            void ThrowIfHandoffAborted()
            {
                if (requireProcessExit && isHandoffAborted?.Invoke() == true)
                    throw new OperationCanceledException("The interactive update handoff was aborted.");
            }

            // Cache eviction is itself a filesystem side effect. Do not perform it
            // until the installed target passes the same strict ownership gate
            // required by the engine before stopping processes or replacing files.
            ct.ThrowIfCancellationRequested();
            ThrowIfHandoffAborted();
            _engine.ValidateUpdateTargetBeforeSideEffects();
            ct.ThrowIfCancellationRequested();
            onStage?.Invoke("waiting-for-exit", false);
            if (waitPid > 0)
            {
                bool exited = await _processOperations.WaitForExitAsync(
                    waitPid, MainProcessExitTimeout, ct).ConfigureAwait(false);
                ThrowIfHandoffAborted();
                if (!exited)
                {
                    if (requireProcessExit)
                        throw new InvalidOperationException($"VoltManager (pid {waitPid}) did not exit within {MainProcessExitTimeout.TotalSeconds:0}s; the update was not started.");
                    // Legacy /update --pid has historically continued after timeout.
                    _warn($"VoltManager (pid {waitPid}) did not exit within {MainProcessExitTimeout.TotalSeconds:0}s; it will be stopped by the installer.");
                }
            }
            ThrowIfHandoffAborted();
            onStage?.Invoke("waiting-for-exit", true);

            // The profile is only a cache: a locked file (for instance a WebView2 helper
            // still shutting down) must not block the update itself.
            ThrowIfHandoffAborted();
            string? cacheError = _clearWebView2Cache();
            if (!string.IsNullOrWhiteSpace(cacheError))
                _warn("Unable to reset VoltManager WebView2 data before update: " + cacheError);

            // The main process has already been awaited above. InstallEngine still performs
            // its own process-safety checks for the supervisor/hardware service before files
            // are replaced, so pass 0 to avoid waiting on the same PID twice.
            ThrowIfHandoffAborted();
            if (_engine is InstallEngine installEngine)
            {
                if (onStage != null)
                    installEngine.UpdateStepTransition += onStage;
                try
                {
                    await _engine.UpdateAsync(0, version, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (onStage != null)
                        installEngine.UpdateStepTransition -= onStage;
                }
            }
            else
            {
                await _engine.UpdateAsync(0, version, ct).ConfigureAwait(false);
            }
        }

        private static string? ClearWebView2Cache()
            => WebView2UpdateCacheCleaner.TryClearDefault(out string cacheError) ? null : cacheError;

    }
}
