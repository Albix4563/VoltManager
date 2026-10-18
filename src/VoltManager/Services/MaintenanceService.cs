using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Single coordinator service for system maintenance: application inventory,
/// safe uninstaller execution, targeted registry cleaning, leftover cleanup,
/// fail-closed backups and restore.
/// </summary>
public sealed class MaintenanceService : IDisposable
{
    private const int MaxScanItems = 1000;
    private const int MaxRetainedOperations = 50;

    private static readonly Regex MsiCommandRegex = new(
        @"^(?<exe>""[^""]+""|\S+)\s+/(?:x|i|uninstall)\s*(?<product>\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\})(?:\s+/(?:quiet|qn|norestart))*\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IMaintenanceEnvironment _env;
    private readonly MaintenanceBackupManager _backupManager;
    private readonly Func<string, CancellationToken, Task<bool>> _confirmAction;
    private readonly Func<string, object[], string> _localizer;
    private readonly string _exclusionsPath;
    private readonly ConcurrentDictionary<string, OperationState> _operations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VerifiedAppSnapshot> _verifiedUninstalledApps = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly object _exclusionsSync = new();
    private MaintenanceExclusions _exclusions;

    public MaintenanceService(
        Func<string, CancellationToken, Task<bool>>? confirmAction = null,
        Func<string, object[], string>? localizer = null,
        string? backupDirectory = null,
        string? exclusionsFilePath = null)
        : this(
            new WindowsMaintenanceEnvironment(),
            confirmAction ?? ((_, _) => Task.FromResult(false)),
            localizer ?? ((key, args) => string.Format(CultureInfo.InvariantCulture, key, args)),
            backupDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoltManager", "MaintenanceBackups"),
            exclusionsFilePath ?? Path.Combine(ValidationEnvironment.ApplicationDataRoot, "VoltManager", "maintenance_exclusions.json"))
    {
    }

    internal MaintenanceService(
        IMaintenanceEnvironment env,
        Func<string, CancellationToken, Task<bool>> confirmAction,
        Func<string, object[], string> localizer,
        string backupDirectory,
        string exclusionsFilePath)
    {
        _env = env;
        _confirmAction = confirmAction;
        _localizer = localizer;
        _exclusionsPath = exclusionsFilePath;
        _backupManager = new MaintenanceBackupManager(backupDirectory, _env);
        _exclusions = LoadExclusions();
    }

    public void Dispose()
    {
        _mutationLock.Dispose();
        foreach (var op in _operations.Values)
        {
            op.Cts.Dispose();
        }
    }

    #region 1. Applications & Uninstaller

    public Task<MaintenanceAppsResult> GetAppsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var records = _env.GetUninstallRecords();
        var apps = new List<MaintenanceAppEntry>();
        var warnings = new List<string>();

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Include unnamed registrations in the inventory for overlap checks,
            // but never offer them as uninstallable application choices.
            if (string.IsNullOrWhiteSpace(record.DisplayName))
                continue;

            string appKeyId = $"uninstall:{record.Hive}:{record.View}:{record.SubKeyName}";
            if (!seenKeys.Add(appKeyId))
                continue;

            bool canUninstall = true;
            string reason = "Disinstallatore valido";

            // 1. Self protection: VoltManager
            if (record.DisplayName.Contains("VoltManager", StringComparison.OrdinalIgnoreCase) ||
                (record.InstallLocation != null && record.InstallLocation.Contains("VoltManager", StringComparison.OrdinalIgnoreCase)))
            {
                canUninstall = false;
                reason = "Protezione interna: VoltManager non può essere rimosso da se stesso";
            }
            // 2. System components and Windows updates
            else if (record.SystemComponent == 1)
            {
                canUninstall = false;
                reason = "Componente di sistema protetto";
            }
            else if (!string.IsNullOrWhiteSpace(record.ParentKeyName))
            {
                canUninstall = false;
                reason = "Aggiornamento o dipendenza subordinata";
            }
            else if (IsWindowsUpdate(record.DisplayName))
            {
                canUninstall = false;
                reason = "Aggiornamento o pacchetto di manutenzione di Windows";
            }
            // 3. Store / MSIX apps
            else if (!string.IsNullOrWhiteSpace(record.PackageFullName) ||
                     (record.InstallLocation != null && record.InstallLocation.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) ||
                     (record.UninstallString != null && record.UninstallString.Contains("Get-AppxPackage", StringComparison.OrdinalIgnoreCase)))
            {
                canUninstall = false;
                reason = "Applicazione Microsoft Store / MSIX: gestibile solo tramite Impostazioni di Windows";
            }
            // 4. Validate uninstaller command
            else if (string.IsNullOrWhiteSpace(record.UninstallString))
            {
                canUninstall = false;
                reason = "Nessun comando di disinstallazione dichiarato";
            }
            else if (!ValidateUninstallCommand(record, out string cmdReason))
            {
                canUninstall = false;
                reason = cmdReason;
            }

            apps.Add(new MaintenanceAppEntry(
                Id: appKeyId,
                Name: record.DisplayName,
                Version: record.DisplayVersion,
                Publisher: record.Publisher,
                InstallLocation: record.InstallLocation,
                EstimatedSizeKb: record.EstimatedSizeKb,
                CanUninstall: canUninstall,
                Reason: reason));

            if (apps.Count >= MaxScanItems)
            {
                warnings.Add($"Raggiunto il limite massimo di {MaxScanItems} applicazioni elencate.");
                break;
            }
        }

        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return Task.FromResult(new MaintenanceAppsResult(apps, warnings));
    }

    private static bool IsWindowsUpdate(string name)
    {
        return name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Update for", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Hotfix", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(name, @"\bKB\d{6,}\b", RegexOptions.IgnoreCase);
    }

    private bool ValidateUninstallCommand(RawUninstallRecord record, out string reason)
    {
        reason = "";
        string raw = (record.UninstallString ?? "").Trim();

        // An MSI entry must identify precisely the product registered under its key.
        // Parsing arbitrary GUIDs from an untrusted command can uninstall another product.
        if (record.WindowsInstaller == 1 || raw.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            bool validMsi = TryGetMsiProductCode(record, raw, out _);
            if (!validMsi) reason = "Comando MSI o ProductCode non coerente con la registrazione.";
            return validMsi;
        }

        // Standard executable
        string? exe = WindowsMaintenanceEnvironment.ExtractExecutablePath(raw);
        if (string.IsNullOrWhiteSpace(exe))
        {
            reason = "Percorso eseguibile non identificabile nel comando";
            return false;
        }

        string fileName = Path.GetFileName(exe);
        if (!string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Sono consentiti soltanto disinstallatori eseguibili .exe.";
            return false;
        }
        if (IsProhibitedInterpreter(fileName))
        {
            reason = $"Interprete di comandi '{fileName}' non consentito per la disinstallazione diretta";
            return false;
        }

        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(exe).Trim(); }
        catch { reason = "Percorso eseguibile non valido"; return false; }

        if (!Path.IsPathRooted(expanded) || expanded.Contains("..") || !_env.IsSafeLocalPath(expanded))
        {
            reason = "Percorso eseguibile non assoluto o non sicuro";
            return false;
        }

        if (!_env.FileExists(expanded))
        {
            reason = "Eseguibile di disinstallazione non trovato su disco";
            return false;
        }

        return true;
    }

    private static bool TryGetMsiProductCode(RawUninstallRecord record, string command, out string productCode)
    {
        productCode = "";
        if (record.WindowsInstaller != 1 ||
            !Guid.TryParseExact(record.SubKeyName, "B", out var registrationGuid))
            return false;

        var match = MsiCommandRegex.Match(command);
        if (!match.Success || !Guid.TryParseExact(match.Groups["product"].Value, "B", out var commandGuid) ||
            registrationGuid != commandGuid)
            return false;

        string executable = match.Groups["exe"].Value.Trim('"');
        if (!string.Equals(Path.GetFileName(executable), "msiexec.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        if (Path.IsPathRooted(executable))
        {
            string systemMsi = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            string wowMsi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "msiexec.exe");
            if (!string.Equals(executable, systemMsi, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(executable, wowMsi, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else if (!string.Equals(executable, "msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        productCode = commandGuid.ToString("B").ToUpperInvariant();
        return true;
    }

    private static bool IsProhibitedInterpreter(string fileName)
    {
        string[] prohibited = ["cmd.exe", "powershell.exe", "pwsh.exe", "cscript.exe", "wscript.exe", "bash.exe", "sh.exe", "rundll32.exe", "mshta.exe"];
        return prohibited.Any(p => string.Equals(p, fileName, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region 2. Operations Lifecycle (start, status, cancel, verifyUninstall)

    public async Task<MaintenanceOperation> StartOperationAsync(string kind, string? appId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string operationId = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();

        switch (kind.ToLowerInvariant())
        {
            case "uninstall":
                if (string.IsNullOrWhiteSpace(appId))
                    throw new ArgumentException("Parametro appId obbligatorio per l'operazione di disinstallazione.");
                return await StartUninstallOperationAsync(operationId, appId, cts, cancellationToken);

            case "registry":
                return StartRegistryScanOperation(operationId, cts);

            case "leftovers":
                return StartLeftoversScanOperation(operationId, appId, cts);

            default:
                throw new ArgumentException($"Tipo di operazione sconosciuto: '{kind}'.");
        }
    }

    private async Task<MaintenanceOperation> StartUninstallOperationAsync(
        string operationId,
        string appId,
        CancellationTokenSource cts,
        CancellationToken cancellationToken)
    {
        if (!_mutationLock.Wait(0))
        {
            throw new InvalidOperationException("Un'altra operazione di manutenzione o modifica è attualmente in corso.");
        }

        bool lockHeld = true;
        try
        {
            var appsResult = await GetAppsAsync(cancellationToken);
            var app = appsResult.Apps.FirstOrDefault(a => string.Equals(a.Id, appId, StringComparison.Ordinal));
            if (app == null)
            {
                throw new KeyNotFoundException($"Applicazione con ID '{appId}' non trovata.");
            }

            if (!app.CanUninstall)
            {
                throw new InvalidOperationException($"Disinstallazione non consentita: {app.Reason}");
            }

            var rawRecords = _env.GetUninstallRecords();
            var raw = rawRecords.FirstOrDefault(r => string.Equals($"uninstall:{r.Hive}:{r.View}:{r.SubKeyName}", appId, StringComparison.Ordinal));
            if (raw == null)
            {
                throw new KeyNotFoundException($"Dettagli di disinstallazione per '{appId}' non reperibili.");
            }

            ResolveUninstallerInvocation(raw, out string exePath, out string args);

            // Native confirmation dialog via localizer
            string confirmMsg = _localizer("Maintenance_ConfirmUninstall", [app.Name, app.Publisher ?? "Unknown", $"{exePath} {args}".Trim()]);
            bool confirmed = await _confirmAction(confirmMsg, cancellationToken);
            if (!confirmed)
            {
                lockHeld = false;
                _mutationLock.Release();

                var cancelledOp = new MaintenanceOperation(
                    Id: operationId,
                    Kind: "uninstall",
                    Status: "cancelled",
                    Message: "Disinstallazione annullata dall'utente.",
                    Items: [],
                    Processed: 0,
                    Total: 0,
                    AppId: appId);

                RegisterOperation(operationId, cancelledOp, cts);
                return cancelledOp;
            }

            // Re-validate selection after confirmation await
            var recheckRecords = _env.GetUninstallRecords();
            var rechecked = recheckRecords.SingleOrDefault(r => string.Equals($"uninstall:{r.Hive}:{r.View}:{r.SubKeyName}", appId, StringComparison.Ordinal));
            if (rechecked != raw || !ValidateUninstallCommand(rechecked, out _))
            {
                throw new InvalidOperationException("L'applicazione non è più presente nel registro del sistema.");
            }

            var initialOp = new MaintenanceOperation(
                Id: operationId,
                Kind: "uninstall",
                Status: "running",
                Message: $"Disinstallatore in esecuzione per '{app.Name}'...",
                Items: [],
                Processed: 0,
                Total: 1,
                AppId: appId);

            var state = RegisterOperation(operationId, initialOp, cts);

            // Snapshot details for leftovers scan once verified
            string savedAppName = app.Name;
            string? savedPublisher = app.Publisher;
            string? savedInstallLocation = app.InstallLocation;
            state.AppSnapshot = new VerifiedAppSnapshot(savedAppName, savedPublisher, savedInstallLocation);
            state.UninstallRegistration = raw;

            // Run process in background, keeping mutation lock until exit
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _env.RunUninstallerAsync(exePath, args, cts.Token);
                    state.ProcessExited = result.Completed;

                    // Reading all roots fails closed on partial inventories, while an independent
                    // key lookup detects registrations whose DisplayName has been removed.
                    _ = _env.GetUninstallRecords();
                    bool keyPresent = _env.IsUninstallRegistrationPresent(raw.Hive, raw.View, raw.SubKeyName);

                    if (result.Completed && result.ExitCode == 0)
                    {
                        if (!keyPresent)
                        {
                            _verifiedUninstalledApps[appId] = new VerifiedAppSnapshot(savedAppName, savedPublisher, savedInstallLocation);

                            UpdateOperation(state, initialOp with
                            {
                                Status = "completed",
                                Message = "Disinstallazione completata con successo.",
                                Processed = 1,
                                CanScanLeftovers = true
                            });
                        }
                        else
                        {
                            UpdateOperation(state, initialOp with
                            {
                                Status = "needsVerification",
                                Message = "Disinstallatore terminato. La registrazione è ancora presente: verifica o attendi i processi subordinati.",
                                Processed = 1,
                                CanScanLeftovers = false
                            });
                        }
                    }
                    else if (result.Completed && result.ExitCode == 3010)
                    {
                        UpdateOperation(state, initialOp with
                        {
                            Status = "rebootRequired",
                            Message = "Disinstallazione completata: riavvio del sistema richiesto.",
                            Processed = 1,
                            CanScanLeftovers = false
                        });
                    }
                    else if (!result.Completed)
                    {
                        UpdateOperation(state, initialOp with
                        {
                            Status = "needsVerification",
                            Message = result.ErrorMessage ?? "Attesa del processo annullata. La disinstallazione potrebbe essere ancora in corso.",
                            Processed = 0,
                            CanScanLeftovers = false
                        });
                    }
                    else
                    {
                        UpdateOperation(state, initialOp with
                        {
                            Status = "failed",
                            Message = $"Il disinstallatore è terminato con codice di errore {result.ExitCode}.",
                            Processed = 0,
                            CanScanLeftovers = false
                        });
                    }
                }
                catch (Exception ex)
                {
                    UpdateOperation(state, initialOp with
                    {
                        Status = "failed",
                        Message = $"Errore durante la disinstallazione: {ex.Message}",
                        CanScanLeftovers = false
                    });
                }
                finally
                {
                    _mutationLock.Release();
                }
            });

            return initialOp;
        }
        catch
        {
            if (lockHeld)
            {
                _mutationLock.Release();
            }
            throw;
        }
    }

    private static void ResolveUninstallerInvocation(RawUninstallRecord record, out string exePath, out string args)
    {
        string raw = (record.UninstallString ?? "").Trim();
        if (record.WindowsInstaller == 1 || raw.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetMsiProductCode(record, raw, out string productCode))
                throw new InvalidOperationException("MSI ProductCode or uninstaller command changed.");
            exePath = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            args = $"/x {productCode}";
            return;
        }

        string? extracted = WindowsMaintenanceEnvironment.ExtractExecutablePath(raw);
        exePath = Environment.ExpandEnvironmentVariables(extracted ?? raw).Trim();
        if (raw.StartsWith("\""))
        {
            int endQuote = raw.IndexOf('\"', 1);
            args = endQuote > 1 && endQuote < raw.Length - 1 ? raw[(endQuote + 1)..].Trim() : "";
        }
        else
        {
            int space = raw.IndexOf(' ');
            args = space > 0 ? raw[(space + 1)..].Trim() : "";
        }
    }

    public Task<MaintenanceOperation> VerifyUninstallAsync(string operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_operations.TryGetValue(operationId, out var state))
        {
            throw new KeyNotFoundException($"Operazione '{operationId}' non trovata.");
        }

        var op = state.Current;
        if (op.Status == "running" || !state.ProcessExited)
        {
            return Task.FromResult(op with
            {
                Message = "Disinstallazione ancora in corso. Attendere il completamento del processo."
            });
        }

        if (string.IsNullOrWhiteSpace(op.AppId))
        {
            return Task.FromResult(op with
            {
                Status = "failed",
                Message = "Identificativo applicazione non presente nell'operazione."
            });
        }

        try
        {
            var registration = state.UninstallRegistration
                ?? throw new InvalidOperationException("Identità di registrazione disinstallazione non disponibile.");
            _ = _env.GetUninstallRecords();
            bool stillPresent = _env.IsUninstallRegistrationPresent(
                registration.Hive, registration.View, registration.SubKeyName);

            if (!stillPresent)
            {
                // Register verified uninstallation snapshot if not already present
                if (!_verifiedUninstalledApps.ContainsKey(op.AppId))
                {
                    if (op.Kind != "uninstall" || state.AppSnapshot == null)
                        throw new InvalidOperationException("Snapshot disinstallazione non disponibile.");
                    _verifiedUninstalledApps[op.AppId] = state.AppSnapshot;
                }

                var completedOp = op with
                {
                    Status = "completed",
                    Message = "Disinstallazione verificata con successo.",
                    CanScanLeftovers = true
                };
                UpdateOperation(state, completedOp);
                return Task.FromResult(completedOp);
            }
            else
            {
                var unverifiedOp = op with
                {
                    Status = "needsVerification",
                    Message = "L'applicazione risulta ancora registrata nel sistema.",
                    CanScanLeftovers = false
                };
                UpdateOperation(state, unverifiedOp);
                return Task.FromResult(unverifiedOp);
            }
        }
        catch (Exception ex)
        {
            var failedOp = op with
            {
                Status = "needsVerification",
                Message = $"Impossibile verificare lo stato della disinstallazione: {ex.Message}",
                CanScanLeftovers = false
            };
            UpdateOperation(state, failedOp);
            return Task.FromResult(failedOp);
        }
    }

    private MaintenanceOperation StartRegistryScanOperation(string operationId, CancellationTokenSource cts)
    {
        var initialOp = new MaintenanceOperation(
            Id: operationId,
            Kind: "registry",
            Status: "running",
            Message: "Scansione del registro in corso...",
            Items: [],
            Processed: 0,
            Total: 0);

        var state = RegisterOperation(operationId, initialOp, cts);

        _ = Task.Run(() =>
        {
            try
            {
                var candidates = new List<MaintenanceOperationItem>();

                // 1. Startup Run & RunOnce values with missing executable targets
                var runValues = _env.GetStartupRunValues();
                foreach (var rv in runValues)
                {
                    if (cts.IsCancellationRequested)
                        break;

                    if (string.IsNullOrWhiteSpace(rv.ExtractedExecutablePath))
                        continue;

                    if (_env.CheckFileIsTrulyMissing(rv.ExtractedExecutablePath, out string normPath, out string reason))
                    {
                        string itemId = $"regval|{rv.Hive}|{rv.View}|{rv.SubKey}|{rv.ValueName}";
                        if (IsExcluded(itemId, normPath))
                            continue;

                        candidates.Add(new MaintenanceOperationItem(
                            Id: itemId,
                            Kind: "registry",
                            Path: $"[{rv.Hive}:{rv.View}] {rv.SubKey} -> {(string.IsNullOrEmpty(rv.ValueName) ? "(Default)" : rv.ValueName)}",
                            Reason: $"{reason}: {normPath}",
                            Selectable: true,
                            ContainsUserData: false,
                            Status: "pending") { RegistryValue = rv });

                        if (candidates.Count >= MaxScanItems)
                            break;
                    }
                }

                // 2. App Paths entries with missing target executables
                if (candidates.Count < MaxScanItems)
                {
                    var appPaths = _env.GetAppPathsRecords();
                    foreach (var ap in appPaths)
                    {
                        if (cts.IsCancellationRequested)
                            break;

                        string target = !string.IsNullOrWhiteSpace(ap.DefaultValue) ? ap.DefaultValue : (ap.PathValue ?? "");
                        if (string.IsNullOrWhiteSpace(target))
                            continue;

                        if (_env.CheckFileIsTrulyMissing(target, out string normPath, out string reason))
                        {
                            string itemId = $"regval|{ap.Hive}|{ap.View}|{ap.SubKey}|";
                            if (IsExcluded(itemId, normPath))
                                continue;

                            var registryValue = _env.ReadRegistryValue(ap.Hive, ap.View, ap.SubKey, "");
                            if (registryValue == null) continue;
                            candidates.Add(new MaintenanceOperationItem(
                                Id: itemId,
                                Kind: "registry",
                                Path: $"[{ap.Hive}:{ap.View}] {ap.SubKey} -> (Default)",
                                Reason: $"{reason}: {normPath}",
                                Selectable: true,
                                ContainsUserData: false,
                                Status: "pending") { RegistryValue = registryValue });

                            if (candidates.Count >= MaxScanItems)
                                break;
                        }
                    }
                }

                if (cts.IsCancellationRequested)
                {
                    UpdateOperation(state, initialOp with
                    {
                        Status = "cancelled",
                        Message = "Scansione annullata.",
                        Items = candidates,
                        Total = candidates.Count
                    });
                }
                else
                {
                    UpdateOperation(state, initialOp with
                    {
                        Status = "completed",
                        Message = $"Scansione completata. Trovati {candidates.Count} elementi.",
                        Items = candidates,
                        Total = candidates.Count
                    });
                }
            }
            catch (Exception ex)
            {
                UpdateOperation(state, initialOp with
                {
                    Status = "failed",
                    Message = $"Errore durante la scansione: {ex.Message}"
                });
            }
        });

        return initialOp;
    }

    private MaintenanceOperation StartLeftoversScanOperation(string operationId, string? appId, CancellationTokenSource cts)
    {
        if (string.IsNullOrWhiteSpace(appId) || !_verifiedUninstalledApps.TryGetValue(appId, out var verifiedApp))
        {
            throw new InvalidOperationException("La scansione dei residui è consentita esclusivamente per un'applicazione la cui disinstallazione è stata verificata.");
        }

        var initialOp = new MaintenanceOperation(
            Id: operationId,
            Kind: "leftovers",
            Status: "running",
            Message: "Scansione dei residui su disco in corso...",
            Items: [],
            Processed: 0,
            Total: 0,
            AppId: appId);

        var state = RegisterOperation(operationId, initialOp, cts);

        _ = Task.Run(() =>
        {
            try
            {
                var candidates = new List<MaintenanceOperationItem>();

                // Only inspect verified InstallLocation; forbid arbitrary AppData / vendor scanning
                string? installLocation = verifiedApp.InstallLocation;

                if (!string.IsNullOrWhiteSpace(installLocation) &&
                    IsExclusiveInstallRoot(installLocation) &&
                    _env.DirectoryExists(installLocation))
                {
                    var files = _env.FindLeftoverFiles(installLocation, MaxScanItems);
                    foreach (string file in files)
                    {
                        if (cts.IsCancellationRequested)
                            break;

                        if (_env.IsProtectedSystemPath(file) || !_env.IsSafeLocalPath(file) || !IsWithin(file, installLocation))
                            continue;

                        string safeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToLowerInvariant())))[..16];
                        string itemId = $"leftover|file|{safeHash}";
                        if (IsExcluded(itemId, file))
                            continue;

                        bool hasUserData = _env.ContainsUserData(file);
                        candidates.Add(new MaintenanceOperationItem(
                            Id: itemId,
                            Kind: "file",
                            Path: file,
                            Reason: hasUserData
                                ? "Possibile file dati utente o configurazione: deselezionato per sicurezza"
                                : "File residuo nella cartella di installazione",
                            Selectable: !hasUserData,
                            ContainsUserData: hasUserData,
                            Status: "pending") { InstallRoot = installLocation });

                        if (candidates.Count >= MaxScanItems)
                            break;
                    }
                }

                if (cts.IsCancellationRequested)
                {
                    UpdateOperation(state, initialOp with
                    {
                        Status = "cancelled",
                        Message = "Scansione annullata.",
                        Items = candidates,
                        Total = candidates.Count
                    });
                }
                else
                {
                    UpdateOperation(state, initialOp with
                    {
                        Status = "completed",
                        Message = $"Scansione completata. Trovati {candidates.Count} elementi.",
                        Items = candidates,
                        Total = candidates.Count
                    });
                }
            }
            catch (Exception ex)
            {
                UpdateOperation(state, initialOp with
                {
                    Status = "failed",
                    Message = $"Errore durante la scansione: {ex.Message}"
                });
            }
        });

        return initialOp;
    }

    private static bool IsWithin(string path, string root)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private bool IsExclusiveInstallRoot(string root)
    {
        if (!_env.IsSafeLocalPath(root) || _env.IsProtectedSystemPath(root)) return false;
        string full = Path.GetFullPath(root).TrimEnd('\\', '/');
        return !_env.GetUninstallRecords().Any(record => !string.IsNullOrWhiteSpace(record.InstallLocation) &&
            (string.Equals(full, Path.GetFullPath(record.InstallLocation).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
             || IsWithin(record.InstallLocation, full) || IsWithin(full, record.InstallLocation)));
    }

    public MaintenanceOperation GetOperationStatus(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("ID operazione mancante.");

        if (!_operations.TryGetValue(operationId, out var state))
        {
            throw new KeyNotFoundException($"Operazione '{operationId}' non trovata.");
        }

        return state.Current;
    }

    public MaintenanceOperation CancelOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("ID operazione mancante.");

        if (!_operations.TryGetValue(operationId, out var state))
        {
            throw new KeyNotFoundException($"Operazione '{operationId}' non trovata.");
        }

        if (state.Current.Kind == "uninstall" && !state.ProcessExited) return state.Current;

        try { state.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* A completed operation may already have released its token source. */ }

        var updated = state.Current with
        {
            Status = "cancelled",
            Message = "Operazione annullata dall'utente."
        };

        UpdateOperation(state, updated);
        return updated;
    }

    #endregion

    #region 3. Apply, Restore, Backups & Exclusions

    public async Task<MaintenanceOperation> ApplyOperationAsync(
        string operationId,
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("ID operazione mancante.");

        ArgumentNullException.ThrowIfNull(itemIds);

        if (itemIds.Count == 0)
        {
            throw new ArgumentException("Nessun elemento selezionato per l'eliminazione.");
        }

        if (!_operations.TryGetValue(operationId, out var state))
        {
            throw new KeyNotFoundException($"Operazione '{operationId}' non trovata.");
        }

        var sourceOp = state.Current;
        if (sourceOp.Status != "completed") throw new InvalidOperationException("Scan is not complete.");
        if (sourceOp.Kind != "registry" && sourceOp.Kind != "leftovers")
            throw new InvalidOperationException("Only completed maintenance scans can be applied.");
        var requestedIds = new HashSet<string>(itemIds, StringComparer.Ordinal);
        if (requestedIds.Count != itemIds.Count || requestedIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Invalid or duplicate item selections.");
        var itemsToDelete = sourceOp.Items
            .Where(i => requestedIds.Contains(i.Id) && i.Selectable && !IsExcluded(i.Id, i.Path))
            .ToList();

        if (itemsToDelete.Count != requestedIds.Count ||
            itemsToDelete.Any(i => !HasValidItemIdentity(i)))
        {
            throw new InvalidOperationException("Gli elementi selezionati non corrispondono agli elementi validati dalla scansione.");
        }

        if (!_mutationLock.Wait(0))
        {
            throw new InvalidOperationException("Un'altra operazione di manutenzione è attualmente in corso.");
        }

        try
        {
            // Confirmation dialog via localizer
            string confirmMsg = _localizer("Maintenance_ConfirmApply", [itemsToDelete.Count]);
            bool confirmed = await _confirmAction(confirmMsg, cancellationToken);
            if (!confirmed)
            {
                return sourceOp with
                {
                    Status = "cancelled",
                    Message = "Eliminazione annullata dall'utente."
                };
            }

            // Create mandatory fail-closed backup before any mutation
            string backupDesc = $"Manutenzione {sourceOp.Kind} ({itemsToDelete.Count} elementi)";
            string backupId = _backupManager.CreateBackup(sourceOp.Kind, backupDesc, itemsToDelete);

            var resultItems = new List<MaintenanceOperationItem>();
            int processedCount = 0;

            foreach (var item in itemsToDelete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsExcluded(item.Id, item.Path) || !_backupManager.MatchesBackup(backupId, item))
                {
                    resultItems.Add(item with { Status = "skipped" });
                    continue;
                }
                if (string.Equals(item.Kind, "registry", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = item.RegistryValue ?? throw new InvalidDataException("Missing registry identity.");
                    if (raw.ExtractedExecutablePath == null || !_env.CheckFileIsTrulyMissing(raw.ExtractedExecutablePath, out _, out _))
                    {
                        resultItems.Add(item with { Status = "skipped" });
                        continue;
                    }
                    bool deleted = _env.DeleteRegistryValueIfUnchanged(raw);
                    if (deleted) processedCount++;

                    resultItems.Add(item with { Status = deleted ? "cleaned" : "failed" });
                }
                else
                {
                    // File delete: re-validate immediately before delete
                    if (item.Kind != "file" || item.InstallRoot == null || !IsExclusiveInstallRoot(item.InstallRoot)
                        || !IsWithin(item.Path, item.InstallRoot) || !_env.IsSafeLocalPath(item.Path)
                        || !_env.FileExists(item.Path) || _env.IsProtectedSystemPath(item.Path) || _env.ContainsUserData(item.Path))
                    {
                        resultItems.Add(item with { Status = "skipped" });
                        continue;
                    }

                    try
                    {
                        _env.DeleteFile(item.Path);
                        processedCount++;
                        resultItems.Add(item with { Status = "cleaned" });
                    }
                    catch
                    {
                        resultItems.Add(item with { Status = "failed" });
                    }
                }
            }

            var completedOp = new MaintenanceOperation(
                Id: Guid.NewGuid().ToString("N"),
                Kind: "apply",
                Status: "completed",
                Message: $"Pulizia completata: {processedCount}/{itemsToDelete.Count} elementi rimossi.",
                Items: resultItems,
                Processed: processedCount,
                Total: itemsToDelete.Count,
                BackupId: backupId);

            return completedOp;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private static bool HasValidItemIdentity(MaintenanceOperationItem item)
    {
        if (item.ContainsUserData || !item.Selectable)
            return false;

        if (item.Kind == "registry" && item.RegistryValue is { } raw)
        {
            return string.Equals(
                item.Id,
                $"regval|{raw.Hive}|{raw.View}|{raw.SubKey}|{raw.ValueName}",
                StringComparison.Ordinal);
        }

        if (item.Kind == "file" && !string.IsNullOrWhiteSpace(item.InstallRoot))
        {
            string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(item.Path.ToLowerInvariant())))[..16];
            return string.Equals(item.Id, $"leftover|file|{hash}", StringComparison.Ordinal);
        }

        return false;
    }

    public async Task<MaintenanceOperation> RestoreBackupAsync(
        string backupId,
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_mutationLock.Wait(0))
        {
            throw new InvalidOperationException("Un'altra operazione di manutenzione è attualmente in corso.");
        }

        try
        {
            var detail = _backupManager.GetBackup(backupId);
            int countToRestore = itemIds.Count > 0 ? itemIds.Count : detail.Items.Count;

            string confirmMsg = _localizer("Maintenance_ConfirmRestore", [backupId, countToRestore]);
            bool confirmed = await _confirmAction(confirmMsg, cancellationToken);
            if (!confirmed)
            {
                return new MaintenanceOperation(
                    Id: Guid.NewGuid().ToString("N"),
                    Kind: "restore",
                    Status: "cancelled",
                    Message: "Ripristino annullato dall'utente.",
                    Items: [],
                    Processed: 0,
                    Total: countToRestore,
                    BackupId: backupId);
            }

            return _backupManager.RestoreBackup(backupId, itemIds);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<bool> DeleteBackupAsync(string backupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_mutationLock.Wait(0)) throw new InvalidOperationException("Another maintenance mutation is running.");
        try
        {
            string confirmMsg = _localizer("Maintenance_ConfirmDeleteBackup", [backupId]);
            bool confirmed = await _confirmAction(confirmMsg, cancellationToken);
            return confirmed && _backupManager.DeleteBackup(backupId);
        }
        finally { _mutationLock.Release(); }
    }

    public IReadOnlyList<MaintenanceBackupSummary> GetBackups() => _backupManager.GetBackups();
    public MaintenanceBackupDetail GetBackup(string backupId) => _backupManager.GetBackup(backupId);

    public bool OpenAppsSettings() => _env.OpenAppsSettings();

    public bool ExcludeItems(string operationId, IReadOnlyList<string> itemIds)
    {
        if (!_operations.TryGetValue(operationId, out var state))
            return false;

        var items = state.Current.Items.Where(i => itemIds.Contains(i.Id, StringComparer.Ordinal)).ToList();
        if (items.Count == 0) return false;

        lock (_exclusionsSync)
        {
            foreach (var item in items)
            {
                _exclusions.ExcludedIds.Add(item.Id);
                _exclusions.ExcludedPaths.Add(item.Path);
            }

            SaveExclusions(_exclusions);
        }

        return true;
    }

    private bool IsExcluded(string id, string path)
    {
        lock (_exclusionsSync)
        {
            return _exclusions.ExcludedIds.Contains(id) || _exclusions.ExcludedPaths.Contains(path);
        }
    }

    private MaintenanceExclusions LoadExclusions()
    {
        try
        {
            if (_env.FileExists(_exclusionsPath))
            {
                string json = _env.ReadAllText(_exclusionsPath);
                var loaded = JsonSerializer.Deserialize<MaintenanceExclusions>(json);
                if (loaded != null)
                    return loaded;
            }
        }
        catch { /* An unreadable exclusions file falls back to an empty user preference list. */ }

        return new MaintenanceExclusions(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private void SaveExclusions(MaintenanceExclusions exclusions)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_exclusionsPath);
            if (!string.IsNullOrWhiteSpace(dir) && !_env.DirectoryExists(dir))
            {
                _env.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(exclusions, new JsonSerializerOptions { WriteIndented = true });
            _env.WriteAllText(_exclusionsPath, json);
        }
        catch { /* Exclusion preferences remain available in memory if persistence is unavailable. */ }
    }

    private OperationState RegisterOperation(string id, MaintenanceOperation initial, CancellationTokenSource cts)
    {
        var state = new OperationState(initial, cts);
        _operations[id] = state;

        if (_operations.Count > MaxRetainedOperations)
        {
            var oldest = _operations.Keys.FirstOrDefault();
            if (oldest != null && !string.Equals(oldest, id, StringComparison.Ordinal))
            {
                if (_operations.TryRemove(oldest, out var oldState))
                {
                    oldState.Cts.Dispose();
                }
            }
        }

        return state;
    }

    private static void UpdateOperation(OperationState state, MaintenanceOperation updated)
    {
        state.Current = updated;
    }

    #endregion

    private sealed record VerifiedAppSnapshot(string Name, string? Publisher, string? InstallLocation);

    private sealed class OperationState
    {
        public MaintenanceOperation Current { get; set; }
        public CancellationTokenSource Cts { get; }
        public VerifiedAppSnapshot? AppSnapshot { get; set; }
        public RawUninstallRecord? UninstallRegistration { get; set; }
        public bool ProcessExited { get; set; }

        public OperationState(MaintenanceOperation current, CancellationTokenSource cts)
        {
            Current = current;
            Cts = cts;
        }
    }
}
