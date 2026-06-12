using System.Windows;
using VoltManager.Setup.Engine;
using VoltManager.Setup.Windows;

namespace VoltManager.Setup
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = SetupArgs.Parse(e.Args);

            switch (args.Mode)
            {
                case SetupMode.Silent:
                    RunSilent();
                    break;

                case SetupMode.Update:
                    RunUpdate(args.WaitPid);
                    break;

                case SetupMode.Uninstall:
                    new SetupWindow(args).Show();
                    break;

                default:
                    new SetupWindow(args).Show();
                    break;
            }
        }

        private async void RunSilent()
        {
            var engine = new InstallEngine();
            var opts   = new InstallOptions();
            try
            {
                await engine.InstallAsync(opts, GetVersion());
            }
            catch { /* silent — swallow */ }
            Shutdown();
        }

        private async void RunUpdate(int pid)
        {
            var engine = new InstallEngine();
            try
            {
                await engine.UpdateAsync(pid);
            }
            catch { /* silent */ }
            Shutdown();
        }

        internal static string GetVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
        }
    }
}
