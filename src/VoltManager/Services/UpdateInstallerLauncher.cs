using System.Diagnostics;

namespace VoltManager.Services;

internal sealed class UpdateInstallerLauncher
{
    private static readonly TimeSpan SetupVisibilityTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SetupAcceptanceTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Action<string> _infoLogger;
    private readonly Action<string> _errorLogger;

    internal UpdateInstallerLauncher(
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Action<string>? infoLogger = null,
        Action<string>? errorLogger = null)
    {
        _startProcess = startProcess ?? Process.Start;
        _infoLogger = infoLogger ?? Logger.Info;
        _errorLogger = errorLogger ?? Logger.Error;
    }

    internal void Launch(VerifiedUpdateDownload download, string arguments)
    {
        ArgumentNullException.ThrowIfNull(download);
        try
        {
            download.VerifyOrThrow();
            using Process? process = _startProcess(new ProcessStartInfo(download.Path, arguments)
            {
                UseShellExecute = true,
            });
            if (process is null)
                throw new InvalidOperationException("Avvio installer aggiornamento non riuscito.");

            download.MarkLaunched();
            _infoLogger("Update installer launched; exiting for update.");
        }
        catch (Exception ex)
        {
            _errorLogger("Update installer launch rejected/failed: " + ex.Message);
            download.Dispose();
            throw;
        }
    }

    internal Task LaunchOrLegacyAsync(
        VerifiedUpdateDownload download,
        string arguments,
        string? encodedPalette,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(download);
        cancellationToken.ThrowIfCancellationRequested();
        if (download.SupportsInteractiveUpdate)
            return LaunchVerifiedAsync(download, arguments, encodedPalette, cancellationToken);

        // Previously published Setup builds only understand the headless /update
        // protocol. Do not wait for a window that cannot be displayed by them.
        Launch(download, arguments);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits for the interactive Setup window to report that it is visible before
    /// allowing the running application to exit. Failure leaves the application alive.
    /// The verified download stays open for the complete handshake.
    /// </summary>
    internal async Task LaunchVerifiedAsync(
        VerifiedUpdateDownload download,
        string arguments,
        string? encodedPalette,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (!await _launchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Un aggiornamento è già in avvio.");

        UpdateSessionChannels? channels = null;
        bool accepted = false;
        try
        {
            download.VerifyOrThrow();
            string sessionId = UpdateSessionProtocol.NewId();
            channels = UpdateSessionProtocol.CreateHost(sessionId);
            string sessionArguments = UpdateSessionProtocol.BuildArguments(
                arguments, sessionId, encodedPalette);

            using Process? process = _startProcess(new ProcessStartInfo(download.Path, sessionArguments)
            {
                UseShellExecute = true,
            });
            if (process is null)
                throw new InvalidOperationException("Avvio installer aggiornamento non riuscito.");

            // Observe an installer that exits early (UAC rejection, failed startup,
            // etc.) without waiting the complete window-readiness timeout.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool> visibility = channels.WaitVisibleAsync(SetupVisibilityTimeout, waitCts.Token);
            Task exited = process.WaitForExitAsync(waitCts.Token);
            Task completed = await Task.WhenAny(visibility, exited).ConfigureAwait(false);
            if (completed == exited)
            {
                await exited.ConfigureAwait(false);
                if (!visibility.IsCompletedSuccessfully || !visibility.Result)
                    throw new InvalidOperationException("Setup si è terminato prima di rendere visibile la finestra.");
            }
            bool visible = await visibility.ConfigureAwait(false);
            waitCts.Cancel();
            if (!visible)
                throw new TimeoutException("La finestra Setup non ha confermato l'avvio entro il tempo previsto.");

            cancellationToken.ThrowIfCancellationRequested();
            if (channels.IsAborted)
                throw new InvalidOperationException("Setup ha annullato la sessione di aggiornamento.");
            if (process.HasExited)
                throw new InvalidOperationException("Setup si è terminato prima della conferma di avvio.");
            // Proceed is only an offer. Setup must explicitly acknowledge the
            // update after its own guards have passed before the host can exit.
            channels.SignalProceed();
            bool acknowledged = await channels.WaitAcceptedAsync(
                SetupAcceptanceTimeout, cancellationToken).ConfigureAwait(false);
            if (!acknowledged || channels.IsAborted)
                throw new InvalidOperationException("Setup non ha accettato la sessione di aggiornamento.");
            // Acceptance commits the handoff. Cancellation can no longer roll it
            // back because Setup is now waiting for the original host to exit.
            accepted = true;
            download.MarkLaunched();
            _infoLogger("Interactive update Setup accepted handoff; shutdown can proceed.");
        }
        catch (Exception ex)
        {
            if (!accepted && channels is not null)
            {
                try { channels.SignalAbort(); }
                catch (Exception signalError)
                {
                    _errorLogger("Interactive update abort signal failed: " + signalError.Message);
                }
            }
            _errorLogger("Interactive update installer launch rejected/failed: " + ex.Message);
            if (!accepted)
                download.Dispose();
            throw;
        }
        finally
        {
            channels?.Dispose();
            _launchGate.Release();
        }
    }
}
