using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Builds a plain-text diagnostics bundle (system, app state, recent logs)
/// for support/export. Never throws to the caller: failures degrade to notes
/// inside the report so the UI can still save something useful.
/// </summary>
public sealed class DiagnosticsReportService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public const int DefaultLogTailLines = 400;

    /// <summary>%APPDATA%\VoltManager\logs — created if missing.</summary>
    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoltManager", "logs");
            try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
            return dir ?? "";
        }
    }

    public static string? LogFilePath => Logger.LogFilePath;

    /// <summary>
    /// Pure-ish assembly of report sections. Dependencies injected as funcs so
    /// HostBridge can pass live services without tight coupling.
    /// </summary>
    public static string BuildReport(DiagnosticsSnapshot snap, int logTailLines = DefaultLogTailLines)
    {
        var sb = new StringBuilder(16 * 1024);
        sb.AppendLine("VoltManager diagnostics report");
        sb.AppendLine("Generated (local): ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        sb.AppendLine("Generated (UTC):   ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'"));
        sb.AppendLine(new string('=', 60));

        Section(sb, "Application");
        sb.AppendLine("Version:     " + (snap.AppVersion ?? "?"));
        sb.AppendLine("ProcessId:   " + Environment.ProcessId);
        sb.AppendLine("CLR:         " + Environment.Version);
        sb.AppendLine("OS:          " + Environment.OSVersion);
        sb.AppendLine("64-bit OS:   " + Environment.Is64BitOperatingSystem);
        sb.AppendLine("64-bit proc: " + Environment.Is64BitProcess);
        sb.AppendLine("Culture:     " + System.Globalization.CultureInfo.CurrentUICulture.Name);
        sb.AppendLine("BaseDir:     " + AppContext.BaseDirectory);
        sb.AppendLine("Log file:    " + (snap.LogPath ?? "(none)"));

        Section(sb, "System");
        if (snap.SystemInfo != null)
        {
            try { sb.AppendLine(JsonSerializer.Serialize(snap.SystemInfo, JsonOpts)); }
            catch { sb.AppendLine("(system info serialize failed)"); }
        }
        else sb.AppendLine("(unavailable)");

        Section(sb, "Live metrics");
        if (snap.Metrics != null)
        {
            try { sb.AppendLine(JsonSerializer.Serialize(snap.Metrics, JsonOpts)); }
            catch { sb.AppendLine("(metrics serialize failed)"); }
        }
        else sb.AppendLine("(unavailable)");

        Section(sb, "Active power plan");
        sb.AppendLine(snap.ActivePlanSummary ?? "(unknown)");

        Section(sb, "Feature state");
        AppendJson(sb, "keepAwake", snap.KeepAwake);
        AppendJson(sb, "powerSource", snap.PowerSource);
        AppendJson(sb, "thermalGuard", snap.ThermalGuard);
        AppendJson(sb, "idlePowerGuard", snap.IdlePowerGuard);
        AppendJson(sb, "cpuAutomation", snap.CpuAutomation);
        AppendJson(sb, "batteryPower", snap.BatteryPower);
        AppendJson(sb, "batteryHealth", snap.BatteryHealth);
        AppendJson(sb, "memory", snap.Memory);

        Section(sb, "Settings (sanitized)");
        if (snap.SettingsJson != null)
            sb.AppendLine(snap.SettingsJson);
        else
            sb.AppendLine("(unavailable)");

        Section(sb, $"Log tail (last ~{logTailLines} lines)");
        sb.AppendLine(ReadLogTail(snap.LogPath, logTailLines) ?? "(no log content)");

        sb.AppendLine();
        sb.AppendLine("--- end of report ---");
        return sb.ToString();
    }

    /// <summary>Copy of settings with nothing secret today; ready if fields appear later.</summary>
    public static string SanitizeSettingsJson(AppSettings settings)
    {
        try
        {
            // Clone via serialize so we never mutate live settings.
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            return json;
        }
        catch (Exception ex)
        {
            return "(settings serialize failed: " + ex.Message + ")";
        }
    }

    public static string? ReadLogTail(string? path, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(path) || maxLines <= 0)
            return null;
        try
        {
            if (!File.Exists(path))
                return null;

            // Read with shared access so logging can keep writing.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var ring = new Queue<string>(Math.Min(maxLines, 512));
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (ring.Count >= maxLines) ring.Dequeue();
                ring.Enqueue(line);
            }
            return ring.Count == 0 ? null : string.Join(Environment.NewLine, ring);
        }
        catch (Exception ex)
        {
            return "(log read failed: " + ex.Message + ")";
        }
    }

    public static bool TryOpenLogFolder(out string? path, out string? error)
    {
        path = LogDirectory;
        error = null;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string ResolveAppVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return info.Split('+')[0];
            return asm.GetName().Version?.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine("## " + title);
        sb.AppendLine(new string('-', Math.Min(40, title.Length + 3)));
    }

    private static void AppendJson(StringBuilder sb, string label, object? value)
    {
        sb.Append(label).Append(": ");
        if (value == null)
        {
            sb.AppendLine("(null)");
            return;
        }
        try { sb.AppendLine(JsonSerializer.Serialize(value, JsonOpts)); }
        catch { sb.AppendLine("(serialize failed)"); }
    }
}

/// <summary>Inputs collected on the UI thread / host side before pure BuildReport.</summary>
public sealed class DiagnosticsSnapshot
{
    public string? AppVersion { get; init; }
    public string? LogPath { get; init; }
    public SystemInfo? SystemInfo { get; init; }
    public MetricsSnapshot? Metrics { get; init; }
    public string? ActivePlanSummary { get; init; }
    public object? KeepAwake { get; init; }
    public object? PowerSource { get; init; }
    public object? ThermalGuard { get; init; }
    public object? IdlePowerGuard { get; init; }
    public object? CpuAutomation { get; init; }
    public object? BatteryPower { get; init; }
    public object? BatteryHealth { get; init; }
    public object? Memory { get; init; }
    public string? SettingsJson { get; init; }
}
