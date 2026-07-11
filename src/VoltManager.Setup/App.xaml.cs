using System.Windows;
using System.Windows.Media;
using System.IO;
using System.Text.RegularExpressions;
using VoltManager.Setup.Engine;
using VoltManager.Setup.Windows;

namespace VoltManager.Setup
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ApplyThemeFromSettings();
            var args = SetupArgs.Parse(e.Args);
            var savedLang = I18n.TryReadSavedLanguage();
            I18n.Initialize(args.Language, savedLang);

            switch (args.Mode)
            {
                case SetupMode.Silent:
                    RunSilent();
                    break;

                case SetupMode.Update:
                    RunUpdate(args.WaitPid);
                    break;

                case SetupMode.Uninstall:
                    if (InstallEngine.TryRelaunchFromTempIfNeeded(args, out int handoffExit))
                    {
                        if (args.SilentUninstall)
                            Shutdown(handoffExit);
                        else
                            Shutdown();
                        return;
                    }
                    if (args.SilentUninstall)
                        RunSilentUninstall(args);
                    else
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
                await engine.UpdateAsync(pid, GetVersion());
            }
            catch { /* silent */ }
            Shutdown();
        }

        private async void RunSilentUninstall(SetupArgs args)
        {
            int exit = 0;
            try
            {
                var result = await new InstallEngine().UninstallAsync(args.TargetDir);
                if (!result.Success) exit = 1;
            }
            catch
            {
                exit = 1;
            }

            Shutdown(exit);
        }

        internal static string GetVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
        }

        private void ApplyThemeFromSettings()
        {
            bool light = ReadSavedTheme() == "light";
            Resources["BgBrush"] = Brush(light ? "#F6F9FC" : "#0A1128");
            Resources["SidebarBrush"] = Brush(light ? "#EAF2F7" : "#060E22");
            Resources["SurfaceBrush"] = Brush(light ? "#FFFFFF" : "#1E2A4A");
            Resources["PillBrush"] = Brush(light ? "#EEF5F8" : "#0D1C38");
            Resources["TextBrush"] = Brush(light ? "#102033" : "#E2E8F0");
            Resources["MutedBrush"] = Brush(light ? "#52677D" : "#94A3B8");
            Resources["BorderBrush2"] = Brush(light ? "#C2D2DE" : "#2D3D5A");
            Resources["AccentBrush"] = Brush(light ? "#00AEBB" : "#00F1FE");
            Resources["AccentTextBrush"] = Brush(light ? "#003F46" : "#0A1128");
            Resources["AccentHoverBrush"] = Brush(light ? "#24C6D2" : "#33FEFF");
            Resources["AccentPressedBrush"] = Brush(light ? "#008A95" : "#00B8C4");
            Resources["DangerBrush"] = Brush(light ? "#B42318" : "#E74C3C");
            Resources["WarningBrush"] = Brush(light ? "#B7791F" : "#F39C12");
        }

        private static string ReadSavedTheme()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VoltManager", "settings.json");
                if (!File.Exists(path)) return "dark";
                var match = Regex.Match(File.ReadAllText(path), "\"theme\"\\s*:\\s*\"(?<theme>[^\"]*)\"", RegexOptions.IgnoreCase);
                if (!match.Success) return "dark";
                return string.Equals(match.Groups["theme"].Value, "light", StringComparison.OrdinalIgnoreCase)
                    ? "light"
                    : "dark";
            }
            catch
            {
                return "dark";
            }
        }

        private static SolidColorBrush Brush(string color)
            => new((Color)ColorConverter.ConvertFromString(color));
    }
}
