using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VoltManager.Services;

namespace VoltManager;

public partial class App
{
    private EventWaitHandle? _uninstallShutdownEvent;
    private RegisteredWaitHandle? _uninstallShutdownWait;
    private int _uninstallShutdownScheduled;

    private void OnUninstallLifecycleStartup(object sender, StartupEventArgs e)
    {
        // StartupCore raises Application.Startup before all services are ready.
        // Register at ApplicationIdle so an uninstall request can always reuse
        // ExitApp() after the normal service graph has been initialized.
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(InitializeUninstallShutdownSignal));
    }

    private void InitializeUninstallShutdownSignal()
    {
        if (_uninstallShutdownEvent != null)
            return;

        try
        {
            _uninstallShutdownEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                VoltManagerArtifacts.UninstallShutdownEventName);
            _uninstallShutdownWait = ThreadPool.RegisterWaitForSingleObject(
                _uninstallShutdownEvent,
                (_, _) => QueueUninstallShutdown(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            Logger.Warn("Unable to register uninstall shutdown signal: " + ex.Message);
        }
    }

    private void QueueUninstallShutdown()
    {
        if (Interlocked.Exchange(ref _uninstallShutdownScheduled, 1) != 0)
            return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                try
                {
                    Logger.Info("Graceful shutdown requested by VoltManager uninstaller.");
                    ExitApp();
                }
                catch (Exception ex)
                {
                    Logger.Warn("Graceful uninstall shutdown failed: " + ex.Message);
                    Shutdown();
                }
            }));
    }

    private void OnUninstallLifecycleExit(object sender, ExitEventArgs e)
    {
        try { _uninstallShutdownWait?.Unregister(null); } catch { }
        _uninstallShutdownWait = null;
        try { _uninstallShutdownEvent?.Dispose(); } catch { }
        _uninstallShutdownEvent = null;
    }
}
