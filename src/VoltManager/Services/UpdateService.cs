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
        string channel = _settings.Current.AutoUpdates?.UpdateChannel ?? "stable";
        bool isDev = channel == "dev";
        bool isPreview = channel == "preview";
        bool isPrerelease = isDev || isPreview;
        try
        {
            string? latestVersion = null, notes = null, downloadUrl = null;
            bool hasRelease = false;

            HttpResponseMessage relResp;
            JsonElement root = default;
            bool found = false;

            if (isPrerelease)
            {
                relResp = await _http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=10");
                if (relResp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await relResp.Content.ReadAsStringAsync());
                    foreach (var rel in doc.RootElement.EnumerateArray())
                    {
                        if (rel.TryGetProperty("prerelease", out var p) && p.GetBoolean())
                        {
                            var t = rel.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
                            if (isDev && t.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
                            {
                                root = rel.Clone();
                                found = true;
                                break;
                            }
                            else if (isPreview && !t.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
                            {
                                root = rel.Clone();
                                found = true;
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                relResp = await _http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
                if (relResp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await relResp.Content.ReadAsStringAsync());
                    root = doc.RootElement.Clone();
                    found = true;
                }
            }

            if (relResp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                (int)relResp.StatusCode == 429)
                return new UpdateInfo { Status = "ratelimited", CurrentVersion = CurrentVersion, Message = "Limite richieste GitHub raggiunto. Riprova più tardi." };

            if (found)
            {
                latestVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
                if (isPrerelease && latestVersion != null)
                {
                    if (isDev && !latestVersion.Contains("ALPHA", StringComparison.OrdinalIgnoreCase))
                        latestVersion += "-ALPHA";
                    else if (isPreview && !latestVersion.Contains("BETA", StringComparison.OrdinalIgnoreCase) && !latestVersion.Contains("ALPHA", StringComparison.OrdinalIgnoreCase))
                        latestVersion += "-BETA";
                }

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
            else if (relResp.StatusCode != System.Net.HttpStatusCode.NotFound && !relResp.IsSuccessStatusCode)
            {
                return new UpdateInfo { Status = "error", CurrentVersion = CurrentVersion, Message = $"GitHub ha risposto {(int)relResp.StatusCode}." };
            }

            // Changelog from branch commits.
            var targetBranch = isDev ? "Dev" : (isPreview ? "Preview" : "main");
            var (commits, commitsOk) = await FetchCommitsAsync(repo, targetBranch);
            if (!commitsOk && !hasRelease)
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
                    ? $"Nuova versione {(isDev ? "ALPHA " : (isPreview ? "BETA " : ""))}{latestVersion} disponibile."
                    : hasRelease ? "VoltManager è aggiornato." : $"Nessuna release pubblicata. Mostro gli ultimi commit del branch {targetBranch}.",
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

    /// <summary>Fetches last 20 commits from branch. Returns list + whether the call succeeded.</summary>
    private async Task<(List<CommitInfo> commits, bool ok)> FetchCommitsAsync(string repo, string branch)
    {
        var commits = new List<CommitInfo>();
        var comResp = await _http.GetAsync($"https://api.github.com/repos/{repo}/commits?sha={branch}&per_page=20");
        if (!comResp.IsSuccessStatusCode) return (commits, false);

        using var doc = JsonDocument.Parse(await comResp.Content.ReadAsStringAsync());
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var commit = c.GetProperty("commit");
            var sha = c.GetProperty("sha").GetString() ?? "";
            commits.Add(new CommitInfo
            {
                Sha = sha[..Math.Min(7, sha.Length)],
                Message = (commit.GetProperty("message").GetString() ?? "").Split('\n')[0],
                Author = commit.GetProperty("author").GetProperty("name").GetString() ?? "",
                Date = commit.GetProperty("author").GetProperty("date").GetString() ?? "",
            });
        }
        return (commits, true);
    }

    /// <summary>Full release history from GitHub. Falls back to main commits if no release.</summary>
    public async Task<ReleaseHistory> GetReleaseHistoryAsync()
    {
        string repo = _settings.Current.UpdateRepo;
        string channel = _settings.Current.AutoUpdates?.UpdateChannel ?? "stable";
        bool isDev = channel == "dev";
        bool isPreview = channel == "preview";
        bool isPrerelease = isDev || isPreview;
        try
        {
            var resp = await _http.GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=100");
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
                return new ReleaseHistory { Status = "ratelimited", CurrentVersion = CurrentVersion, Message = "Limite richieste GitHub raggiunto. Riprova più tardi." };

            var releases = new List<ReleaseEntry>();
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var r in doc.RootElement.EnumerateArray())
                {
                    var version = (r.TryGetProperty("tag_name", out var t) ? t.GetString() : null)?.TrimStart('v', 'V') ?? "";
                    var p = r.TryGetProperty("prerelease", out var prereleaseProp) ? prereleaseProp.GetBoolean() : false;
                    // Filtro per canale: Prerelease vede solo prerelease, Stabile solo le normali.
                    if (p != isPrerelease) continue;
                    if (isPrerelease)
                    {
                        bool isAlpha = version.Contains("-alpha", StringComparison.OrdinalIgnoreCase);
                        if (isDev && !isAlpha) continue;
                        if (isPreview && isAlpha) continue;

                        if (isDev && version.Length > 0 && !version.Contains("ALPHA", StringComparison.OrdinalIgnoreCase))
                            version += "-ALPHA";
                        else if (isPreview && version.Length > 0 && !version.Contains("BETA", StringComparison.OrdinalIgnoreCase) && !version.Contains("ALPHA", StringComparison.OrdinalIgnoreCase))
                            version += "-BETA";
                    }

                    releases.Add(new ReleaseEntry
                    {
                        Version = version,
                        Name = r.TryGetProperty("name", out var n) ? n.GetString() : null,
                        Date = r.TryGetProperty("published_at", out var d) ? d.GetString() ?? "" : "",
                        Notes = r.TryGetProperty("body", out var b) ? b.GetString() : null,
                        HtmlUrl = r.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                        Prerelease = p,
                        IsCurrent = version.Length > 0 && CompareVersions(version, CurrentVersion) == 0,
                    });
                }
            }
            else if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                return new ReleaseHistory { Status = "error", CurrentVersion = CurrentVersion, Message = $"GitHub ha risposto {(int)resp.StatusCode}." };
            }

            if (releases.Count > 0)
            {
                return new ReleaseHistory { Status = "ok", CurrentVersion = CurrentVersion, Releases = releases };
            }

            // No published release: fall back to recent branch commits.
            var targetBranch = isDev ? "Dev" : (isPreview ? "Preview" : "main");
            var (commits, commitsOk) = await FetchCommitsAsync(repo, targetBranch);
            return new ReleaseHistory
            {
                Status = "norelease",
                CurrentVersion = CurrentVersion,
                Commits = commits,
                Message = commitsOk
                    ? $"Nessuna release pubblicata. Mostro gli ultimi commit del branch {targetBranch}."
                    : "Repository non trovato o nessuna release pubblicata.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ReleaseHistory { Status = "offline", CurrentVersion = CurrentVersion, Message = "Impossibile contattare GitHub. Verifica la connessione." };
        }
        catch (Exception ex)
        {
            return new ReleaseHistory { Status = "error", CurrentVersion = CurrentVersion, Message = ex.Message };
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
