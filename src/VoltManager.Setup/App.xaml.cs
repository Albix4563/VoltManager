using System.Windows;
using System.Windows.Media;
using System.IO;
using System.Text.RegularExpressions;
using VoltManager.Models;
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
                    RunSilent(args);
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

        private async void RunSilent(SetupArgs args)
        {
            var engine = new InstallEngine();
            var opts   = new InstallOptions
            {
                InstallDir = InstallOptions.NormalizeInstallDir(
                    string.IsNullOrWhiteSpace(args.TargetDir) ? null : args.TargetDir),
            };
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
            int exit = 0;
            try
            {
                await new UpdateInstallCoordinator(engine).UpdateAsync(pid, GetVersion());
            }
            catch
            {
                exit = 1;
            }
            Shutdown(exit);
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
            var palette = AppThemeColorPalette.Get(ReadSavedThemeColor());
            var primary = ParseColor(palette.Primary);

            Resources["C.Accent"] = primary;
            Resources["C.Accent.Dim"] = ParseColor(palette.Secondary);
            Resources["C.Accent.Glow"] = ParseColor(palette.Secondary);
            Resources["C.Accent.Deep"] = ParseColor(palette.Hover);
            Resources["C.Accent.Alpha40"] = WithAlpha(primary, 0x40);
            Resources["C.Accent.Alpha20"] = WithAlpha(primary, 0x20);
            Resources["C.Accent.Transparent"] = WithAlpha(primary, 0x00);

            Resources["BgBrush"] = Brush("#0A1128");
            Resources["SidebarBrush"] = Brush("#0A1128");
            Resources["SurfaceBrush"] = Brush("#16233F");
            Resources["PillBrush"] = Brush("#16233F");
            Resources["TextBrush"] = Brush("#F8FAFC");
            Resources["TextStrongBrush"] = Brush("#F8FAFC");
            Resources["MutedBrush"] = Brush("#CBD5E1");
            Resources["FaintBrush"] = Brush("#94A3B8");
            Resources["BorderBrush2"] = Brush("#334155");
            Resources["AccentBrush"] = Brush(palette.Primary);
            Resources["AccentTextBrush"] = Brush("#0F172A");
            Resources["AccentHoverBrush"] = Brush(palette.Secondary);
            Resources["AccentPressedBrush"] = Brush(palette.Hover);
            Resources["DangerBrush"] = Brush("#FF5B4A");
            Resources["WarningBrush"] = Brush("#F5B042");
        }

        private static AppThemeColor ReadSavedThemeColor()
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VoltManager", "settings.json");
                if (!File.Exists(path)) return AppThemeColor.Blue;

                var match = Regex.Match(
                    File.ReadAllText(path),
                    "\"themeColor\"\\s*:\\s*\"(?<themeColor>[^\"]*)\"",
                    RegexOptions.IgnoreCase);
                if (!match.Success) return AppThemeColor.Blue;

                return AppThemeColorPalette.TryParseKey(match.Groups["themeColor"].Value, out var parsed)
                    ? parsed
                    : AppThemeColor.Blue;
            }
            catch
            {
                return AppThemeColor.Blue;
            }
        }

        private static Color ParseColor(string value)
            => (Color)ColorConverter.ConvertFromString(value);

        private static Color WithAlpha(Color color, byte alpha)
            => Color.FromArgb(alpha, color.R, color.G, color.B);

        private static SolidColorBrush Brush(string color)
            => new(ParseColor(color));

    }
}
