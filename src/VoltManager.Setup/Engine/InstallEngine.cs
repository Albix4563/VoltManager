using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VoltManager.Setup.Engine
{
    public class InstallEngine
    {
        private const string AppName        = "VoltManager";
        private const string AppExe         = "VoltManager.exe";
        private const string ARP_KEY        = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VoltManager";
        private const string INNO_ARP_KEY   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B7E64C0A-52D1-4E2B-9C0F-VOLTMGR00001}_is1";
        private const string STARTUP_TASK   = "VoltManagerAutostart";
        private const string WEBVIEW2_CLIENT = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        public event Action<string, double>? Progress; // (statusText, 0-100)

        public async Task InstallAsync(InstallOptions opts, string version, CancellationToken ct = default)
        {
            Report(I18n.T("status_kill"), 0);
            KillRunningApp();
            ct.ThrowIfCancellationRequested();

            Report(I18n.T("status_migrate"), 5);
            await RemoveLegacyInnoInstallAsync(ct);
            ct.ThrowIfCancellationRequested();

            Report(I18n.T("status_extract"), 15);
            await ExtractPayloadAsync(opts.InstallDir, ct);
            ct.ThrowIfCancellationRequested();

            if (WebView2Missing())
            {
                Report(I18n.T("status_webview"), 65);
                await InstallWebView2Async(ct);
                ct.ThrowIfCancellationRequested();
            }

            Report(I18n.T("status_shortcuts"), 75);
            CreateShortcuts(opts);

            if (opts.StartWithWindows)
            {
                Report(I18n.T("status_startup"), 82);
                SetStartup(opts.InstallDir, true);
            }

            Report(I18n.T("status_registry"), 88);
            WriteArpEntry(opts.InstallDir, version);
            CopyUninstaller(opts.InstallDir);

            Report("", 100);
        }

        public async Task UpdateAsync(int waitPid, CancellationToken ct = default)
        {
            // Wait for main app to exit.
            if (waitPid > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(waitPid);
                    await Task.Run(() => proc.WaitForExit(30_000), ct);
                }
                catch { /* process already exited */ }
            }

            string? installDir = ReadInstallLocation();
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                throw new InvalidOperationException("VoltManager install directory not found in registry.");

            string version = ReadInstalledVersion() ?? "1.0.0";

            Report(I18n.T("status_extract"), 0);
            await ExtractPayloadAsync(installDir!, ct);

            Report(I18n.T("status_registry"), 90);
            WriteArpEntry(installDir!, version);
            CopyUninstaller(installDir!);

            Report("", 100);

            // Relaunch the app with --updated flag.
            string exe = Path.Combine(installDir!, AppExe);
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
        }

        public async Task UninstallAsync(string? targetDir = null, CancellationToken ct = default)
        {
            string dir = targetDir ?? ReadInstallLocation() ?? "";

            Report(I18n.T("status_uninst_kill"), 5);
            KillRunningApp();
            await Task.Delay(800, ct);

            Report(I18n.T("status_uninst_files"), 20);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                // Schedule self-deletion via cmd after we exit (uninstall.exe is inside dir).
                ScheduleSelfDelete(dir);
            }

            // Remove WebView2 user-data cache.
            try
            {
                string wv2Cache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VoltManager", "WebView2");
                if (Directory.Exists(wv2Cache)) Directory.Delete(wv2Cache, true);
            }
            catch { }

            Report(I18n.T("status_startup"), 60);
            try { RunSchtasks($"/delete /f /tn \"{STARTUP_TASK}\""); } catch { }

            // Remove shortcuts.
            try
            {
                RemoveShortcuts();
            }
            catch { }

            Report(I18n.T("status_uninst_reg"), 80);
            try { Registry.LocalMachine.DeleteSubKey(ARP_KEY, false); } catch { }

            Report("", 100);
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private void Report(string msg, double pct) => Progress?.Invoke(msg, pct);

        private static void KillRunningApp()
        {
            foreach (var p in Process.GetProcessesByName("VoltManager"))
            {
                try
                {
                    if (!p.CloseMainWindow()) p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
            }
        }

        private static async Task RemoveLegacyInnoInstallAsync(CancellationToken ct)
        {
            using var key = Registry.LocalMachine.OpenSubKey(INNO_ARP_KEY);
            if (key == null) return;

            string? loc = key.GetValue("InstallLocation") as string;
            if (string.IsNullOrEmpty(loc)) return;

            string unins = Path.Combine(loc, "unins000.exe");
            if (!File.Exists(unins)) return;

            var psi = new ProcessStartInfo(unins, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            { UseShellExecute = true };
            var p = Process.Start(psi)!;
            await Task.Run(() => p.WaitForExit(60_000), ct);
        }

        private static async Task ExtractPayloadAsync(string destDir, CancellationToken ct)
        {
            Directory.CreateDirectory(destDir);

            // Extract payload.zip from embedded resources.
            var asm = Assembly.GetExecutingAssembly();
            string? resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

            if (resName == null) return; // dev build without payload

            string tempZip = Path.Combine(Path.GetTempPath(), "VoltManagerPayload.zip");
            using (var src = asm.GetManifestResourceStream(resName)!)
            using (var fs = File.Create(tempZip))
                await src.CopyToAsync(fs, 81920, ct);

            // Remove old files before extracting (net48 ZipFile has no overwrite option).
            if (Directory.Exists(destDir))
            {
                foreach (var f in Directory.GetFiles(destDir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
            }

            ZipFile.ExtractToDirectory(tempZip, destDir);
            try { File.Delete(tempZip); } catch { }
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
            if (resName == null) return;

            string tmp = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (var src = asm.GetManifestResourceStream(resName)!)
            using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, 81920, ct);

            var p = Process.Start(new ProcessStartInfo(tmp, "/silent /install")
            { UseShellExecute = true })!;
            await Task.Run(() => p.WaitForExit(300_000), ct);
            try { File.Delete(tmp); } catch { }
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

        private static void RemoveShortcuts()
        {
            string startDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
            if (Directory.Exists(startDir)) Directory.Delete(startDir, true);

            string desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                AppName + ".lnk");
            if (File.Exists(desktop)) File.Delete(desktop);
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

        private static void SetStartup(string installDir, bool enable)
        {
            string exe = Path.Combine(installDir, AppExe);
            if (enable)
                RunSchtasks($"/create /f /tn \"{STARTUP_TASK}\" /tr \"\\\"{exe}\\\" --minimized\" /sc onlogon /rl highest /delay 0000:30");
            else
                RunSchtasks($"/delete /f /tn \"{STARTUP_TASK}\"");
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
            key.SetValue("URLInfoAbout", "https://github.com/Albix4563/power_efficency");
        }

        private static void CopyUninstaller(string installDir)
        {
            string self = Assembly.GetExecutingAssembly().Location;
            string dest = Path.Combine(installDir, "uninstall.exe");
            try { File.Copy(self, dest, true); } catch { }
        }

        private static void ScheduleSelfDelete(string dir)
        {
            // Use cmd /c timeout + rmdir to delete the install dir after process exits.
            string bat = Path.Combine(Path.GetTempPath(), "vmgr_uninst.bat");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "rmdir /s /q \"" + dir + "\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd", "/c \"" + bat + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }

        private static long DirSize(DirectoryInfo d)
        {
            long size = 0;
            try
            {
                foreach (var f in d.GetFiles()) size += f.Length;
                foreach (var sub in d.GetDirectories()) size += DirSize(sub);
            }
            catch { }
            return size;
        }

        private static void RunSchtasks(string args)
        {
            var p = Process.Start(new ProcessStartInfo("schtasks", args)
            { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(10000);
        }

        private static string? ReadInstallLocation()
        {
            using var k = Registry.LocalMachine.OpenSubKey(ARP_KEY);
            return k?.GetValue("InstallLocation") as string;
        }

        private static string? ReadInstalledVersion()
        {
            using var k = Registry.LocalMachine.OpenSubKey(ARP_KEY);
            return k?.GetValue("DisplayVersion") as string;
        }
    }
}
