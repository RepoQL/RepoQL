using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

internal static class PatternResolver
{
    public static List<string> ResolvePatterns(string patterns, string repoRoot)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in Split(patterns))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();

            if (Uri.TryCreate(s, UriKind.Absolute, out var abs) && !string.IsNullOrEmpty(abs.Scheme))
            {
                if (abs.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    var local = abs.LocalPath;
                    var rel = GetRelativeTo(repoRoot, local).Replace('\\', '/');
                    results.Add(string.IsNullOrEmpty(rel) ? abs.AbsoluteUri : $"file:///{rel}");
                }
                else results.Add(abs.AbsoluteUri);
                continue;
            }

            if (File.Exists(s))
            {
                var full = Path.GetFullPath(s);
                var rel = GetRelativeTo(repoRoot, full).Replace('\\', '/');
                if (!string.IsNullOrEmpty(rel)) results.Add($"file:///{rel}");
                continue;
            }
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var any = false;
        foreach (var raw in Split(patterns))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();

            if (IsGlob(s)) { matcher.AddInclude(NormalizePattern(s)); any = true; continue; }

            if (Directory.Exists(s))
            {
                var rel = GetRelativeTo(repoRoot, Path.GetFullPath(s));
                var pattern = string.IsNullOrEmpty(rel) ? "**/*" : rel.Replace('\\', '/') + "/**/*";
                matcher.AddInclude(pattern); any = true; continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), s));
            var relFile = GetRelativeTo(repoRoot, candidate);
            if (!string.IsNullOrEmpty(relFile)) { matcher.AddInclude(relFile.Replace('\\', '/')); any = true; }
        }

        if (any)
        {
            var rootInfo = new DirectoryInfo(repoRoot);
            var fs = new DirectoryInfoWrapper(rootInfo);
            var matches = matcher.Execute(fs);
            foreach (var m in matches.Files)
            {
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

