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

        // Handle SCP-style SSH URLs: user@host:path (e.g. git@github.com:owner/repo.git,
        // alice@git.company.com:team/repo.git)
        var atIndex = url.IndexOf('@');
        if (atIndex > 0 && !url.Contains("://"))
        {
            var colonIndex = url.IndexOf(':', atIndex + 1);
            if (colonIndex < 0)
                return "";

            var host = url[(atIndex + 1)..colonIndex];
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

            var host = uri.Host.ToLowerInvariant();
            if (uri.Port is not (-1) && !IsDefaultPort(uri.Scheme, uri.Port))
                host = $"{host}:{uri.Port}";

            return $"{host}/{path.ToLowerInvariant()}";
        }

        return "";
    }

    private static bool IsDefaultPort(string scheme, int port) => scheme switch
    {
        "https" => port == 443,
        "http" => port == 80,
        "ssh" => port == 22,
        _ => false
    };

    private static string StripDotGit(string path)
    {
        path = path.TrimEnd('/');
        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? path[..^4]
            : path;
    }
}
