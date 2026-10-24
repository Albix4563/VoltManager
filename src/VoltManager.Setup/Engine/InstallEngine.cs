using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VoltManager.Setup.Engine
{
    public sealed class UninstallResult
    {
        public List<string> Failures { get; } = new List<string>();
        public List<string> Residuals { get; } = new List<string>();
        // Non-blocking notes (kept user data, removals pending a restart).
        public List<string> Warnings { get; } = new List<string>();
        internal List<string> PendingReboot { get; } = new List<string>();
        public bool HasResiduals => Residuals.Count > 0;
        public bool Success => Failures.Count == 0 && Residuals.Count == 0;
        public string Summary => string.Join("; ", Failures.Concat(Residuals));

        public void Add(string failure)
        {
            if (!string.IsNullOrWhiteSpace(failure))
                Failures.Add(failure);
        }

        public void AddResidual(string residual)
        {
            if (!string.IsNullOrWhiteSpace(residual))
                Residuals.Add(residual);
        }

        public void AddWarning(string warning)
        {
            if (!string.IsNullOrWhiteSpace(warning))
                Warnings.Add(warning);
        }
    }

    public class InstallEngine : IInstallUpdateEngine
    {
        private const string AppName        = "VoltManager";
        private const string AppExe         = "VoltManager.exe";
        private const string ARP_KEY        = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VoltManager";
        private const string INNO_ARP_KEY   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B7E64C0A-52D1-4E2B-9C0F-VOLTMGR00001}_is1";
        private const string STARTUP_TASK   = "VoltManagerAutostart";
        private const string WEBVIEW2_CLIENT = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        private readonly IInstallProcessOperations _processOperations;
        private readonly IPreviewReleaseClient _previewReleaseClient;
        private readonly Func<string?> _findLegacyInnoUninstaller;
        private readonly Func<string, CancellationToken, Task<(bool Exited, int ExitCode)?>> _runLegacyInnoUninstaller;
        private readonly Func<string?> _readInstallLocation;
        private readonly Action<string> _requireProtectedInstallLocation;
        private readonly Func<string?> _trustedInstalledPayloadZipPath;
        private readonly SetupWorkflowRunner _workflowRunner = new SetupWorkflowRunner();
        private readonly List<string> _operationWarnings = new List<string>();

        public event Action<string, double>? Progress; // (statusText, 0-100)
        public SetupWorkflowResult? LastOperationResult { get; private set; }
        public IReadOnlyList<string> LastWarnings => _operationWarnings;
        public bool UpdatePayloadInstalled { get; private set; }
        public bool UpdateRelaunchRequested { get; private set; }
        public event Action<string, bool>? UpdateStepTransition;

        public InstallEngine()
            : this(new SystemInstallProcessOperations(), GitHubPreviewReleaseClient.CreateDefault())
        {
        }

        internal InstallEngine(IInstallProcessOperations processOperations)
            : this(processOperations, GitHubPreviewReleaseClient.CreateDefault())
        {
        }

        internal InstallEngine(IInstallProcessOperations processOperations, IPreviewReleaseClient previewReleaseClient)
            : this(processOperations, previewReleaseClient, FindLegacyInnoUninstaller, RunLegacyInnoUninstallerAsync)
        {
        }

        internal InstallEngine(
            IInstallProcessOperations processOperations,
            IPreviewReleaseClient previewReleaseClient,
            Func<string?> findLegacyInnoUninstaller,
            Func<string, CancellationToken, Task<(bool Exited, int ExitCode)?>> runLegacyInnoUninstaller)
            : this(processOperations, previewReleaseClient, findLegacyInnoUninstaller, runLegacyInnoUninstaller,
                ReadInstallLocation, InstallDirectorySecurity.RequireProtectedElevatedUninstallerLocation,
                () => null)
        {
        }

        // Test-only dependency seam: production always uses the HKLM reader,
        // protected elevated-location check and locally embedded trusted inventory.
        internal InstallEngine(
            IInstallProcessOperations processOperations,
            IPreviewReleaseClient previewReleaseClient,
            Func<string?> findLegacyInnoUninstaller,
            Func<string, CancellationToken, Task<(bool Exited, int ExitCode)?>> runLegacyInnoUninstaller,
            Func<string?> readInstallLocation,
            Action<string> requireProtectedInstallLocation,
            Func<string?> trustedInstalledPayloadZipPath)
        {
            _processOperations = processOperations ?? throw new ArgumentNullException(nameof(processOperations));
            _previewReleaseClient = previewReleaseClient ?? throw new ArgumentNullException(nameof(previewReleaseClient));
            _findLegacyInnoUninstaller = findLegacyInnoUninstaller ?? throw new ArgumentNullException(nameof(findLegacyInnoUninstaller));
            _runLegacyInnoUninstaller = runLegacyInnoUninstaller ?? throw new ArgumentNullException(nameof(runLegacyInnoUninstaller));
            _readInstallLocation = readInstallLocation ?? throw new ArgumentNullException(nameof(readInstallLocation));
            _requireProtectedInstallLocation = requireProtectedInstallLocation ?? throw new ArgumentNullException(nameof(requireProtectedInstallLocation));
            _trustedInstalledPayloadZipPath = trustedInstalledPayloadZipPath ?? throw new ArgumentNullException(nameof(trustedInstalledPayloadZipPath));
        }

        public async Task InstallAsync(InstallOptions opts, string version, CancellationToken ct = default)
        {
            _operationWarnings.Clear();
            LastOperationResult = null;
            opts.InstallDir = InstallOptions.NormalizeInstallDir(opts.InstallDir);
            InstallTargetValidationResult targetValidation = InstallTargetValidator.ValidateInstallTarget(opts.InstallDir);
            if (!targetValidation.Ok)
            {
                string message = "Unsafe install target '" + opts.InstallDir + "': " + targetValidation.Reason;
                SetupUpdateLog.Error(message);
                throw new InvalidOperationException(message);
            }

            string effectiveVersion = version;
            string? previewPayloadZip = null;
            PreviewReleaseDownload? previewRelease = null;
            var steps = new List<SetupWorkflowStep>
            {
                new SetupWorkflowStep("validate-elevated-install-location", token =>
                {
                    // Even process termination and legacy migration have effects.
                    // Reject unsafe destinations before either can run.
                    _requireProtectedInstallLocation(opts.InstallDir);
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("validate-install-ownership", token =>
                {
                    // A forged marker or same-named file must never authorize
                    // process termination or execution of a legacy uninstaller.
                    InstallManifest.ValidateExistingInstallBeforeSideEffects(
                        opts.InstallDir, _trustedInstalledPayloadZipPath(), _requireProtectedInstallLocation);
                    // An HKLM uninstall path is not authenticated provenance for
                    // arbitrary legacy code. Require explicit manual migration.
                    if (_findLegacyInnoUninstaller() != null)
                        throw new InvalidDataException(
                            "Legacy Inno uninstaller cannot be independently authenticated; manual migration required.");
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("stop-processes", token =>
                {
                    Report(I18n.T("status_kill"), 0);
                    if (!_processOperations.StopForInstall(opts.InstallDir))
                        throw new InvalidOperationException("VoltManager processes are still running.");
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("remove-legacy-install", async token =>
                {
                    Report(I18n.T("status_migrate"), 5);
                    await RemoveLegacyInnoInstallAsync(token).ConfigureAwait(false);
                }),
                new SetupWorkflowStep("prepare-channel-payload", async token =>
                {
                    if (!IsPreviewChannel(opts))
                        return;
                    Report(I18n.T("status_download_preview"), 8);
                    previewRelease = await _previewReleaseClient.DownloadLatestAsync(
                        pct => Report(I18n.T("status_download_preview"), 8 + pct * 0.06), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    // Keep the SHA256-verified Preview installer (and its held read-only
                    // file handle) until the matching uninstaller is registered.
                    previewPayloadZip = ExtractPreviewPayloadZipIntoDirectory(
                        previewRelease.ExePath, previewRelease.StagingDirectory, token);
                    effectiveVersion = previewRelease.Version;
                }),
                new SetupWorkflowStep("extract-payload", async token =>
                {
                    Report(I18n.T("status_extract"), 15);
                    string? backupWarning = await ExtractPayloadAsync(
                        opts.InstallDir, token, previewPayloadZip, _requireProtectedInstallLocation).ConfigureAwait(false);
                    if (backupWarning != null && backupWarning.Length > 0)
                        _operationWarnings.Add(backupWarning);
                }),
                new SetupWorkflowStep("ensure-webview2", async token =>
                {
                    if (!WebView2Missing())
                        return;
                    Report(I18n.T("status_webview"), 65);
                    await InstallWebView2Async(token).ConfigureAwait(false);
                }),
                new SetupWorkflowStep("create-shortcuts", token =>
                {
                    Report(I18n.T("status_shortcuts"), 75);
                    CreateShortcuts(opts);
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("configure-startup", token =>
                {
                    if (opts.StartWithWindows)
                    {
                        Report(I18n.T("status_startup"), 82);
                        CreateStartupTask(opts.InstallDir, _operationWarnings);
                    }
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("register-installation", token =>
                {
                    Report(I18n.T("status_registry"), 88);
                    string uninstallerSource = ResolveUninstallerSource(IsPreviewChannel(opts), previewRelease);
                    _requireProtectedInstallLocation(opts.InstallDir);
                    RegisterUninstallerAndArp(opts.InstallDir, _operationWarnings,
                        uninstallerSource, () => WriteArpEntry(opts.InstallDir, effectiveVersion));
                    WriteInitialAppSettings(opts);
                    token.ThrowIfCancellationRequested();
                    Report("", 100);
                    return Task.CompletedTask;
                }),
            };

            try
            {
                CompleteWorkflow(await _workflowRunner.RunAsync(steps, ct).ConfigureAwait(false), ct);
            }
            finally
            {
                CleanupPreviewRelease(previewRelease, previewPayloadZip);
            }
        }

        public async Task UpdateAsync(int waitPid, string version, CancellationToken ct = default)
        {
            _operationWarnings.Clear();
            LastOperationResult = null;
            UpdatePayloadInstalled = false;
            UpdateRelaunchRequested = false;
            string? installDir = null;
            var steps = new List<SetupWorkflowStep>
            {
                new SetupWorkflowStep("validate-install-ownership", token =>
                {
                    installDir = ResolveValidatedUpdateInstallDirectory();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("wait-main-process", async token =>
                {
                    if (waitPid <= 0)
                        return;
                    bool exited = await _processOperations.WaitForExitAsync(
                        waitPid, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    if (!exited)
                        throw new InvalidOperationException("VoltManager did not exit before the update timeout.");
                }),
                new SetupWorkflowStep("resolve-installation", token =>
                {
                    // Verify again before StopInstalled to narrow the gap from
                    // the earlier read-only preflight. No path snapshot is atomic.
                    installDir = ResolveValidatedUpdateInstallDirectory();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("stop-processes", token =>
                {
                    if (!_processOperations.StopInstalled(installDir!))
                        throw new InvalidOperationException("VoltManager processes are still running.");
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("extract-payload", async token =>
                {
                    Report(I18n.T("status_extract"), 0);
                    string? backupWarning = await ExtractPayloadAsync(
                        installDir!, token, null, _requireProtectedInstallLocation).ConfigureAwait(false);
                    if (backupWarning != null && backupWarning.Length > 0)
                        _operationWarnings.Add(backupWarning);
                }),
                new SetupWorkflowStep("register-update", token =>
                {
                    Report(I18n.T("status_registry"), 90);
                    _requireProtectedInstallLocation(installDir!);
                    RegisterUninstallerAndArp(installDir!, _operationWarnings,
                        Assembly.GetExecutingAssembly().Location, () => WriteArpEntry(installDir!, version));
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
                new SetupWorkflowStep("complete-installation", token =>
                {
                    Report("", 100);
                    ScheduleDownloadedUpdateDelete();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }),
            };

            CompleteWorkflow(await _workflowRunner.RunAsync(steps, ct,
                (name, completed) => UpdateStepTransition?.Invoke(name, completed)).ConfigureAwait(false), ct);
            UpdatePayloadInstalled = true;
        }

        /// <summary>
        /// Requests application relaunch after a completed installation; retrying this
        /// operation does not reinstall any files. Readiness is acknowledged separately.
        /// </summary>
        public void RelaunchUpdatedApp(string? updateSessionId = null)
        {
            if (!UpdatePayloadInstalled)
                throw new InvalidOperationException("Update payload has not completed installation.");
            string installDir = ResolveValidatedUpdateInstallDirectory();
            string exe = Path.Combine(installDir, AppExe);
            if (!File.Exists(exe))
                throw new FileNotFoundException("Installed VoltManager executable is missing.", exe);
            UpdateRelaunchRequested = false;
            string args = "--updated";
            if (!string.IsNullOrEmpty(updateSessionId))
            {
                if (!Guid.TryParseExact(updateSessionId, "N", out _))
                    throw new ArgumentException("Invalid update session identifier.", nameof(updateSessionId));
                args += " --update-session " + updateSessionId;
            }
            _processOperations.Start(exe, args);
            UpdateRelaunchRequested = true;
        }

        public void RetryUpdateRelaunch(string? updateSessionId = null)
            => RelaunchUpdatedApp(updateSessionId);

        /// <summary>
        /// Read-only preflight for UpdateInstallCoordinator, which may clear
        /// WebView2 cache before UpdateAsync runs. UpdateAsync repeats it.
        /// </summary>
        public void ValidateUpdateTargetBeforeSideEffects()
            => ResolveValidatedUpdateInstallDirectory();

        private string ResolveValidatedUpdateInstallDirectory()
        {
            string? installDir = _readInstallLocation();
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                throw new InvalidOperationException("VoltManager install directory not found in registry.");
            InstallTargetValidationResult validation = InstallTargetValidator.ValidateInstallTarget(installDir);
            if (!validation.Ok)
                throw new InvalidOperationException("Unsafe registered install directory: " + validation.Reason);
            _requireProtectedInstallLocation(installDir!);
            InstallManifest.ValidateExistingInstallBeforeSideEffects(
                installDir!, _trustedInstalledPayloadZipPath(), _requireProtectedInstallLocation);
            return installDir!;
        }

        private void CompleteWorkflow(SetupWorkflowResult result, CancellationToken cancellationToken)
        {
            foreach (string warning in _operationWarnings)
                result.AddWarning(warning);
            LastOperationResult = result;
            if (result.Success)
                return;
            if (result.Cancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(result.Summary, result.FailureException);
            }

            throw new InvalidOperationException(
                "Setup step '" + (result.FailedStep ?? "unknown") + "' failed: " + result.Summary,
                result.FailureException);
        }

        public static string ResolveInstallDir(string? targetDir = null)
        {
            if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
                return Path.GetFullPath(targetDir);

            string? fromRegistry = ReadInstallLocation();
            if (!string.IsNullOrWhiteSpace(fromRegistry) && Directory.Exists(fromRegistry))
                return Path.GetFullPath(fromRegistry);

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
            if (Directory.Exists(fallback))
                return Path.GetFullPath(fallback);

            return !string.IsNullOrWhiteSpace(targetDir)
                ? Path.GetFullPath(targetDir)
                : (!string.IsNullOrWhiteSpace(fromRegistry) ? Path.GetFullPath(fromRegistry) : fallback);
        }

        /// <summary>
        /// If this process is running from the install dir, copy self to a protected
        /// per-run update staging directory and relaunch.
        /// Returns true when the caller should exit (handoff done).
        /// For silent mode waits for the child and sets <paramref name="exitCode"/>.
        /// For UI mode starts the child and returns immediately with exitCode 0.
        /// </summary>
        public static bool TryRelaunchFromTempIfNeeded(SetupArgs args, out int exitCode)
        {
            exitCode = 0;
            if (!string.IsNullOrWhiteSpace(args.TargetDir))
            {
                InstallTargetValidationResult validation = InstallTargetValidator.ValidateUninstallTarget(args.TargetDir);
                if (!validation.Ok)
                {
                    SetupUpdateLog.Error("Unsafe uninstall target '" + args.TargetDir + "': " + validation.Reason);
                    exitCode = 1;
                    return true;
                }
            }
            if (args.FromTemp) return false;

            string installDir = ResolveInstallDir(args.TargetDir);
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return false;

            string self = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(self) || !File.Exists(self))
                return false;

            if (!IsPathUnder(self, installDir))
                return false;

            string tempDir = SetupStaging.CreateProtectedRunDirectory(SetupStaging.DefaultUpdatesRoot);
            string tempExe = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.Copy(self, tempExe, true);
            }
            catch
            {
                SetupStaging.DeleteRunDirectoryBestEffort(tempDir, SetupUpdateLog.Warn);
                throw;
            }

            var parts = new List<string> { "/uninstall", "--from-temp", "--target", installDir };
            if (args.SilentUninstall) parts.Add("/SILENT");
            if (args.PurgeUserData) parts.Add("/PURGE");
            if (!string.IsNullOrEmpty(args.Language))
            {
                parts.Add("--lang");
                parts.Add(args.Language);
            }

            string cmdLine = string.Join(" ", parts.ConvertAll(QuoteArg));
            var psi = new ProcessStartInfo(tempExe, cmdLine)
            {
                UseShellExecute = !args.SilentUninstall,
                CreateNoWindow = args.SilentUninstall,
            };

            Process? child;
            try
            {
                child = Process.Start(psi);
            }
            catch
            {
                SetupStaging.DeleteRunDirectoryBestEffort(tempDir, SetupUpdateLog.Warn);
                throw;
            }
            if (child == null)
            {
                SetupStaging.DeleteRunDirectoryBestEffort(tempDir, SetupUpdateLog.Warn);
                exitCode = 1;
                return true;
            }

            if (args.SilentUninstall)
            {
                child.WaitForExit();
                exitCode = child.ExitCode;
                child.Dispose();
                SetupStaging.DeleteRunDirectoryBestEffort(tempDir, SetupUpdateLog.Warn);
            }
            else
            {
                exitCode = 0;
                child.Dispose();
            }

            return true;
        }

        internal static bool TryDeleteDirectoryTree(string dir, out string error)
        {
            error = "";
            if (!Directory.Exists(dir)) return true;

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    MakeWritableTree(dir);
                    Directory.Delete(dir, true);
                    if (!Directory.Exists(dir)) return true;
                    error = "directory still exists after delete";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                Thread.Sleep(300 * attempt);
            }

            return !Directory.Exists(dir);
        }

        // ── Private helpers ──────────────────────────────────────────────

        private static bool IsPreviewChannel(InstallOptions opts)
            => InstallOptions.NormalizeChannel(opts?.UpdateChannel) == "preview";

        internal static string ResolveUninstallerSource(bool previewChannel, PreviewReleaseDownload? previewRelease)
        {
            if (!previewChannel)
                return Assembly.GetExecutingAssembly().Location;
            return previewRelease?.ExePath
                ?? throw new InvalidOperationException("Verified Preview uninstaller is missing.");
        }

        /// <summary>
        /// Reads the payload.zip embedded resource out of the downloaded Preview
        /// setup exe in a temporary AppDomain so the downloaded VoltManagerSetup
        /// assembly never enters the WPF application's AppDomain.
        /// </summary>
        private static string ExtractPreviewPayloadZip(string previewSetupExe, CancellationToken ct)
        {
            string tempDir = CreateUniqueTempDirectory();
            try
            {
                return ExtractPreviewPayloadZipIntoDirectory(previewSetupExe, tempDir, ct);
            }
            catch
            {
                DeleteOwnedTempFile(Path.Combine(tempDir, "VoltManagerPreviewPayload.zip"), tempDir);
                TryDeleteTempDirectory(tempDir);
                throw;
            }
        }

        private static string ExtractPreviewPayloadZipIntoDirectory(
            string previewSetupExe, string extractionDirectory, CancellationToken ct)
        {
            AppDomain? extractionDomain = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                string zipPath = Path.Combine(extractionDirectory, "VoltManagerPreviewPayload.zip");

                extractionDomain = AppDomain.CreateDomain(
                    "VoltManagerPreviewPayload-" + Guid.NewGuid().ToString("N"),
                    null,
                    new AppDomainSetup { ApplicationBase = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) });
                var extractor = (PreviewPayloadExtractor)extractionDomain.CreateInstanceFromAndUnwrap(
                    Assembly.GetExecutingAssembly().Location,
                    typeof(PreviewPayloadExtractor).FullName);

                if (!extractor.CopyEmbeddedPayload(previewSetupExe, zipPath))
                    throw new InvalidOperationException(I18n.T("err_preview_payload"));

                ct.ThrowIfCancellationRequested();
                return zipPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    I18n.T("err_preview_payload") + " " + ex.Message, ex);
            }
            finally
            {
                if (extractionDomain != null)
                    AppDomain.Unload(extractionDomain);
            }
        }

        private static void CleanupPreviewRelease(PreviewReleaseDownload? release, string? extractedZip)
        {
            if (release == null) return;
            release.Dispose();
            string runDirectory = release.StagingDirectory;
            try
            {
                if (!Directory.Exists(runDirectory)) return;
                string? current = Path.GetFullPath(runDirectory);
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.Exists(current) &&
                        (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Preview staging ancestry contains a link: " + current);
                    string? parent = Path.GetDirectoryName(current);
                    if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                    current = parent;
                }

                // Only the two files created/verified during this download belong to
                // the current run; unknown files are retained for manual review.
                if (!string.IsNullOrWhiteSpace(extractedZip))
                    DeletePreviewOwnedFile(extractedZip!, runDirectory);
                DeletePreviewOwnedFile(release.ExePath, runDirectory);
                if (!Directory.EnumerateFileSystemEntries(runDirectory).Any())
                    Directory.Delete(runDirectory, recursive: false);
                else
                    SetupUpdateLog.Warn("Preview staging retained for manual review: " + runDirectory);
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Preview staging cleanup could not be verified: " + ex.Message);
            }
        }

        private static void DeletePreviewOwnedFile(string filePath, string runDirectory)
        {
            string fullFile = Path.GetFullPath(filePath);
            if (!string.Equals(Path.GetDirectoryName(fullFile), Path.GetFullPath(runDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Preview artifact is outside its download directory: " + fullFile);
            if (Directory.Exists(fullFile))
                throw new InvalidOperationException("Preview artifact was replaced with a directory: " + fullFile);
            if (!File.Exists(fullFile)) return;
            if ((File.GetAttributes(fullFile) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Preview artifact is a link: " + fullFile);
            File.Delete(fullFile);
        }

        public sealed class PreviewPayloadExtractor : MarshalByRefObject
        {
            public bool CopyEmbeddedPayload(string previewSetupExe, string zipPath)
            {
                var asm = Assembly.Load(File.ReadAllBytes(previewSetupExe));
                string? resName = Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
                if (resName == null)
                    return false;

                using (var src = asm.GetManifestResourceStream(resName)!)
                using (var dst = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst, 81920);

                return true;
            }
        }

        // Legge il payload precedente solo come dati: il dominio reflection-only
        // viene scaricato senza eseguire initializer o dipendenze del vecchio setup.
        // Il caricamento da percorso mappa l'immagine: Load(byte[]) impiega ~20 s su 150 MB.
        internal static byte[]? ReadEmbeddedPayloadWithoutExecution(string setupImagePath)
        {
            AppDomain domain = AppDomain.CreateDomain(
                "VoltManagerInstalledPayload-" + Guid.NewGuid().ToString("N"),
                null,
                new AppDomainSetup { ApplicationBase = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) });
            try
            {
                var reader = (InstalledPayloadReader)domain.CreateInstanceFromAndUnwrap(
                    Assembly.GetExecutingAssembly().Location, typeof(InstalledPayloadReader).FullName);
                return reader.Read(setupImagePath);
            }
            finally
            {
                AppDomain.Unload(domain);
            }
        }

        public sealed class InstalledPayloadReader : MarshalByRefObject
        {
            public byte[]? Read(string setupImagePath)
            {
                Assembly image = Assembly.ReflectionOnlyLoadFrom(setupImagePath);
                string[] names = Array.FindAll(image.GetManifestResourceNames(),
                    n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
                if (names.Length == 0)
                    return null;
                if (names.Length > 1)
                    throw new InvalidDataException("Installed uninstaller declares multiple payload archives.");
                using (Stream source = image.GetManifestResourceStream(names[0])!)
                {
                    if (source.Length > int.MaxValue)
                        throw new InvalidDataException("Installed payload exceeds safety limits.");
                    using (var copy = new MemoryStream((int)source.Length))
                    {
                        source.CopyTo(copy, 81920);
                        return copy.ToArray();
                    }
                }
            }
        }

        protected void Report(string msg, double pct) => Progress?.Invoke(msg, pct);

        internal static bool StopProcessesForInstall(string installDir)
            // Only the caller's independently validated install root is in
            // scope. Never stop a second, HKLM-discovered tree without its own
            // verified ownership preflight.
            => StopRunningInstalledProcesses(installDir);

        internal static bool StopRunningInstalledProcesses(string installDir)
        {
            foreach (string processName in new[] { "VoltManager.HardwareService", "VoltManager.Supervisor", "VoltManager" })
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (path == null || path.Length == 0 || !IsPathUnder(path, installDir))
                            continue;

                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch
                    {
                        // Verification below decides whether replacing files is safe.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            Thread.Sleep(300);
            return !AnyProcessRunningFromDirectory("VoltManager", installDir) &&
                   !AnyProcessRunningFromDirectory("VoltManager.Supervisor", installDir) &&
                   !AnyProcessRunningFromDirectory("VoltManager.HardwareService", installDir);
        }

        private static bool AnyProcessRunningFromDirectory(string processName, string directory)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path != null && path.Length > 0 && IsPathUnder(path, directory))
                        return true;
                }
                catch
                {
                    // Ignore inaccessible same-name processes outside this install. The
                    // elevated updater can inspect the supervisor it launched beside the app.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return false;
        }

        private static bool IsPathUnder(string path, string directory)
        {
            string fullPath = Path.GetFullPath(path);
            string fullDir = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
        }

        private static string QuoteArg(string value)
        {
            if (value.Length == 0) return "\"\"";
            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private Task RemoveLegacyInnoInstallAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string? unins = _findLegacyInnoUninstaller();
            if (unins == null) return Task.CompletedTask;

            // Rechecking this mutable HKLM path does NOT authenticate the
            // executable. Even if the early ownership gate observed no entry,
            // a new/changed legacy path must never be executed implicitly.
            throw new InvalidDataException(
                "Legacy Inno uninstaller cannot be independently authenticated; manual migration required.");
        }

        private static string? FindLegacyInnoUninstaller()
        {
            using var key = Registry.LocalMachine.OpenSubKey(INNO_ARP_KEY);
            if (key == null) return null;

            string? loc = key.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(loc))
                throw new InvalidOperationException("Legacy Inno uninstall location is missing.");

            string unins = Path.Combine(loc, "unins000.exe");
            if (!File.Exists(unins))
                throw new InvalidOperationException("Legacy Inno uninstaller was not found at '" + unins + "'.");
            return unins;
        }

        private static async Task<(bool Exited, int ExitCode)?> RunLegacyInnoUninstallerAsync(string unins, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(unins, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            { UseShellExecute = true };
            using var process = Process.Start(psi);
            if (process == null)
                return null;

            // Wait in short slices so cancellation is observed without leaving the workflow
            // blocked for 60 seconds. Do not kill a legacy uninstaller mid-operation.
            bool exited = await Task.Run(() =>
            {
                var elapsed = Stopwatch.StartNew();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    long remaining = 60_000 - elapsed.ElapsedMilliseconds;
                    if (remaining <= 0)
                        return false;
                    if (process.WaitForExit((int)Math.Min(250, remaining)))
                    {
                        ct.ThrowIfCancellationRequested();
                        return true;
                    }
                }
            }).ConfigureAwait(false);
            return (exited, exited ? process.ExitCode : 0);
        }

        private static async Task<string?> ExtractPayloadAsync(string destDir, CancellationToken ct, string? payloadZipPath = null,
            Action<string>? requireProtectedInstallLocation = null)
        {
            Directory.CreateDirectory(destDir);

            string? tempZip = payloadZipPath;
            string? ownedTempDir = null;
            string? stagingDir = null;

            try
            {
                if (tempZip == null)
                {
                    // Extract payload.zip from embedded resources.
                    var asm = Assembly.GetExecutingAssembly();
                    string? resName = Array.Find(asm.GetManifestResourceNames(),
                        n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

                    if (resName == null)
                        throw new InvalidDataException("Embedded install payload.zip resource is missing; installation cannot proceed.");

                    ownedTempDir = CreateUniqueTempDirectory();
                    tempZip = Path.Combine(ownedTempDir, "VoltManagerPayload.zip");
                    using (var src = asm.GetManifestResourceStream(resName)!)
                    using (var fs = File.Create(tempZip))
                        await src.CopyToAsync(fs, 81920, ct);
                }

                string fullDest = Path.GetFullPath(destDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(fullDest)
                    ?? throw new InvalidOperationException("Installation directory has no parent.");
                string installName = Path.GetFileName(fullDest);

                // One background thread for the whole locked block: the mutex is thread-affine
                // (no await inside), and waiting for it must not freeze the setup window.
                string zipPath = tempZip;
                return await Task.Run(() =>
                {
                    using (InstallDirectoryLock.Acquire(fullDest, InstallLockTimeout, ct))
                    {
                        stagingDir = Path.Combine(parent,
                            "." + installName + ".staging-" + Path.GetRandomFileName());

                        Directory.CreateDirectory(stagingDir);
                        ZipFile.ExtractToDirectory(zipPath, stagingDir);
                        ct.ThrowIfCancellationRequested();

                        string backupDir = Path.Combine(parent,
                            "." + installName + ".backup-" + Path.GetRandomFileName());
                        // Stable: derive hashes from the setup assembly's embedded,
                        // independently trusted ZIP, never from its mutable TEMP copy.
                        // Preview: use the explicitly supplied verified release ZIP.
                        string? backupWarning = ReplaceInstallDirectoryContentsCore(
                            destDir, stagingDir, backupDir, payloadZipPath, MoveEntry, requireProtectedInstallLocation);

                        // Inspect only empty leftovers while this installer holds the lock.
                        // Nonempty backups are retained for explicit review.
                        DeleteLeftoverSwapDirectories(parent, installName);
                        return backupWarning;
                    }
                }, ct);
            }
            catch (Exception installFailure)
            {
                bool stagingRetained = false;
                try
                {
                    if (!string.IsNullOrWhiteSpace(stagingDir) && Directory.Exists(stagingDir))
                    {
                        stagingRetained = (File.GetAttributes(stagingDir) & FileAttributes.ReparsePoint) != 0 ||
                            Directory.EnumerateFileSystemEntries(stagingDir).Any();
                    }
                }
                catch (Exception probeFailure)
                {
                    // If staging cannot be inspected, retain and report its location.
                    stagingRetained = true;
                    SetupUpdateLog.Warn("Cannot inspect failed install staging: " + probeFailure.Message);
                }

                if (stagingRetained)
                {
                    string detail = "Installation failed; retained staging requires manual review at " + stagingDir +
                        ". Original error: " + installFailure.Message;
                    SetupUpdateLog.Warn(detail);
                    if (installFailure is OperationCanceledException)
                        throw new OperationCanceledException(detail, installFailure, ct);
                    throw new InvalidOperationException(detail, installFailure);
                }
                throw;
            }
            finally
            {
                // A failed swap may have moved new files back into staging for
                // manual recovery. Never recursively delete that directory.
                TryDeleteEmptyStagingDirectory(stagingDir);
                DeleteOwnedTempFile(tempZip, ownedTempDir);
                TryDeleteTempDirectory(ownedTempDir);
            }
        }

        private static void TryDeleteEmptyStagingDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (Directory.Exists(path) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                    !Directory.EnumerateFileSystemEntries(path).Any())
                    Directory.Delete(path, recursive: false);
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Could not remove empty install staging directory " + path + ": " + ex.Message);
            }
        }

        private static string CreateUniqueTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(path);
            return path;
        }

        internal static void DeleteOwnedTempFile(string? filePath, string? ownedTempDir)
        {
            // ponytail: single-file owned temp cleanup; skipped: multi-process shared temp tracker, add when background workers share temp trees.
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(ownedTempDir))
                return;
            try
            {
                string fullFile = Path.GetFullPath(filePath);
                string fullDir = Path.GetFullPath(ownedTempDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(fullFile), fullDir, StringComparison.OrdinalIgnoreCase))
                    return;
                if (!File.Exists(fullFile) || Directory.Exists(fullFile))
                    return;
                if ((File.GetAttributes(fullFile) & FileAttributes.ReparsePoint) != 0 ||
                    UninstallAppData.HasLinkedAncestor(fullDir))
                    return;
                File.Delete(fullFile);
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Could not remove setup temporary file " + filePath + ": " + ex.Message);
            }
        }

        private static void TryDeleteTempDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                // The random name is not proof that this still is our directory.
                // It may have been replaced with a junction or arbitrary files;
                // never traverse or remove such content by path.
                string fullPath = Path.GetFullPath(path);
                string tempRoot = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    SetupUpdateLog.Warn("Unverified setup temporary directory retained: " + fullPath);
                    return;
                }
                if (!Directory.Exists(fullPath)) return;
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                    Directory.EnumerateFileSystemEntries(fullPath).Any())
                {
                    SetupUpdateLog.Warn("Nonempty or linked setup temporary directory retained for manual review: " + fullPath);
                    return;
                }
                Directory.Delete(fullPath, recursive: false);
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Could not verify/remove empty setup temporary directory: " + ex.Message);
            }
        }

        /// <summary>
        /// Swaps the install directory contents for the staged payload. The previous files are
        /// renamed into <paramref name="backupDir"/> (same volume) instead of being deleted, so a
        /// locked file or a failed move rolls back to the previous installation.
        /// </summary>
        internal static string? ReplaceInstallDirectoryContents(
            string destDir, string stagingDir, string backupDir, string? trustedPayloadZipPath = null)
            => ReplaceInstallDirectoryContentsCore(destDir, stagingDir, backupDir, trustedPayloadZipPath, MoveEntry);

        // Internal mover seam permits deterministic rollback-failure tests; production
        // always supplies MoveEntry and uses the same move semantics as before.
        internal static string? ReplaceInstallDirectoryContentsCore(
            string destDir, string stagingDir, string backupDir, string? trustedPayloadZipPath,
            Action<string, string> moveEntry, Action<string>? requireProtectedInstallLocation = null)
        {
            if (moveEntry == null) throw new ArgumentNullException(nameof(moveEntry));
            // A matching path may already contain unrelated files or be a reparse point.
            // A backup must be new for this invocation: never merge into a preexisting tree.
            if (Directory.Exists(backupDir) || File.Exists(backupDir))
                throw new InvalidOperationException("Install backup directory already exists: " + backupDir);

            if (Directory.Exists(stagingDir) &&
                (File.GetAttributes(stagingDir) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Install staging directory is a reparse point: " + stagingDir);
            if (Directory.Exists(destDir) &&
                (File.GetAttributes(destDir) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Install destination directory is a reparse point: " + destDir);

            string[] stagedNames = Directory.GetFileSystemEntries(stagingDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray();
            // An untrusted/mutable extracted ZIP cannot authenticate itself.
            // Null means use the embedded assembly resource as authority.
            InstallManifest.ValidateStagedPayload(stagingDir, trustedPayloadZipPath);
            // Ogni vecchio file è verificato contro A: ciò permette rimozioni e rinomine
            // in B senza spostare dati estranei presenti nelle directory possedute.
            HashSet<string> ownedNames = InstallManifest.GetOwnedNamesForReplacement(
                destDir, stagedNames, trustedPayloadZipPath,
                requireProtectedInstallLocation: requireProtectedInstallLocation);

            Dictionary<string, string> backupSnapshot = InstallManifest.SnapshotBackupFiles(destDir, ownedNames);
            Directory.CreateDirectory(destDir);
            Directory.CreateDirectory(backupDir);
            var backedUp = new List<string>();
            var installed = new List<string>();

            try
            {
                foreach (string path in Directory.GetFileSystemEntries(destDir))
                {
                    string name = Path.GetFileName(path);
                    if (!ownedNames.Contains(name))
                        continue;
                    moveEntry(path, Path.Combine(backupDir, name));
                    backedUp.Add(name);
                }

                foreach (string entry in Directory.GetFileSystemEntries(stagingDir)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(entry);
                    moveEntry(entry, Path.Combine(destDir, name));
                    installed.Add(name);
                }

                string marker = Path.Combine(destDir, InstallManifest.FileName);
                try { InstallManifest.Write(destDir, stagedNames); }
                finally
                {
                    // A failed manifest write may still have created a partial file;
                    // track only an entry that actually exists for reverse rollback.
                    if (File.Exists(marker) || Directory.Exists(marker))
                        installed.Add(InstallManifest.FileName);
                }
            }
            catch (Exception installFailure)
            {
                var rollbackFailures = new List<Exception>();
                for (int i = installed.Count - 1; i >= 0; i--)
                {
                    string name = installed[i];
                    try { MoveInstalledEntryBackToStaging(destDir, stagingDir, name, moveEntry); }
                    catch (Exception ex)
                    {
                        rollbackFailures.Add(new IOException("Could not return new entry " + name + " to staging.", ex));
                        SetupUpdateLog.Warn("Rollback: could not return new entry " + name + " to staging: " + ex.Message);
                    }
                }

                for (int i = backedUp.Count - 1; i >= 0; i--)
                {
                    string name = backedUp[i];
                    try { moveEntry(Path.Combine(backupDir, name), Path.Combine(destDir, name)); }
                    catch (Exception ex)
                    {
                        rollbackFailures.Add(new IOException("Could not restore previous entry " + name + ".", ex));
                        SetupUpdateLog.Warn("Rollback: could not restore " + name + ": " + ex.Message);
                    }
                }

                // Keep the backup on disk if anything could not be put back.
                if (rollbackFailures.Count == 0)
                    TryDeleteBackupDirectory(backupDir);
                else
                {
                    SetupUpdateLog.Warn("Previous installation kept in " + backupDir);
                    throw new InvalidOperationException(
                        "Installation failed and rollback is incomplete. Previous installation backup retained at " + backupDir +
                        ". Rollback errors: " + string.Join("; ", rollbackFailures.Select(ex => ex.Message)),
                        new AggregateException(new[] { installFailure }.Concat(rollbackFailures)));
                }
                throw;
            }

            return TryDeleteVerifiedBackupDirectory(backupDir, backedUp, backupSnapshot);
        }

        private static string? TryDeleteVerifiedBackupDirectory(string backupDir, IEnumerable<string> backedUp,
            IReadOnlyDictionary<string, string> backupSnapshot)
        {
            // ponytail: hash snapshot narrows path races; use handle-bound deletion if concurrent
            // replacement of protected setup directories must be supported.
            try
            {
                if (!Directory.Exists(backupDir)) return null;

                // Refuse if the backup root or any entry/subtree contains a reparse point.
                if (SetupStaging.ContainsReparsePointInTree(backupDir))
                {
                    string warning = "Previous installation backup retained for manual review: " + backupDir;
                    SetupUpdateLog.Warn(warning);
                    return warning;
                }

                // Refuse if the backup contains entries not in backedUp.
                var backedUpSet = new HashSet<string>(backedUp, StringComparer.OrdinalIgnoreCase);
                foreach (string entry in Directory.GetFileSystemEntries(backupDir))
                {
                    string name = Path.GetFileName(entry);
                    if (!backedUpSet.Contains(name))
                    {
                        string warning = "Previous installation backup retained for manual review: " + backupDir;
                        SetupUpdateLog.Warn(warning);
                        return warning;
                    }
                }

                InstallManifest.ValidateBackupSnapshot(backupDir, backupSnapshot);
                // Delete only the names in backedUp, clearing ReadOnly only on non-reparse entries.
                foreach (string name in backedUpSet)
                {
                    string target = Path.Combine(backupDir, name);
                    if (File.Exists(target))
                    {
                        FileAttributes attrs = File.GetAttributes(target);
                        if ((attrs & FileAttributes.ReparsePoint) == 0)
                            MakeWritable(target);
                        File.Delete(target);
                    }
                    else if (Directory.Exists(target))
                    {
                        MakeWritableTree(target);
                        Directory.Delete(target, recursive: true);
                    }
                }

                if (Directory.EnumerateFileSystemEntries(backupDir).Any())
                {
                    string warning = "Previous installation backup retained for manual review: " + backupDir;
                    SetupUpdateLog.Warn(warning);
                    return warning;
                }

                Directory.Delete(backupDir, recursive: false);
                return null;
            }
            catch (Exception ex)
            {
                string warning = "Could not delete install backup " + backupDir + ": " + ex.Message;
                SetupUpdateLog.Warn(warning);
                return warning;
            }
        }

        private static void MoveInstalledEntryBackToStaging(
            string destDir, string stagingDir, string name, Action<string, string> moveEntry)
        {
            string installedPath = Path.Combine(destDir, name);
            string stagedPath = Path.Combine(stagingDir, name);
            if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
                throw new IOException("Original staged path is already occupied: " + stagedPath);

            foreach (string directory in new[] { destDir, stagingDir })
                if (!Directory.Exists(directory) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Rollback directory is missing or linked: " + directory);

            if (!File.Exists(installedPath) && !Directory.Exists(installedPath))
                throw new IOException("New install entry is missing: " + installedPath);
            if ((File.GetAttributes(installedPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("New install entry is a link: " + installedPath);

            // Rename back into the original staging tree. A directory can contain
            // newly created or unverified children, so do not delete it recursively.
            moveEntry(installedPath, stagedPath);
        }

        /// <summary>
        /// A swap directory can outlive its install. Its predictable name cannot
        /// establish ownership, even if the directory happens to be empty.
        /// </summary>
        internal static void DeleteLeftoverSwapDirectories(string parent, string installName)
        {
            try
            {
                foreach (string pattern in new[] { "." + installName + ".backup-*", "." + installName + ".staging-*" })
                    foreach (string dir in Directory.GetDirectories(parent, pattern))
                        SetupUpdateLog.Warn("Unverified install swap directory retained for manual review: " + dir);
            }
            catch (Exception ex)
            {
                SetupUpdateLog.Warn("Could not scan for leftover install directories: " + ex.Message);
            }
        }

        private static readonly TimeSpan InstallLockTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Serializes installers targeting the same folder (e.g. a silent auto-update and a
        /// manual run), so one never swaps or cleans up while another is mid-install.
        /// </summary>
        internal sealed class InstallDirectoryLock : IDisposable
        {
            private readonly Mutex _mutex;

            private InstallDirectoryLock(Mutex mutex)
            {
                _mutex = mutex;
            }

            internal static InstallDirectoryLock Acquire(string fullDest, TimeSpan timeout, CancellationToken ct = default)
            {
                var mutex = new Mutex(false, MutexName(fullDest));
                try
                {
                    bool acquired = false;
                    var wait = Stopwatch.StartNew();
                    // Short slices so a cancelled setup does not keep waiting for the full timeout.
                    while (!acquired && wait.Elapsed < timeout)
                    {
                        ct.ThrowIfCancellationRequested();
                        TimeSpan slice = timeout - wait.Elapsed;
                        if (slice > TimeSpan.FromMilliseconds(250)) slice = TimeSpan.FromMilliseconds(250);
                        if (slice < TimeSpan.Zero) slice = TimeSpan.Zero;
                        try { acquired = mutex.WaitOne(slice); }
                        catch (AbandonedMutexException) { acquired = true; }
                    }
                    if (!acquired)
                        throw new IOException("Another VoltManager setup is installing into this folder.");
                    return new InstallDirectoryLock(mutex);
                }
                catch
                {
                    mutex.Dispose();
                    throw;
                }
            }

            internal static string MutexName(string fullDest)
            {
                string normalized = Path.GetFullPath(fullDest)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToUpperInvariant();
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized));
                    return @"Global\VoltManagerSetup_Install_" + BitConverter.ToString(hash, 0, 16).Replace("-", "");
                }
            }

            public void Dispose()
            {
                try { _mutex.ReleaseMutex(); } catch { /* best-effort: mutex may no longer be owned during teardown. */ }
                _mutex.Dispose();
            }
        }

        private static void MoveEntry(string source, string target)
        {
            if (File.Exists(source))
                File.Move(source, target);
            else
                Directory.Move(source, target);
        }

        private static string? TryDeleteBackupDirectory(string backupDir)
        {
            try
            {
                if (!Directory.Exists(backupDir)) return null;
                // Manifest ownership is only tracked at the top level. A moved directory
                // may contain user data, reparse points or hardlinks; never recursively
                // delete a backup based solely on its generated name.
                if (Directory.EnumerateFileSystemEntries(backupDir).Any())
                {
                    string warning = "Previous installation backup retained for manual review: " + backupDir;
                    SetupUpdateLog.Warn(warning);
                    return warning;
                }
                Directory.Delete(backupDir, recursive: false);
                return null;
            }
            catch (Exception ex)
            {
                string warning = "Could not delete install backup " + backupDir + ": " + ex.Message;
                SetupUpdateLog.Warn(warning);
                return warning;
            }
        }

        private static void MakeWritableTree(string dir)
        {
            // ponytail: iterative non-reparse attribute clearing; skipped: transactional rollback of file attributes, add when attribute restore on cancel is required.
            var pending = new Stack<string>();
            pending.Push(dir);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(current);
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }

                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    continue;

                MakeWritable(current);

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    // Deliberately avoid SearchOption.AllDirectories: it follows junctions.
                    try
                    {
                        foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                            pending.Push(entry);
                    }
                    catch (DirectoryNotFoundException) { /* Entry removed concurrently. */ }
                    catch (UnauthorizedAccessException) { /* Locked or restricted entry. */ }
                }
            }
        }

        private static void MakeWritable(string path)
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(path);
                FileAttributes stripped = attrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
                if (stripped != attrs)
                    File.SetAttributes(path, stripped);
            }
            catch (FileNotFoundException) { /* Entry removed concurrently. */ }
            catch (DirectoryNotFoundException) { /* Entry removed concurrently. */ }
            catch (UnauthorizedAccessException) { /* Insufficient permissions to modify attributes. */ }
        }

        private static bool WebView2Missing()
        {
            foreach (var (hive, path) in new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + WEBVIEW2_CLIENT),
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WEBVIEW2_CLIENT),
                (Registry.CurrentUser,  @"Software\Microsoft\EdgeUpdate\Clients\" + WEBVIEW2_CLIENT),
            })
            {
                using var k = hive.OpenSubKey(path);
                var pv = k?.GetValue("pv") as string;
                if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0") return false;
            }
            return true;
        }

        private static async Task InstallWebView2Async(CancellationToken ct)
        {
            var asm = Assembly.GetExecutingAssembly();
            string? resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("MicrosoftEdgeWebview2Setup.exe", StringComparison.OrdinalIgnoreCase));
            if (resName == null)
            {
                SetupUpdateLog.Warn("WebView2 bootstrapper resource is missing.");
                if (WebView2Missing())
                    throw new InvalidOperationException("Microsoft Edge WebView2 Runtime is not installed.");
                return;
            }

            string tempDir = CreateUniqueTempDirectory();
            string tmp = Path.Combine(tempDir, "MicrosoftEdgeWebview2Setup.exe");
            try
            {
                using (var src = asm.GetManifestResourceStream(resName)!)
                using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, 81920, ct);

                using var p = Process.Start(new ProcessStartInfo(tmp, "/silent /install")
                { UseShellExecute = true });
                if (p == null)
                {
                    SetupUpdateLog.Warn("WebView2 bootstrapper process did not start.");
                }
                else
                {
                    bool exited = await Task.Run(() => p.WaitForExit(300_000), ct);
                    if (!exited)
                        SetupUpdateLog.Warn("WebView2 bootstrapper timed out after 300 seconds.");
                    else if (p.ExitCode != 0)
                        SetupUpdateLog.Warn("WebView2 bootstrapper exited with code " + p.ExitCode + ".");
                }

                if (WebView2Missing())
                    throw new InvalidOperationException("Microsoft Edge WebView2 Runtime installation failed.");
            }
            finally
            {
                DeleteOwnedTempFile(tmp, tempDir);
                TryDeleteTempDirectory(tempDir);
            }
        }

        private static void CreateShortcuts(InstallOptions opts)
        {
            string exe = Path.Combine(opts.InstallDir, AppExe);

            // Start Menu
            string startDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
            Directory.CreateDirectory(startDir);
            CreateShortcut(Path.Combine(startDir, AppName + ".lnk"), exe, opts.InstallDir);

            // Desktop (optional)
            if (opts.CreateDesktopShortcut)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                CreateShortcut(Path.Combine(desktop, AppName + ".lnk"), exe, opts.InstallDir);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkClass { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLink
        {
            void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        private static void CreateShortcut(string lnkPath, string targetPath, string workDir)
        {
            var link = (IShellLink)new ShellLinkClass();
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workDir);
            link.SetIconLocation(targetPath, 0);
            ((IPersistFile)link).Save(lnkPath, false);
        }

        private static void CreateStartupTask(string installDir, ICollection<string> warnings)
        {
            if (!InstallDirectorySecurity.IsUnderProgramFiles(installDir))
            {
                try
                {
                    InstallDirectorySecurity.ApplyRestrictiveAcl(installDir);
                }
                catch (Exception ex)
                {
                    string aclWarning = "Startup task was not created because the install directory ACL could not be secured: " + ex.Message;
                    SetupUpdateLog.Warn(aclWarning);
                    warnings.Add(aclWarning);
                    return;
                }
            }

            string exe = Path.Combine(installDir, AppExe);
            SchtasksResult result = SchtasksRunner.Run(
                $"/create /f /tn \"{STARTUP_TASK}\" /tr \"\\\"{exe}\\\" --minimized\" /sc onlogon /rl highest /delay 0000:30");
            if (result.Success)
                return;

            string detail = result.TimedOut
                ? "schtasks.exe timed out."
                : "schtasks.exe exited with code " + result.ExitCode + ".";
            if (!string.IsNullOrWhiteSpace(result.Output))
                detail += " " + result.Output;
            string warning = "Startup task was not created: " + detail;
            SetupUpdateLog.Error(warning);
            warnings.Add(warning);
        }

        private static void WriteArpEntry(string installDir, string version)
        {
            using var key = Registry.LocalMachine.CreateSubKey(ARP_KEY)!;
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "Albix4563");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("DisplayIcon", Path.Combine(installDir, AppExe) + ",0");
            key.SetValue("UninstallString", "\"" + Path.Combine(installDir, "uninstall.exe") + "\" /uninstall");
            key.SetValue("QuietUninstallString", "\"" + Path.Combine(installDir, "uninstall.exe") + "\" /uninstall /SILENT");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long size = DirSize(new DirectoryInfo(installDir)) / 1024;
            key.SetValue("EstimatedSize", (int)size, RegistryValueKind.DWord);
            key.SetValue("URLInfoAbout", "https://github.com/Albix4563/VoltManager");
        }

        private static void WriteInitialAppSettings(InstallOptions opts)
        {
            string settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName);
            Directory.CreateDirectory(settingsDir);

            string settingsPath = Path.Combine(settingsDir, "settings.json");
            string json = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
            var root = SettingsJsonUpdater.ParseRootOrEmpty(json, out bool malformed);
            if (malformed && File.Exists(settingsPath))
                BackupMalformedSettingsSafely(settingsPath);

            SettingsJsonUpdater.SetWidgetsState(root, opts.EnableWidgets, opts.EnabledWidgetTypes);
            SettingsJsonUpdater.SetUpdateChannelState(root, IsPreviewChannel(opts) ? "preview" : "stable");
            json = SettingsJsonUpdater.Serialize(root);

            string tmpPath = Path.Combine(settingsDir, "settings." + Path.GetRandomFileName() + ".tmp");
            try
            {
                File.WriteAllText(tmpPath, json);
                if (File.Exists(settingsPath))
                    File.Replace(tmpPath, settingsPath, null);
                else
                    File.Move(tmpPath, settingsPath);
            }
            finally
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best-effort: temporary cleanup must not mask the main result. */ }
            }
        }

        private static string BackupMalformedSettingsSafely(string settingsPath)
        {
            // A fixed '.setup-corrupt' pathname may already contain someone's
            // previous recovery data. Never overwrite it, and never proceed to
            // replace malformed settings unless the current bytes were backed up.
            string backup = settingsPath + ".setup-corrupt";
            if (File.Exists(backup) || Directory.Exists(backup))
                backup += "-" + Guid.NewGuid().ToString("N");
            using (var original = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var copy = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                original.CopyTo(copy);
            return backup;
        }

        private static void CopyUninstaller(string installDir, ICollection<string> warnings)
            => CopyUninstallerFromSource(installDir, warnings, Assembly.GetExecutingAssembly().Location);

        // ARP must never publish an UninstallString before its executable exists.
        // A failed copy is a reported failure with no registry side effects.
        // If ARP later fails, retain the newly copied uninstaller: deleting a
        // pathname after an external side-effect fails is not a safe rollback.
        internal static void RegisterUninstallerAndArp(
            string installDir, ICollection<string> warnings, string verifiedInstallerPath, Action writeArpEntry)
        {
            if (writeArpEntry == null) throw new ArgumentNullException(nameof(writeArpEntry));
            CopyUninstallerFromSource(installDir, warnings, verifiedInstallerPath);
            writeArpEntry();
        }

        private static void CopyUninstallerFromSource(
            string installDir, ICollection<string> warnings, string verifiedInstallerPath)
        {
            string dest = Path.Combine(installDir, "uninstall.exe");
            try
            {
                // The replacement step has already moved the previous uninstaller
                // into its backup. A remaining target may be foreign or hardlinked;
                // CREATE_NEW semantics must reject it rather than overwriting it.
                File.Copy(verifiedInstallerPath, dest, overwrite: false);
            }
            catch (Exception ex)
            {
                string failure = "Could not safely copy the uninstaller: " + ex.Message;
                SetupUpdateLog.Error(failure);
                warnings.Add(failure);
                throw new InvalidOperationException(failure, ex);
            }
        }

        private static void ScheduleDownloadedUpdateDelete()
        {
            string self = Assembly.GetExecutingAssembly().Location;
            string fullSelf = Path.GetFullPath(self);
            if (SetupStaging.IsStagedUpdateExecutable(fullSelf, SetupStaging.DefaultUpdatesRoot))
            {
                SetupStaging.ScheduleDeleteOnReboot(fullSelf, SetupUpdateLog.Warn);
                string? directory = Path.GetDirectoryName(fullSelf);
                if (!string.IsNullOrWhiteSpace(directory))
                    SetupStaging.ScheduleDeleteOnReboot(directory, SetupUpdateLog.Warn);
                return;
            }

            if (SetupStaging.IsLegacyDownloadedUpdateExecutable(fullSelf, Path.GetTempPath()))
                SetupStaging.ScheduleDeleteOnReboot(fullSelf, SetupUpdateLog.Warn);
        }

        private static long DirSize(DirectoryInfo d)
        {
            long size = 0;
            try
            {
                foreach (var f in d.GetFiles()) size += f.Length;
                foreach (var sub in d.GetDirectories()) size += DirSize(sub);
            }
            catch { /* best-effort: deferred self-cleanup must not change setup outcome. */ }
            return size;
        }

        private static string? ReadInstallLocation()
        {
            using var k = Registry.LocalMachine.OpenSubKey(ARP_KEY);
            return k?.GetValue("InstallLocation") as string;
        }

    }
}
