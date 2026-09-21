using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VoltManager.Setup.Engine
{
    internal interface IInstallUpdateEngine
    {
        Task UpdateAsync(int waitPid, string version, CancellationToken ct = default);
    }

    internal interface IInstallProcessOperations
    {
        bool StopForInstall(string installDir);
        bool StopInstalled(string installDir);
        Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);
        void Start(string fileName, string arguments);
    }

    internal sealed class SystemInstallProcessOperations : IInstallProcessOperations
    {
        public bool StopForInstall(string installDir) => InstallEngine.StopProcessesForInstall(installDir);
        public bool StopInstalled(string installDir) => InstallEngine.StopRunningInstalledProcesses(installDir);

        public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (processId <= 0)
                return true;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return await Task.Run(
                        () => process.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds)),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        public void Start(string fileName, string arguments)
            => Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
    }
}
