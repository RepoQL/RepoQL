using System.Text;

namespace RepoQL.Contracts;

/// <summary>
/// File-backed persistence for skipped indexing URIs.
/// </summary>
public static class SkipListFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string GetPath(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        return Path.Combine(repoRoot, ".repoql", "skip-list.txt");
    }

    public static bool TryLoadEntries(string repoRoot, out HashSet<string> entries, out string? error)
    {
        entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        error = null;

        try
        {
            var path = GetPath(repoRoot);
            if (!File.Exists(path))
                return true;

            foreach (var rawLine in File.ReadLines(path, Utf8NoBom))
            {
                var trimmed = rawLine.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                entries.Add(NormalizeLine(trimmed));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Contains(IReadOnlySet<string> entries, RepoUri uri)
        => entries.Contains(NormalizeUri(uri));

    public static bool TryAppend(string repoRoot, RepoUri uri, out string? error)
    {
        return TryRewrite(repoRoot, lines =>
        {
            var normalized = NormalizeUri(uri);
            var exists = lines.Any(line => IsUriLineMatch(line, normalized));
            if (!exists)
                lines.Add(normalized);
            return true;
        }, out error);
    }

    public static bool TryRemove(string repoRoot, RepoUri uri, out string? error)
    {
        return TryRewrite(repoRoot, lines =>
        {
            var normalized = NormalizeUri(uri);
            lines.RemoveAll(line => IsUriLineMatch(line, normalized));
            return true;
        }, out error);
    }

    private static bool TryRewrite(string repoRoot, Func<List<string>, bool> mutator, out string? error)
    {
        error = null;
        try
        {
            var path = GetPath(repoRoot);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var lines = File.Exists(path)
                ? File.ReadAllLines(path, Utf8NoBom).ToList()
                : [];

            mutator(lines);
            File.WriteAllLines(path, lines, Utf8NoBom);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsUriLineMatch(string rawLine, string normalizedUri)
    {
        var trimmed = rawLine.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return false;

        return string.Equals(NormalizeLine(trimmed), normalizedUri, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLine(string rawLine)
    {
        if (RepoUri.TryParse(rawLine, out var parsed) && parsed is not null)
            return NormalizeUri(parsed);
        return rawLine.Trim();
    }

    private static string NormalizeUri(RepoUri uri)
    {
        var normalized = RepoUri.NormalizeContainer(uri);
        return normalized;
    }
}
