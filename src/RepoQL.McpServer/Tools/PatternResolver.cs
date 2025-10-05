using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace RepoQL.McpServer.Tools;

/// <summary>
/// Resolve glob patterns, directories, files, or URIs into repo-aware file:/// URIs.
/// Mirrors the CLI behavior but returns a list of URIs for MCP tools.
/// </summary>
public static class PatternResolver
{
    /// <summary>
    /// Resolve comma-separated patterns relative to the provided repository root.
    /// Supports:
    /// - Globs like "**/*.cs"
    /// - Directories like "src/"
    /// - File paths
    /// - Absolute or repo-relative file:/// URIs
    /// </summary>
    public static List<string> ResolvePatterns(string patterns, string repoRoot)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Capture explicit URIs or direct files
        foreach (var raw in Split(patterns))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();

            // Absolute URI
            if (Uri.TryCreate(s, UriKind.Absolute, out var abs) && !string.IsNullOrEmpty(abs.Scheme))
            {
                if (abs.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    // Normalize to repo-relative file:/// URIs if under repo
                    var local = abs.LocalPath;
                    var rel = GetRelativeTo(repoRoot, local).Replace('\\', '/');
                    results.Add(string.IsNullOrEmpty(rel) ? abs.AbsoluteUri : $"file:///{rel}");
                }
                else
                {
                    results.Add(abs.AbsoluteUri);
                }
                continue;
            }

            // Direct file path? Convert to repo-aware URI
            if (File.Exists(s))
            {
                var full = Path.GetFullPath(s);
                var rel = GetRelativeTo(repoRoot, full).Replace('\\', '/');
                if (!string.IsNullOrEmpty(rel)) results.Add($"file:///{rel}");
                continue;
            }
        }

        // 2) Expand directories and glob patterns relative to repo root
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var any = false;
        foreach (var raw in Split(patterns))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();

            if (IsGlob(s))
            {
                matcher.AddInclude(NormalizePattern(s));
                any = true;
                continue;
            }

            if (Directory.Exists(s))
            {
                var rel = GetRelativeTo(repoRoot, Path.GetFullPath(s));
                var pattern = string.IsNullOrEmpty(rel) ? "**/*" : rel.Replace('\\', '/') + "/**/*";
                matcher.AddInclude(pattern);
                any = true;
                continue;
            }

            // Non-glob, non-directory: treat as repo-relative file path pattern
            var candidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), s));
            var relFile = GetRelativeTo(repoRoot, candidate);
            if (!string.IsNullOrEmpty(relFile))
            {
                matcher.AddInclude(relFile.Replace('\\', '/'));
                any = true;
            }
        }

        if (any)
        {
            var rootInfo = new DirectoryInfo(repoRoot);
            var fs = new DirectoryInfoWrapper(rootInfo);
            var matches = matcher.Execute(fs);
            foreach (var m in matches.Files)
            {
                // m.Path is repo-relative
                var rel = m.Path.Replace('\\', '/');
                var full = Path.Combine(repoRoot, m.Path);
                if (!File.Exists(full)) continue;
                results.Add($"file:///{rel}");
            }
        }

        return results.ToList();
    }

    private static IEnumerable<string> Split(string patterns)
        => patterns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool IsGlob(string s) => s.IndexOfAny(['*', '?', '[', ']']) >= 0;

    private static string NormalizePattern(string p) => p.Replace('\\', '/');

    private static string GetRelativeTo(string baseDir, string path)
    {
        var b = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path);
        return p.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? p[b.Length..] : p;
    }
}

