using System.Diagnostics;

namespace VoltManager.Services;

internal sealed class UpdateInstallerLauncher
{
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
}
