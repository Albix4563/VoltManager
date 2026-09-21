namespace VoltManager.Services;

internal enum UpdateChannelKind
{
    Stable,
    Preview,
    Dev,
}

internal readonly record struct UpdateChannelPolicy(UpdateChannelKind Kind)
{
    internal static UpdateChannelPolicy Parse(string? channel)
        => channel?.Trim().ToLowerInvariant() switch
        {
            "dev" => new(UpdateChannelKind.Dev),
            "preview" => new(UpdateChannelKind.Preview),
            _ => new(UpdateChannelKind.Stable),
        };

    internal bool IsPrerelease => Kind != UpdateChannelKind.Stable;
    internal bool IsDev => Kind == UpdateChannelKind.Dev;
    internal bool IsPreview => Kind == UpdateChannelKind.Preview;
    internal string Branch => Kind switch
    {
        UpdateChannelKind.Dev => "Dev",
        UpdateChannelKind.Preview => "Preview",
        _ => "main",
    };

    internal bool Matches(GitHubReleaseRecord release)
    {
        if (!IsPrerelease)
            return !release.Prerelease;
        if (!release.Prerelease)
            return false;

        bool alpha = release.TagName.Contains("-alpha", StringComparison.OrdinalIgnoreCase);
        return IsDev ? alpha : !alpha;
    }

    internal string NormalizeVersion(string tag)
    {
        string version = tag.TrimStart('v', 'V');
        if (IsDev && version.Length > 0 && !version.Contains("ALPHA", StringComparison.OrdinalIgnoreCase))
            return version + "-ALPHA";
        if (IsPreview && version.Length > 0 &&
            !version.Contains("BETA", StringComparison.OrdinalIgnoreCase) &&
            !version.Contains("ALPHA", StringComparison.OrdinalIgnoreCase))
            return version + "-BETA";
        return version;
    }

    internal string VersionLabel => IsDev ? "ALPHA " : IsPreview ? "BETA " : "";
}
