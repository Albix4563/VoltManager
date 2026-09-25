using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VoltManager.Models;

public class CustomLauncherApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

public class LauncherSettings
{
    public const int MaxCustomApps = 24;

    private static readonly string[] AllowedExtensions = [".exe", ".lnk", ".url"];

    [JsonPropertyName("customApps")] public List<CustomLauncherApp> CustomApps { get; set; } = new();
    [JsonPropertyName("hiddenIds")] public List<string> HiddenIds { get; set; } = new();

    public static string CustomIdFor(string path)
    {
        string normalized = path.Trim().ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "custom:" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    // Only local programs and shortcuts: no scripts, no network shares.
    public static bool IsAllowedCustomPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;
        if (!System.IO.Path.IsPathFullyQualified(trimmed)) return false;
        return AllowedExtensions.Contains(System.IO.Path.GetExtension(trimmed), StringComparer.OrdinalIgnoreCase);
    }

    public void Normalize()
    {
        CustomApps ??= new List<CustomLauncherApp>();
        HiddenIds ??= new List<string>();

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<CustomLauncherApp>();
        foreach (var app in CustomApps)
        {
            if (app == null || !IsAllowedCustomPath(app.Path)) continue;
            string path = app.Path.Trim();
            if (!seenPaths.Add(path)) continue;

            app.Path = path;
            app.Id = CustomIdFor(path);
            app.Name = string.IsNullOrWhiteSpace(app.Name)
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : app.Name.Trim();
            apps.Add(app);
            if (apps.Count >= MaxCustomApps) break;
        }
        CustomApps = apps;

        HiddenIds = HiddenIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record LauncherEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("args")] string? Arguments,
    [property: JsonPropertyName("iconDataUrl")] string? IconDataUrl,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("hidden")] bool Hidden);

public sealed record LaunchResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] string? Error);
