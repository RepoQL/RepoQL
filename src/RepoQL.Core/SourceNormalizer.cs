namespace RepoQL.Core;

/// <summary>
/// Purpose: Normalize git remote URLs into a canonical form for source identification.
/// Complexity: Strips protocol, auth, trailing .git, and normalizes to lowercase owner/repo.
/// </summary>
public static class SourceNormalizer
{
    /// <summary>
    /// Normalize a git remote URL to a canonical form (e.g. "github.com/owner/repo").
    /// Returns empty string if the URL cannot be normalized.
    /// </summary>
    public static string Normalize(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return "";

        var url = remoteUrl.Trim();

        // Handle SSH URLs: git@github.com:owner/repo.git
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colonIndex = url.IndexOf(':');
            if (colonIndex < 0)
                return "";

            var host = url[4..colonIndex];
            if (string.IsNullOrEmpty(host))
                return "";

            var path = url[(colonIndex + 1)..];
            path = StripDotGit(path);
            return $"{host.ToLowerInvariant()}/{path.ToLowerInvariant()}";
        }

        // Handle HTTPS/HTTP/SSH protocol URLs
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http" || uri.Scheme == "ssh"))
        {
            var path = uri.AbsolutePath.TrimStart('/');
            path = StripDotGit(path);
            if (string.IsNullOrEmpty(path))
                return "";

            return $"{uri.Host.ToLowerInvariant()}/{path.ToLowerInvariant()}";
        }

        return "";
    }

    private static string StripDotGit(string path)
    {
        path = path.TrimEnd('/');
        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? path[..^4]
            : path;
    }
}
