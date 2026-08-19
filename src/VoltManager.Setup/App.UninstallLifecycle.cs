using System.Windows;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup
{
    public partial class App
    {
        protected override void OnExit(ExitEventArgs e)
        {
            // The temp uninstaller cannot delete itself while its WPF process is
            // alive. Start the bounded helper only at real process exit, after the
            // interactive completion page (if any) has been closed.
            HardenedInstallEngine.ScheduleTemporaryUninstallerSelfDeleteIfNeeded();
            base.OnExit(e);
        }
    }
}
