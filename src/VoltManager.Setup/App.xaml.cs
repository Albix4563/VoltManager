using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
                    _ = RunAndExitAsync(
                        () => RunSilent(args),
                        code => Shutdown(code),
                        "Silent install");
                    break;

                case SetupMode.Update:
                    _ = RunAndExitAsync(
                        () => RunUpdate(args.WaitPid),
                        code => Shutdown(code),
                        "Update",
                        ex => string.Format(I18n.T("update_failed"), ex.Message, SetupUpdateLog.FilePath));
                    break;

                case SetupMode.Uninstall:
                    if (!string.IsNullOrWhiteSpace(args.TargetDir))
                    {
                        InstallTargetValidationResult targetValidation =
                            InstallTargetValidator.ValidateUninstallTarget(args.TargetDir);
                        if (!targetValidation.Ok)
                        {
                            string message = string.Format(
                                I18n.T("uninstall_target_invalid"),
                                targetValidation.Reason);
                            SetupUpdateLog.Error(message);
                            if (!args.SilentUninstall)
                            {
                                MessageBox.Show(
                                    message,
                                    "VoltManager",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                            }
                            Shutdown(1);
                            return;
                        }
                    }
                    if (InstallEngine.TryRelaunchFromTempIfNeeded(args, out int handoffExit))
                    {
                        if (args.SilentUninstall)
                            Shutdown(handoffExit);
                        else
                            Shutdown();
                        return;
                    }
                    if (args.SilentUninstall)
                        _ = RunAndExitAsync(
                            () => RunSilentUninstall(args),
                            code => Shutdown(code),
                            "Silent uninstall");
                    else
                        new SetupWindow(args).Show();
                    break;

                default:
                    new SetupWindow(args).Show();
                    break;
            }
        }

        private async Task<int> RunSilent(SetupArgs args)
        {
            var engine = new InstallEngine();
            var opts   = new InstallOptions
            {
                InstallDir = InstallOptions.NormalizeInstallDir(null),
            };
            await engine.InstallAsync(opts, GetVersion());
            return 0;
        }

        private async Task<int> RunUpdate(int pid)
        {
            var engine = new InstallEngine();
            string version = GetVersion();
            SetupUpdateLog.Info($"Update to {version} started (waiting for pid {pid}).");
            await new UpdateInstallCoordinator(engine).UpdateAsync(pid, version);
            SetupUpdateLog.Info($"Update to {version} completed.");
            return 0;
        }

        private async Task<int> RunSilentUninstall(SetupArgs args)
        {
            var engine = new HardenedInstallEngine();
            UninstallResult result = await engine.UninstallAsync(args.TargetDir);
            foreach (string failure in result.Failures)
                SetupUpdateLog.Error("Silent uninstall partial failure: " + failure);
            foreach (string residual in result.Residuals)
                SetupUpdateLog.Error("Silent uninstall residual: " + residual);
            foreach (string warning in engine.LastWarnings)
                SetupUpdateLog.Warn("Silent uninstall warning: " + warning);

            return GetSilentUninstallExitCode(result, engine.LastWarnings);
        }

        internal static int GetSilentUninstallExitCode(
            UninstallResult result,
            IReadOnlyList<string> warnings)
            => result.Success && warnings.Count == 0 ? 0 : 3;

        internal static async Task RunAndExitAsync(
            Func<Task<int>> operation,
            Action<int> shutdown,
            string operationName,
            Func<Exception, string?>? userErrorMessage = null)
        {
            int exitCode;
            try
            {
                exitCode = await operation();
            }
            catch (Exception ex)
            {
                exitCode = 1;
                SetupUpdateLog.Error(operationName + " failed: " + ex);
                if (userErrorMessage != null)
                {
                    try
                    {
                        MessageBox.Show(
                            userErrorMessage(ex),
                            "VoltManager",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    catch (Exception notificationEx)
                    {
                        SetupUpdateLog.Warn("Could not show setup error dialog: " + notificationEx.Message);
                    }
                }
            }

            shutdown(exitCode);
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

            Resources["SurfaceBrush"] = Brush("#16233F");
            Resources["MutedBrush"] = Brush("#CBD5E1");
            Resources["FaintBrush"] = Brush("#94A3B8");
            Resources["AccentBrush"] = Brush(palette.Primary);
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
