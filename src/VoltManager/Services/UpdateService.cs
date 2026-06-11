using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using VoltManager.Models;

namespace VoltManager.Services;

public class UpdateService
{
    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    public event Action<double>? DownloadProgress;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VoltManager");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<UpdateInfo> CheckForUpdatesAsync()
    {
        string repo = _settings.Current.UpdateRepo;
        try
        {
            string? latestVersion = null, notes = null, downloadUrl = null;
            bool hasRelease = false;

            var relResp = await _http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (relResp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                (int)relResp.StatusCode == 429)
                return new UpdateInfo { Status = "ratelimited", CurrentVersion = CurrentVersion, Message = "Limite richieste GitHub raggiunto. Riprova più tardi." };

            if (relResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await relResp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                latestVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
                notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }
                hasRelease = true;
            }
            else if (relResp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateInfo { Status = "error", CurrentVersion = CurrentVersion, Message = $"GitHub ha risposto {(int)relResp.StatusCode}." };
            }

            // Changelog from main branch commits.
            var commits = new List<CommitInfo>();
            var comResp = await _http.GetAsync($"https://api.github.com/repos/{repo}/commits?sha=main&per_page=20");
            if (comResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await comResp.Content.ReadAsStringAsync());
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    var commit = c.GetProperty("commit");
                    commits.Add(new CommitInfo
                    {
                        Sha = (c.GetProperty("sha").GetString() ?? "")[..Math.Min(7, (c.GetProperty("sha").GetString() ?? "").Length)],
                        Message = (commit.GetProperty("message").GetString() ?? "").Split('\n')[0],
                        Author = commit.GetProperty("author").GetProperty("name").GetString() ?? "",
                        Date = commit.GetProperty("author").GetProperty("date").GetString() ?? "",
                    });
                }
            }
            else if (!hasRelease)
            {
                return new UpdateInfo { Status = "norelease", CurrentVersion = CurrentVersion, Message = "Repository non trovato o nessuna release pubblicata." };
            }

            bool updateAvailable = hasRelease && latestVersion != null &&
                                   CompareVersions(latestVersion, CurrentVersion) > 0;

            return new UpdateInfo
            {
                Status = "ok",
                UpdateAvailable = updateAvailable,
                LatestVersion = latestVersion,
                CurrentVersion = CurrentVersion,
                ReleaseNotes = notes,
                DownloadUrl = downloadUrl,
                Commits = commits,
                Message = updateAvailable
                    ? $"Nuova versione {latestVersion} disponibile."
                    : hasRelease ? "VoltManager è aggiornato." : "Nessuna release pubblicata. Mostro gli ultimi commit del branch main.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UpdateInfo { Status = "offline", CurrentVersion = CurrentVersion, Message = "Impossibile contattare GitHub. Verifica la connessione." };
        }
        catch (Exception ex)
        {
            return new UpdateInfo { Status = "error", CurrentVersion = CurrentVersion, Message = ex.Message };
        }
    }

    /// <summary>SemVer-ish compare: returns &gt;0 if a newer than b.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parse(string v) => v.Split('-')[0].Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int xa = i < pa.Length ? pa[i] : 0;
            int xb = i < pb.Length ? pb[i] : 0;
            if (xa != xb) return xa.CompareTo(xb);
        }
        return 0;
    }

    /// <summary>Downloads installer to %TEMP%, reports progress, returns local path.</summary>
    public async Task<string> DownloadUpdateAsync(string url)
    {
        string dest = Path.Combine(Path.GetTempPath(), "VoltManagerUpdate.exe");
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (total > 0) DownloadProgress?.Invoke(Math.Round(readTotal * 100.0 / total, 1));
        }
        DownloadProgress?.Invoke(100);
        return dest;
    }
}
