using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
        IEnumerable<string> CleanupOwnedTempArtifacts(string currentExecutable);
        IReadOnlyList<string> FindResidualArtifacts(string installDir, string appData, string currentExecutable);
    }

    internal sealed class SystemUninstallSystemOperations : IUninstallSystemOperations
    {
        public string AppDataDirectory => VoltManagerArtifacts.AppDataDirectory;

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
            RemoveLanRemoteFirewallRule(result);
            RemoveLanRemoteCertificate(result);
        }

        public IEnumerable<string> CleanupOwnedTempArtifacts(string currentExecutable)
            => VoltManagerArtifacts.CleanupOwnedTempArtifacts(Path.GetTempPath(), currentExecutable);

        public IReadOnlyList<string> FindResidualArtifacts(string installDir, string appData, string currentExecutable)
            => HardenedInstallEngine.FindResidualArtifacts(installDir, appData, currentExecutable);

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
                try
                {
                    rules.GetType().InvokeMember(
                        "Remove",
                        BindingFlags.InvokeMethod,
                        null,
                        rules,
                        new object[] { "VoltManager LAN Remote Control" });
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException)
                {
                    // Removing an already absent rule is idempotent.
                }
                catch (COMException)
                {
                    // Removing an already absent rule is idempotent.
                }
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
                        if (string.Equals(certificate.Subject, "CN=VoltManager LAN Remote", StringComparison.OrdinalIgnoreCase))
                            matches.Add(certificate);
                    }

                    foreach (X509Certificate2 certificate in matches)
                    {
                        try { store.Remove(certificate); }
                        finally { certificate.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Add("LAN remote certificate cleanup: " + ex.Message);
            }
        }

        private static void ReleaseCom(object? value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }
}
