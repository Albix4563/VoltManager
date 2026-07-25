using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        public bool Success => Failures.Count == 0;
        public string Summary => string.Join("; ", Failures);

        public void Add(string failure)
        {
            if (!string.IsNullOrWhiteSpace(failure))
                Failures.Add(failure);
        }
    }

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
            if (!StopProcessesForInstall(opts.InstallDir))
                throw new InvalidOperationException("VoltManager processes are still running.");
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
            WriteInitialAppSettings(opts);

            Report("", 100);
        }

        public async Task UpdateAsync(int waitPid, string version, CancellationToken ct = default)
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

            // The app can delegate to the external supervisor before the updater starts.
            // Stop both processes before clearing files so the supervisor executable cannot
            // remain locked and leave the installation only partially replaced.
            if (!StopRunningInstalledProcesses(installDir!))
                throw new InvalidOperationException("VoltManager processes are still running.");

            Report(I18n.T("status_extract"), 0);
            await ExtractPayloadAsync(installDir!, ct);

            Report(I18n.T("status_registry"), 90);
            WriteArpEntry(installDir!, version);
            CopyUninstaller(installDir!);

            Report("", 100);
            ScheduleDownloadedUpdateDelete();

            // Relaunch the app with --updated flag.
            string exe = Path.Combine(installDir!, AppExe);
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
        }

        public async Task<UninstallResult> UninstallAsync(string? targetDir = null, CancellationToken ct = default)
        {
            var result = new UninstallResult();
            string dir = ResolveInstallDir(targetDir);

            Report(I18n.T("status_uninst_kill"), 5);
            if (!StopRunningInstalledProcesses(dir))
                result.Add("VoltManager process still running");
            await Task.Delay(400, ct);

            Report(I18n.T("status_uninst_files"), 20);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                if (IsRunningFromDirectory(dir))
                {
                    result.Add("Uninstaller is still running from the install directory");
                }
                else if (!TryDeleteDirectoryTree(dir, out string filesErr))
                {
                    result.Add("Install directory: " + filesErr);
                }
            }

            Report(I18n.T("status_uninst_files"), 45);
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            if (Directory.Exists(appData) && !TryDeleteDirectoryTree(appData, out string appDataErr))
                result.Add("AppData: " + appDataErr);

            Report(I18n.T("status_startup"), 60);
            try { RunSchtasks($"/delete /f /tn \"{STARTUP_TASK}\""); }
            catch (Exception ex) { result.Add("Startup task delete: " + ex.Message); }
            if (StartupTaskExists())
                result.Add("Startup task still present: " + STARTUP_TASK);

            Report(I18n.T("status_uninst_files"), 75);
            try { RemoveShortcuts(); }
            catch (Exception ex) { result.Add("Shortcuts: " + ex.Message); }
            if (ShortcutsRemain(out string shortcutLeft))
                result.Add("Shortcuts still present: " + shortcutLeft);

            Report(I18n.T("status_uninst_reg"), 90);
            try { Registry.LocalMachine.DeleteSubKey(ARP_KEY, false); }
            catch (Exception ex) { result.Add("ARP delete: " + ex.Message); }
            try { Registry.LocalMachine.DeleteSubKey(INNO_ARP_KEY, false); } catch { /* legacy best-effort */ }
            if (ArpKeyExists())
                result.Add("ARP entry still present");

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                result.Add("Install directory still exists: " + dir);
            if (Directory.Exists(appData))
                result.Add("AppData still exists: " + appData);

            Report("", 100);
            return result;
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
        /// If this process is running from the install dir, copy self to %TEMP% and relaunch.
        /// Returns true when the caller should exit (handoff done).
        /// For silent mode waits for the child and sets <paramref name="exitCode"/>.
        /// For UI mode starts the child and returns immediately with exitCode 0.
        /// </summary>
        public static bool TryRelaunchFromTempIfNeeded(SetupArgs args, out int exitCode)
        {
            exitCode = 0;
            if (args.FromTemp) return false;

            string installDir = ResolveInstallDir(args.TargetDir);
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                return false;

            string self = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(self) || !File.Exists(self))
                return false;

            if (!IsPathUnder(self, installDir))
                return false;

            string tempExe = Path.Combine(Path.GetTempPath(), "VoltManagerUninstall.exe");
            File.Copy(self, tempExe, true);

            var parts = new List<string> { "/uninstall", "--from-temp", "--target", installDir };
            if (args.SilentUninstall) parts.Add("/SILENT");
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

            var child = Process.Start(psi);
            if (child == null)
            {
                exitCode = 1;
                return true;
            }

            if (args.SilentUninstall)
            {
                child.WaitForExit();
                exitCode = child.ExitCode;
            }
            else
            {
                exitCode = 0;
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

        // ── Private helpers ──────────────────────────────────────────────────

        private void Report(string msg, double pct) => Progress?.Invoke(msg, pct);

        private static bool StopProcessesForInstall(string installDir)
        {
            string existingInstallDir = ResolveInstallDir();
            if (Directory.Exists(existingInstallDir) &&
                !string.Equals(
                    Path.GetFullPath(existingInstallDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) &&
                !StopRunningInstalledProcesses(existingInstallDir))
            {
                return false;
            }

            return StopRunningInstalledProcesses(installDir);
        }

        private static bool StopRunningInstalledProcesses(string installDir)
        {
            foreach (string processName in new[] { "VoltManager.Supervisor", "VoltManager" })
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
                   !AnyProcessRunningFromDirectory("VoltManager.Supervisor", installDir);
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

        private static bool IsRunningFromDirectory(string dir)
        {
            string self = Assembly.GetExecutingAssembly().Location;
            return !string.IsNullOrEmpty(self) && IsPathUnder(self, dir);
        }

        private static bool IsPathUnder(string path, string directory)
        {
            string fullPath = Path.GetFullPath(path);
            string fullDir = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartupTaskExists()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks", $"/query /tn \"{STARTUP_TASK}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                })!;
                p.WaitForExit(10_000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShortcutsRemain(out string remaining)
        {
            var left = new List<string>();
            string startDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
            if (Directory.Exists(startDir)) left.Add(startDir);

            string desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                AppName + ".lnk");
            if (File.Exists(desktop)) left.Add(desktop);

            remaining = string.Join(", ", left);
            return left.Count > 0;
        }

        private static bool ArpKeyExists()
        {
            using var k = Registry.LocalMachine.OpenSubKey(ARP_KEY);
            return k != null;
        }

        private static string QuoteArg(string value)
        {
            if (value.Length == 0) return "\"\"";
            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
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

            ClearInstallDirectory(destDir);

            ZipFile.ExtractToDirectory(tempZip, destDir);
            try { File.Delete(tempZip); } catch { }
        }

        internal static void ClearInstallDirectory(string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string path in Directory.GetFileSystemEntries(destDir))
            {
                if (File.Exists(path))
                {
                    MakeWritable(path);
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    MakeWritableTree(path);
                    Directory.Delete(path, true);
                }
            }
        }

        private static void MakeWritableTree(string dir)
        {
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                MakeWritable(file);
            foreach (string subdir in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                MakeWritable(subdir);
            MakeWritable(dir);
        }

        private static void MakeWritable(string path)
        {
            FileAttributes attrs = File.GetAttributes(path);
            attrs &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(path, attrs);
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

        private static void WriteInitialAppSettings(InstallOptions opts)
        {
            string settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppName);
            Directory.CreateDirectory(settingsDir);

            string settingsPath = Path.Combine(settingsDir, "settings.json");
            string json = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
            if (!LooksLikeJsonObject(json))
            {
                string backupPath = settingsPath + ".setup-corrupt";
                try { File.Copy(settingsPath, backupPath, overwrite: true); } catch { }
                json = "{}";
            }

            json = SetWidgetsState(json, opts.EnableWidgets, opts.EnabledWidgetTypes);

            string tmpPath = settingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Copy(tmpPath, settingsPath, overwrite: true);
            try { File.Delete(tmpPath); } catch { }
        }

        private static bool LooksLikeJsonObject(string json)
        {
            string trimmed = json.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[trimmed.Length - 1] == '}';
        }

        private static string SetWidgetsState(string json, bool masterEnabled, HashSet<string> enabledTypes)
        {
            var all = new[] { "clock", "calendar", "usage", "temps", "power", "plans" };
            // Only explicitly selected types start enabled. Empty selection ⇒ master off.
            var selected = enabledTypes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool any = false;
            foreach (var t in all) if (selected.Contains(t)) { any = true; break; }
            bool master = masterEnabled && any;
            var items = string.Join(",", Array.ConvertAll(all, t =>
                "{\"type\":\"" + t + "\",\"enabled\":" + (selected.Contains(t) ? "true" : "false") + ",\"pinned\":false}"));
            string widgetsVal = "{\"enabled\":" + (master ? "true" : "false") + ",\"items\":[" + items + "]}";

            int propStart = FindJsonProperty(json, "widgets");
            if (propStart < 0)
                return InsertTopLevelProperty(json, "\"widgets\": " + widgetsVal);

            int valueStart = FindJsonValueStart(json, propStart);
            if (valueStart < 0) return InsertTopLevelProperty("{}", "\"widgets\": " + widgetsVal);
            int valueEnd = json[valueStart] == '{'
                ? FindMatching(json, valueStart, '{', '}')
                : FindJsonValueEnd(json, valueStart);
            if (valueEnd < valueStart) return InsertTopLevelProperty("{}", "\"widgets\": " + widgetsVal);
            return json.Substring(0, valueStart) + widgetsVal + json.Substring(valueEnd + 1);
        }

        private static string SetObjectBooleanProperty(string objectJson, string propertyName, bool enabled)
        {
            string value = enabled ? "true" : "false";
            int propertyStart = FindJsonProperty(objectJson, propertyName);
            if (propertyStart >= 0)
            {
                int valueStart = FindJsonValueStart(objectJson, propertyStart);
                int valueEnd = FindJsonValueEnd(objectJson, valueStart);
                if (valueStart >= 0 && valueEnd >= valueStart)
                    return objectJson.Substring(0, valueStart) + value + objectJson.Substring(valueEnd + 1);
            }

            string inner = objectJson.Substring(1, objectJson.Length - 2).Trim();
            return inner.Length == 0
                ? "{ \"" + propertyName + "\": " + value + " }"
                : "{ \"" + propertyName + "\": " + value + ", " + inner + " }";
        }

        private static int FindJsonProperty(string json, string propertyName)
        {
            var match = Regex.Match(json, "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:", RegexOptions.CultureInvariant);
            return match.Success ? match.Index : -1;
        }

        private static int FindJsonValueStart(string json, int propertyStart)
        {
            int colon = json.IndexOf(':', propertyStart);
            if (colon < 0) return -1;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            return i < json.Length ? i : -1;
        }

        private static int FindJsonValueEnd(string json, int valueStart)
        {
            if (valueStart < 0 || valueStart >= json.Length) return -1;
            char first = json[valueStart];
            if (first == '{') return FindMatching(json, valueStart, '{', '}');
            if (first == '[') return FindMatching(json, valueStart, '[', ']');
            if (first == '"') return FindStringEnd(json, valueStart);

            int i = valueStart;
            while (i < json.Length && json[i] != ',' && json[i] != '}') i++;
            return i - 1;
        }

        private static int FindMatching(string json, int start, char open, char close)
        {
            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') inString = true;
                else if (c == open) depth++;
                else if (c == close && --depth == 0) return i;
            }

            return -1;
        }

        private static int FindStringEnd(string json, int start)
        {
            bool escaped = false;
            for (int i = start + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') return i;
            }
            return -1;
        }

        private static string InsertTopLevelProperty(string json, string propertyJson)
        {
            int end = json.LastIndexOf('}');
            if (end < 0) return "{\n  " + propertyJson + "\n}";

            string prefix = json.Substring(0, end).TrimEnd();
            int firstBrace = prefix.IndexOf('{');
            bool hasProperties = firstBrace >= 0 && prefix.Substring(firstBrace + 1).Trim().Length > 0;
            string separator = hasProperties ? ",\n  " : "\n  ";
            return prefix + separator + propertyJson + "\n" + json.Substring(end);
        }

        private static void CopyUninstaller(string installDir)
        {
            string self = Assembly.GetExecutingAssembly().Location;
            string dest = Path.Combine(installDir, "uninstall.exe");
            try { File.Copy(self, dest, true); } catch { }
        }

        private static void ScheduleDownloadedUpdateDelete()
        {
            string self = Assembly.GetExecutingAssembly().Location;
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullSelf = Path.GetFullPath(self);

            if (!fullSelf.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            if (!string.Equals(Path.GetFileName(fullSelf), "VoltManagerUpdate.exe", StringComparison.OrdinalIgnoreCase))
                return;

            string bat = Path.Combine(Path.GetTempPath(), "vmgr_update_cleanup.bat");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "for /l %%i in (1,1,30) do (\r\n" +
                "  del /f /q \"" + fullSelf + "\" 2>nul && goto done\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                ")\r\n" +
                ":done\r\n" +
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

    }
}
