using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using VoltManager.Models;
using VoltManager.Services;
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
            ApplyThemeFromSettings();
            if (args.Mode == SetupMode.Update)
                ApplyUpdatePalette(args.UpdatePalette);
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
                    var window = new UpdateWindow();
                    window.SetVersion(GetVersion());
                    MainWindow = window;
                    if (args.HasUpdateSessionFlag)
                        _ = RunInteractiveUpdateAsync(args, window);
                    else
                        _ = RunLegacyUpdateWindowAsync(args, window);
                    window.Show();
                    break;

                case SetupMode.Uninstall:
                    RedirectUninstallLogToRunDirectory();
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
            engine.RelaunchUpdatedApp();
            SetupUpdateLog.Info($"Update to {version} completed.");
            return 0;
        }

        private async Task RunLegacyUpdateWindowAsync(SetupArgs args, UpdateWindow window)
        {
            var engine = new InstallEngine();
            try
            {
                var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.FirstRendered += (_, __) => rendered.TrySetResult(true);
                window.SetUpdateStarted();
                await rendered.Task;
                string version = GetVersion();
                SetupUpdateLog.Info($"Legacy update to {version} started; app pid {args.WaitPid}.");
                await new UpdateInstallCoordinator(engine).UpdateAsync(args.WaitPid, version,
                    onStage: (name, complete) => ReportUpdateStage(window, name, complete));
                window.ReportStage(3);
                engine.RelaunchUpdatedApp();
                SetupUpdateLog.Info($"Legacy update to {version} launched.");
                window.ShowLegacyCompleted();
                await Task.Delay(1200);
                window.Close();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Error("Legacy update failed: " + ex);
                window.ShowFailure(ex, engine.UpdatePayloadInstalled
                    ? new Func<Task>(() => { engine.RelaunchUpdatedApp(); return Task.CompletedTask; }) : null);
            }
        }

        private static void ReportUpdateStage(UpdateWindow window, string name, bool complete)
        {
            int stage = name == "extract-payload" ? 1
                : name == "register-update" || name == "complete-installation" ? 2 : 0;
            if (complete && name == "extract-payload") stage = 2;
            if (complete && name == "complete-installation") stage = 3;
            window.ReportStage(stage);
        }

        private async Task RunInteractiveUpdateAsync(SetupArgs args, UpdateWindow window)
        {
            UpdateSessionChannels? channels = null;
            bool installed = false;
            bool closing = false;
            bool userAborted = false;
            bool retainChannelsForRetry = false;
            bool channelsDisposed = false;
            var engine = new InstallEngine();
            try
            {
                if (!UpdateSessionProtocol.TryValidateId(args.UpdateSessionToken, out string sessionId))
                    throw new ArgumentException("Invalid update session identifier.");
                if (args.HasUpdatePaletteFlag &&
                    !UpdateSessionProtocol.TryDecodePalette(args.UpdatePalette, out _))
                    throw new ArgumentException("Invalid update palette.");

                channels = UpdateSessionProtocol.OpenSetup(sessionId);
                UpdateSessionChannels activeChannels = channels;
                window.Closed += (_, __) => {
                    closing = true;
                    // Closing before the handoff must only wake the waiter. Disposing
                    // its wait handles while WaitProceedAsync is pending races WaitAny.
                    if (!installed)
                        activeChannels.SignalAbort();
                    else if (retainChannelsForRetry && !channelsDisposed)
                    {
                        activeChannels.Dispose();
                        channelsDisposed = true;
                    }
                };
                var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.FirstRendered += (_, __) => {
                    channels.SignalVisible();
                    rendered.TrySetResult(true);
                };
                window.UpdateAborted += (_, __) => { userAborted = true; channels.SignalAbort(); };
                window.SetWaitingForProceed();
                await rendered.Task;
                if (!await channels.WaitProceedAsync(TimeSpan.FromSeconds(90)))
                    throw new TimeoutException("The original application did not authorize proceeding with the update.");
                if (userAborted || channels.IsAborted)
                    throw new OperationCanceledException("Update canceled before installation.");
                window.SetUpdateStarted();
                // The host must not exit until the setup has committed to update.
                channels.SignalAccepted();

                string version = GetVersion();
                SetupUpdateLog.Info($"Interactive update to {version} began; app pid {args.WaitPid}.");
                await new UpdateInstallCoordinator(engine).UpdateAsync(args.WaitPid, version,
                    CancellationToken.None, requireProcessExit: true,
                    onStage: (name, complete) => ReportUpdateStage(window, name, complete),
                    updateSessionId: sessionId,
                    isHandoffAborted: () => channels.IsAborted);
                installed = true;
                window.ReportStage(3);
                engine.RelaunchUpdatedApp(sessionId);
                if (!await channels.WaitReadyAsync(TimeSpan.FromSeconds(90)))
                    throw new TimeoutException(I18n.T("update_ready_timeout"));

                SetupUpdateLog.Info($"Interactive update to {version} confirmed ready.");
                window.ShowCompleted();
                await Task.Delay(1200);
                window.Close();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Error("Interactive update failed: " + ex);
                if (!installed && channels != null && !channelsDisposed)
                    channels.SignalAbort();
                retainChannelsForRetry = installed && channels != null && !closing;
                if (!closing)
                    window.ShowFailure(ex, retainChannelsForRetry ? new Func<Task>(async () => {
                    if (closing) return;
                    engine.RelaunchUpdatedApp(args.UpdateSessionToken);
                    if (!await channels!.WaitReadyAsync(TimeSpan.FromSeconds(90)))
                        throw new TimeoutException(I18n.T("update_ready_timeout"));
                    SetupUpdateLog.Info("Updated application restart confirmed after retry.");
                    window.ShowCompleted();
                    await Task.Delay(1200);
                    if (!closing) window.Close();
                    Shutdown(0);
                    }) : null);
            }
            finally
            {
                // Retry owns the channels until Window.Closed. All other paths,
                // including pre-Proceed cancellation, release them only after
                // the awaited operation has completed.
                if (channels != null && !retainChannelsForRetry && !channelsDisposed)
                {
                    channels.Dispose();
                    channelsDisposed = true;
                }
            }
        }

        private void ApplyUpdatePalette(string? paletteArgument)
        {
            if (!UpdateSessionProtocol.TryDecodePalette(paletteArgument!, out string[] colors))
                return;

            Resources["WindowBgGradient"] = Brush(colors[0]);
            Resources["SidebarGradient"] = Brush(colors[1]);
            Resources["C.Accent"] = ParseColor(colors[3]);
            Resources["C.Accent.Dim"] = ParseColor(colors[4]);
            Resources["C.Accent.Glow"] = ParseColor(colors[4]);
            Resources["C.Accent.Deep"] = ParseColor(colors[5]);
            Resources["AccentBrush"] = Brush(colors[3]);
            Resources["AccentDimBrush"] = Brush(colors[4]);
            Resources["TextPrimaryBrush"] = Brush(colors[6]);
            Resources["TextSecondaryBrush"] = Brush(colors[7]);
            Resources["TextMutedBrush"] = Brush(colors[7]);
            Resources["BorderBrush"] = Brush(colors[8]);
        }

        private static void RedirectUninstallLogToRunDirectory()
        {
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (SetupStaging.IsStagedUpdateExecutable(self, SetupStaging.DefaultUpdatesRoot))
                SetupUpdateLog.FilePath = Path.Combine(Path.GetDirectoryName(self)!, "uninstall.log");
        }

        private async Task<int> RunSilentUninstall(SetupArgs args)
        {
            var engine = new HardenedInstallEngine { PurgeUserData = args.PurgeUserData };
            UninstallResult result = await engine.UninstallAsync(args.TargetDir);
            foreach (string failure in result.Failures)
                SetupUpdateLog.Error("Silent uninstall partial failure: " + failure);
            foreach (string residual in result.Residuals)
                SetupUpdateLog.Error("Silent uninstall residual: " + residual);
            foreach (string warning in result.Warnings)
                SetupUpdateLog.Warn("Silent uninstall note: " + warning);
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
