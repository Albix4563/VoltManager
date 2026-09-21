using System.Net;
using System.Net.Http;
using System.Text.Json;
using VoltManager.Models;

namespace VoltManager.Services;

internal enum UpdateRemoteStatus
{
    Ok,
    NotFound,
    RateLimited,
    Offline,
    Error,
}

internal sealed record UpdateRemoteResult<T>(UpdateRemoteStatus Status, T? Value = default, int? StatusCode = null)
{
    internal static UpdateRemoteResult<T> Ok(T value) => new(UpdateRemoteStatus.Ok, value);
}

internal sealed record GitHubReleaseAsset(string Name, string? DownloadUrl);

internal sealed record GitHubReleaseRecord(
    string TagName,
    string? Name,
    string? Body,
    string? PublishedAt,
    string? HtmlUrl,
    bool Prerelease,
    IReadOnlyList<GitHubReleaseAsset> Assets);

internal sealed class GitHubUpdateClient
{
    private readonly HttpClient _http;

    internal GitHubUpdateClient(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    internal async Task<UpdateRemoteResult<GitHubReleaseRecord?>> GetLatestReleaseAsync(
        string repo,
        UpdateChannelPolicy policy,
        CancellationToken cancellationToken)
    {
        if (!policy.IsPrerelease)
        {
            UpdateRemoteResult<string> response = await GetStringAsync(
                $"https://api.github.com/repos/{repo}/releases/latest", cancellationToken);
            if (response.Status != UpdateRemoteStatus.Ok)
                return new(response.Status, null, response.StatusCode);
            return UpdateRemoteResult<GitHubReleaseRecord?>.Ok(ParseRelease(response.Value!));
        }

        UpdateRemoteResult<IReadOnlyList<GitHubReleaseRecord>> releases = await GetReleasesAsync(repo, 10, cancellationToken);
        if (releases.Status != UpdateRemoteStatus.Ok)
            return new(releases.Status, null, releases.StatusCode);
        return UpdateRemoteResult<GitHubReleaseRecord?>.Ok(releases.Value!.FirstOrDefault(policy.Matches));
    }

    internal async Task<UpdateRemoteResult<IReadOnlyList<GitHubReleaseRecord>>> GetReleasesAsync(
        string repo,
        int perPage,
        CancellationToken cancellationToken)
    {
        UpdateRemoteResult<string> response = await GetStringAsync(
            $"https://api.github.com/repos/{repo}/releases?per_page={perPage}", cancellationToken);
        if (response.Status != UpdateRemoteStatus.Ok)
            return new(response.Status, Array.Empty<GitHubReleaseRecord>(), response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(response.Value!);
        var releases = new List<GitHubReleaseRecord>();
        foreach (JsonElement element in doc.RootElement.EnumerateArray())
            releases.Add(ParseRelease(element));
        return UpdateRemoteResult<IReadOnlyList<GitHubReleaseRecord>>.Ok(releases);
    }

    internal async Task<UpdateRemoteResult<List<CommitInfo>>> GetCommitsAsync(
        string repo,
        string branch,
        CancellationToken cancellationToken)
    {
        UpdateRemoteResult<string> response = await GetStringAsync(
            $"https://api.github.com/repos/{repo}/commits?sha={branch}&per_page=20", cancellationToken);
        if (response.Status != UpdateRemoteStatus.Ok)
            return new(response.Status, new List<CommitInfo>(), response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(response.Value!);
        var commits = new List<CommitInfo>();
        foreach (JsonElement c in doc.RootElement.EnumerateArray())
        {
            JsonElement commit = c.GetProperty("commit");
            string sha = c.GetProperty("sha").GetString() ?? "";
            commits.Add(new CommitInfo
            {
                Sha = sha[..Math.Min(7, sha.Length)],
                Message = (commit.GetProperty("message").GetString() ?? "").Split('\n')[0],
                Author = commit.GetProperty("author").GetProperty("name").GetString() ?? "",
                Date = commit.GetProperty("author").GetProperty("date").GetString() ?? "",
            });
        }
        return UpdateRemoteResult<List<CommitInfo>>.Ok(commits);
    }

    private async Task<UpdateRemoteResult<string>> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                return new(UpdateRemoteStatus.RateLimited, null, (int)response.StatusCode);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateRemoteStatus.NotFound, null, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                return new(UpdateRemoteStatus.Error, null, (int)response.StatusCode);
            return UpdateRemoteResult<string>.Ok(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(UpdateRemoteStatus.Offline);
        }
    }

    private static GitHubReleaseRecord ParseRelease(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return ParseRelease(doc.RootElement);
    }

    private static GitHubReleaseRecord ParseRelease(JsonElement element)
    {
        var assets = new List<GitHubReleaseAsset>();
        if (element.TryGetProperty("assets", out JsonElement assetsElement))
        {
            foreach (JsonElement asset in assetsElement.EnumerateArray())
            {
                assets.Add(new GitHubReleaseAsset(
                    asset.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
                    asset.TryGetProperty("browser_download_url", out JsonElement url) ? url.GetString() : null));
            }
        }

        return new GitHubReleaseRecord(
            element.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString() ?? "" : "",
            element.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() : null,
            element.TryGetProperty("body", out JsonElement body) ? body.GetString() : null,
            element.TryGetProperty("published_at", out JsonElement published) ? published.GetString() : null,
            element.TryGetProperty("html_url", out JsonElement html) ? html.GetString() : null,
            element.TryGetProperty("prerelease", out JsonElement prerelease) && prerelease.GetBoolean(),
            assets);
    }
}
