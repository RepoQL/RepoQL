using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem;

/// <summary>
/// Git-ignore aware filter for file:// URIs. Excludes a set of relative paths such as the DB file.
/// </summary>
public sealed partial class RepoGitIgnoreFilter : IUriFilter
{
    private readonly Ignore.Ignore _ignore = new();

    private readonly HashSet<string> _excludedRelPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git"
    };

    private static readonly Regex DefaultExcludes = DefaultExcludesRegex();

    /// <summary>Create a filter using .gitignore at repo root and extra excluded relative paths.</summary>
    public RepoGitIgnoreFilter(string rootPath, IEnumerable<string>? extraExcludedRelPaths = null)
    {
        var rootPath1 = Path.GetFullPath(rootPath);
        var gi = Path.Combine(rootPath1, ".gitignore");
        if (File.Exists(gi)) _ignore.Add(File.ReadAllLines(gi));
        if (extraExcludedRelPaths == null)
            return;
        foreach (var r in extraExcludedRelPaths)
            _excludedRelPaths.Add(r.Replace('\\', '/').TrimStart('/'));
    }

    /// <inheritdoc/>
    public bool IncludeFile(RepoUri uri)
    {
        // Handle both repo:// and file:/// URIs
        if (!string.Equals(uri.Scheme, "repo", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rel = uri.AbsolutePath.TrimStart('/');

        // For file:/// URIs on Windows, the path might be like /C:/path/to/file
        // We need to extract just the relative path from the repo root
        if (string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            // Try to make it relative to the repo - this is a simplified approach
            // The path should already be relative in most cases
            rel = rel.Replace('\\', '/');

            // Check if path contains .repoql, .git, or .claude/worktrees directories
            if (rel.Contains("/.repoql/") || rel.Contains("/.git/") || rel.Contains("/.claude/worktrees/") ||
                rel.EndsWith("/.repoql") || rel.EndsWith("/.git") || rel.EndsWith("/.claude/worktrees"))
            {
                return false;
            }
        }

        if (DefaultExcludes.IsMatch(rel))
            return false;
        if (IsTempOrBackupFile(rel))
            return false;
        if (_excludedRelPaths.Contains(rel)) return false;
        return !_ignore.IsIgnored(rel);
    }

    /// <summary>
    /// Check if a filename represents a temporary or backup file that should be excluded.
    /// </summary>
    private static bool IsTempOrBackupFile(string path)
    {
        // Get just the filename
        var lastSlash = path.LastIndexOfAny(['/', '\\']);
        var fileName = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;

        // Common temp/backup file patterns:
        // - Ends with ~ (vim backup, editor temp files)
        // - Ends with .swp, .swo (vim swap files)
        // - Ends with .tmp
        // - Starts with ~ (Office temp files)
        // - Starts with .# (Emacs lock files)
        // - Ends with .bak
        if (fileName.EndsWith('~') ||
            fileName.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".swo", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith('~') ||
            fileName.StartsWith(".#"))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"(\.git|\.repoql|\.claude[\\/]worktrees)[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DefaultExcludesRegex();
}