using System.Windows;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup
{
    public partial class App
    {
        protected override void OnExit(ExitEventArgs e)
        {
            // A staged uninstaller cannot delete itself while its WPF process is
            // alive. Schedule its reboot-time cleanup only at real process exit.
            HardenedInstallEngine.ScheduleTemporaryUninstallerSelfDeleteIfNeeded();
            base.OnExit(e);
        }
    }
}
