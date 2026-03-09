using System.Text.RegularExpressions;

namespace RepoQL.Embedding.Client;

/// <summary>
/// Purpose: Converts git remote URLs into one canonical cache source identifier.
/// Complexity: Handles HTTPS, SSH, scp-style remotes, RepoQL github:// URIs,
/// credential stripping, and conservative fallback for malformed inputs.
/// </summary>
public static partial class SourceNormalizer
{
    [GeneratedRegex("^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase, "en-NZ")]
    private static partial Regex SchemePrefixRegex();

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        var candidate = input.Trim();
        if (candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return "";

        if (candidate.StartsWith("github://", StringComparison.OrdinalIgnoreCase))
            return NormalizeHostAndPath($"github.com/{candidate["github://".Length..]}");

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile)
                return "";

            var host = absoluteUri.Host;
            if (!string.IsNullOrWhiteSpace(host))
                return NormalizeHostAndPath($"{host}/{absoluteUri.AbsolutePath.TrimStart('/')}");
        }

        var withoutScheme = SchemePrefixRegex().Replace(candidate, "");
        return NormalizeHostAndPath(withoutScheme);
    }

    private static string NormalizeHostAndPath(string input)
    {
        var candidate = input.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return "";

        candidate = candidate.TrimStart('/');

        var atIndex = candidate.LastIndexOf('@');
        if (atIndex >= 0)
            candidate = candidate[(atIndex + 1)..];

#pragma warning disable CA1307 // Single-character delimiter search is ordinal by definition.
        var slashIndex = candidate.IndexOf('/');
        var colonIndex = candidate.IndexOf(':');
#pragma warning restore CA1307
        if (colonIndex >= 0 && (slashIndex < 0 || colonIndex < slashIndex))
            candidate = string.Concat(candidate.AsSpan(0, colonIndex), "/", candidate.AsSpan(colonIndex + 1));

        candidate = candidate.Trim('/');
        if (candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[..^4];

#pragma warning disable CA1308 // Canonical cache keys must be lowercase to match the cache shard contract.
        return candidate.Trim('/').ToLowerInvariant();
#pragma warning restore CA1308
    }
}
