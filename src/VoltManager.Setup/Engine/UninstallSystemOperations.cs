using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Services;

namespace VoltManager.Setup.Engine
{
    internal interface IUninstallSystemOperations
    {
        string AppDataDirectory { get; }
        Task<bool> StopRunningInstalledProcessesAsync(string installDir, CancellationToken cancellationToken);
        bool DirectoryExists(string path);
        bool TryDeleteDirectoryTree(string path, out string error);
        void DeleteStartupTask(UninstallResult result);
        void RemoveShortcuts(UninstallResult result);
        void RemoveRegistryEntries(UninstallResult result);
        void RemoveLanRemoteControlArtifacts(UninstallResult result);
        void RestoreDefaultPowerPlan(string appData, UninstallResult result);
        UninstallCleanupResult DeleteInstallFiles(string installDir, string? trustedPayloadZipPath);
        IReadOnlyList<string> DeleteSwapLeftovers(string installDir, string? trustedPayloadZipPath);
        /// <summary>Deletes AppData except the kept top-level names; returns the names kept.</summary>
        IReadOnlyList<string> DeleteAppDataEntries(string appData, IReadOnlyCollection<string> keep, UninstallResult result);
        IReadOnlyCollection<string> KeptAppDataEntries { get; set; }
        IEnumerable<string> CleanupOwnedTempArtifacts(string currentExecutable);
        IReadOnlyList<string> FindResidualArtifacts(string installDir, string appData, string currentExecutable);
    }

    internal sealed class SystemUninstallSystemOperations : IUninstallSystemOperations
    {
        // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND); only this denotes an absent rule.
        private const int FirewallRuleNotFoundHResult = unchecked((int)0x80070002);
        private const string FirewallRuleName = "VoltManager LAN Remote Control";
        private const string CertificateSubject = "CN=VoltManager LAN Remote";
        internal const string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

        private string? _appDataDirectory;

        public string AppDataDirectory => _appDataDirectory ??= InteractiveUserProfile.ResolveAppDataDirectory(
            VoltManagerArtifacts.AppDataDirectory, out _interactiveUserDiffers);

        private bool _interactiveUserDiffers;

        public IReadOnlyCollection<string> KeptAppDataEntries { get; set; } = Array.Empty<string>();

        public UninstallCleanupResult DeleteInstallFiles(string installDir, string? trustedPayloadZipPath)
            => InstallManifest.DeleteOwnedEntriesTolerant(installDir, trustedPayloadZipPath);

        public IReadOnlyList<string> DeleteSwapLeftovers(string installDir, string? trustedPayloadZipPath)
            => InstallManifest.DeleteVerifiedSwapLeftovers(installDir, trustedPayloadZipPath);

        public IReadOnlyList<string> DeleteAppDataEntries(
            string appData, IReadOnlyCollection<string> keep, UninstallResult result)
            => UninstallAppData.DeleteEntries(appData, keep, result);

        public void RestoreDefaultPowerPlan(string appData, UninstallResult result)
        {
            try
            {
                string? active = PowerPlanRestore.ParseActiveGuid(RunPowercfg("/getactivescheme"));
                if (!PowerPlanRestore.ShouldRestore(active, PowerPlanRestore.ReadManagedPlanGuids(appData)))
                    return;
                RunPowercfg("/setactive " + BalancedPlanGuid);
                if (!string.Equals(PowerPlanRestore.ParseActiveGuid(RunPowercfg("/getactivescheme")),
                        BalancedPlanGuid, StringComparison.OrdinalIgnoreCase))
                    result.AddWarning("Could not re-activate the Windows Balanced power plan.");
            }
            catch (Exception ex)
            {
                result.AddWarning("Power plan restore: " + ex.Message);
            }
        }

        private static string RunPowercfg(string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using (var process = System.Diagnostics.Process.Start(psi))
            {
                if (process == null) return "";
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch { /* best-effort: timed-out powercfg may already have exited. */ }
                    return "";
                }
                return output.Wait(1000) ? output.Result : "";
            }
        }

        public Task<bool> StopRunningInstalledProcessesAsync(string installDir, CancellationToken cancellationToken)
            => HardenedInstallEngine.StopRunningInstalledProcessesAsync(installDir, cancellationToken);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool TryDeleteDirectoryTree(string path, out string error)
            => InstallEngine.TryDeleteDirectoryTree(path, out error);

        public void DeleteStartupTask(UninstallResult result)
            => HardenedInstallEngine.DeleteStartupTask(result);

        public void RemoveShortcuts(UninstallResult result)
            => HardenedInstallEngine.RemoveShortcuts(result);

        public void RemoveRegistryEntries(UninstallResult result)
            => HardenedInstallEngine.RemoveRegistryEntries(result);

        public void RemoveLanRemoteControlArtifacts(UninstallResult result)
        {
            // Resolve before touching a CurrentUser certificate store: an over-the-shoulder
            // elevation must not delete the administrator's own VoltManager credentials.
            _ = AppDataDirectory;
            RemoveLanRemoteFirewallRule(result);
            if (!_interactiveUserDiffers) RemoveLanRemoteCertificate(result);
            if (_interactiveUserDiffers)
                result.AddWarning("The LAN remote certificate of the signed-in user could not be removed " +
                                  "because setup runs as a different administrator account.");
        }

        public IEnumerable<string> CleanupOwnedTempArtifacts(string currentExecutable)
            => VoltManagerArtifacts.CleanupOwnedTempArtifacts(Path.GetTempPath(), currentExecutable);

        public IReadOnlyList<string> FindResidualArtifacts(string installDir, string appData, string currentExecutable)
            => HardenedInstallEngine.FindResidualArtifacts(installDir, appData, currentExecutable,
                KeptAppDataEntries, checkCurrentUserCertificate: !_interactiveUserDiffers);

        internal static bool LanRemoteFirewallRuleExists()
        {
            object? policy = null;
            object? rules = null;
            object? rule = null;
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false);
                if (policyType == null) return false;
                policy = Activator.CreateInstance(policyType);
                if (policy == null) return false;
                rules = policyType.InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null);
                if (rules == null) return false;
                rule = rules.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, rules,
                    new object[] { FirewallRuleName });
                return rule != null;
            }
            catch
            {
                // Not found (0x80070002) and unknown errors alike: only a confirmed rule is a residual.
                return false;
            }
            finally
            {
                ReleaseCom(rule);
                ReleaseCom(rules);
                ReleaseCom(policy);
            }
        }

        internal static bool LanRemoteCertificateExists()
        {
            try
            {
                using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadOnly);
                    foreach (X509Certificate2 certificate in store.Certificates)
                    {
                        using (certificate)
                            if (string.Equals(certificate.Subject, CertificateSubject, StringComparison.OrdinalIgnoreCase))
                                return true;
                    }
                }
            }
            catch { /* best-effort: residual probing must not fail the uninstall. */ }
            return false;
        }

        private static void RemoveLanRemoteFirewallRule(UninstallResult result)
        {
            object? policy = null;
            object? rules = null;
            try
            {
                Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false);
                if (policyType == null) return;
                policy = Activator.CreateInstance(policyType);
                if (policy == null) return;
                rules = policyType.InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null);
                if (rules == null) return;
                TryRemoveFirewallRule(result, () =>
                {
                    rules.GetType().InvokeMember(
                        "Remove",
                        BindingFlags.InvokeMethod,
                        null,
                        rules,
                        new object[] { FirewallRuleName });
                });
            }
            catch (Exception ex)
            {
                result.Add("LAN remote firewall cleanup: " + ex.Message);
            }
            finally
            {
                ReleaseCom(rules);
                ReleaseCom(policy);
            }
        }

        internal static void TryRemoveFirewallRule(UninstallResult result, Action removeRule)
        {
            try { removeRule(); }
            catch (TargetInvocationException ex) when (ex.InnerException is COMException com && com.ErrorCode == FirewallRuleNotFoundHResult)
            {
                // Removing an already absent rule is idempotent.
            }
            catch (COMException ex) when (ex.ErrorCode == FirewallRuleNotFoundHResult)
            {
                // Removing an already absent rule is idempotent.
            }
            catch (Exception ex)
            {
                Exception detail = ex is TargetInvocationException { InnerException: COMException com }
                    ? com
                    : ex;
                result.Add("LAN remote firewall cleanup: " + detail.Message);
            }
        }

        private static void RemoveLanRemoteCertificate(UninstallResult result)
        {
            try
            {
                using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
                {
                    store.Open(OpenFlags.ReadWrite);
                    var matches = new List<X509Certificate2>();
                    foreach (X509Certificate2 certificate in store.Certificates)
                    {
                        if (string.Equals(certificate.Subject, CertificateSubject, StringComparison.OrdinalIgnoreCase))
                            matches.Add(certificate);
                    }

                    foreach (X509Certificate2 certificate in matches)
                    {
                        try
                        {
                            // The app persists the key (PersistKeySet); removing only the
                            // certificate would leave the key file under %APPDATA%\Microsoft\Crypto.
                            TryDeletePrivateKey(certificate, result);
                            store.Remove(certificate);
                        }
                        finally { certificate.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Add("LAN remote certificate cleanup: " + ex.Message);
            }
        }

        internal static void TryDeletePrivateKey(X509Certificate2 certificate, UninstallResult result)
        {
            if (!certificate.HasPrivateKey) return;
            try
            {
                using (RSA? rsa = certificate.GetRSAPrivateKey())
                {
                    switch (rsa)
                    {
                        case RSACng cng:
                            cng.Key.Delete();
                            break;
                        case RSACryptoServiceProvider csp:
                            csp.PersistKeyInCsp = false;
                            csp.Clear();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                result.AddWarning("LAN remote certificate private key: " + ex.Message);
            }
        }

        private static void ReleaseCom(object? value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.FinalReleaseComObject(value); } catch { /* best-effort: COM cleanup must not mask uninstall results. */ }
        }
    }
}
