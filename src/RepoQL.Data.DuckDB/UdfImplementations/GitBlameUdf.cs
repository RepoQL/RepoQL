using LibGit2Sharp;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for on-demand git blame queries.
///
/// Purpose: Returns line-by-line authorship information for a file without
/// pre-indexing. Useful for understanding who wrote specific code.
///
/// Complexity: Uses LibGit2Sharp to compute blame on-demand. Can be slow
/// for large files but avoids the storage cost of pre-computing blame for all files.
/// Resolves any URI scheme (file://, github://, etc.) to a physical git repository
/// via the file system registry.
/// </summary>
[UdfClass]
public class GitBlameUdf(RepositoryConfiguration repoConfig, IFileSystemRegistry? fileSystemRegistry = null)
{
    private readonly string _repoRoot = repoConfig.Path;
    private readonly IFileSystemRegistry? _registry = fileSystemRegistry;

    /// <summary>
    /// Returns git blame information for a file.
    /// </summary>
    /// <param name="uri">Repository URI (e.g., file:///src/Foo.cs)</param>
    /// <param name="startLine">Optional start line (1-based)</param>
    /// <param name="endLine">Optional end line (1-based)</param>
    [StructuredUdf("_git_blame_internal", Description = "Returns line-by-line git blame for a file")]
    public IEnumerable<GitBlameRow> Blame(
        string uri,
        [UdfDefault("NULL")] int? startLine,
        [UdfDefault("NULL")] int? endLine)
    {
        // Resolve URI to a git repo root and relative path
        var target = ResolveGitRepository(uri);
        if (target is null)
            yield break; // URI scheme not backed by a physical git repository

        var (repoRoot, relativePath) = target.Value;

        if (!Repository.IsValid(repoRoot))
            yield break; // Not a valid git repository at this path

        using var repo = new Repository(repoRoot);

        BlameHunkCollection blame;
        try
        {
            blame = repo.Blame(relativePath);
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist in the given tree") ||
                                   ex.Message.Contains("does not exist in the index"))
        {
            // File is untracked or not in git - return empty (common when using globs)
            yield break;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"git_blame: Failed to blame '{relativePath}': {ex.Message}", ex);
        }

        foreach (var hunk in blame)
        {
            // LibGit2Sharp's FinalStartLineNumber is 0-based, convert to 1-based
            var hunkStart = hunk.FinalStartLineNumber + 1;
            var hunkEnd = hunkStart + hunk.LineCount - 1;

            // Apply line filters (user provides 1-based line numbers)
            var effectiveStart = startLine.HasValue ? Math.Max(hunkStart, startLine.Value) : hunkStart;
            var effectiveEnd = endLine.HasValue ? Math.Min(hunkEnd, endLine.Value) : hunkEnd;

            if (effectiveStart > effectiveEnd)
                continue;

            for (var lineNum = effectiveStart; lineNum <= effectiveEnd; lineNum++)
            {
                yield return new GitBlameRow(
                    lineNum,
                    hunk.FinalCommit.Sha,
                    hunk.FinalCommit.Author.Name,
                    hunk.FinalCommit.Author.Email,
                    hunk.FinalCommit.Author.When,
                    hunk.FinalCommit.MessageShort
                );
            }
        }
    }

    /// <summary>
    /// Resolves a URI to the git repository root and file's relative path within it.
    /// For file:// URIs, uses the primary repo root. For other schemes (github://, etc.),
    /// resolves through the file system registry to find the backing PhysicalFileSystem.
    /// </summary>
    private (string RepoRoot, string RelativePath)? ResolveGitRepository(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (RepoUri.TryParse(input, out var repoUri))
        {
            if (string.Equals(repoUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = ExtractRelativePath(repoUri);
                return relativePath is null ? null : (_repoRoot, relativePath);
            }

            // Non-file scheme — resolve through file system registry
            if (_registry is not null)
            {
                try
                {
                    var vfs = _registry.Resolve(repoUri);
                    if (vfs is PhysicalFileSystem pfs)
                    {
                        var resolved = FileUriPathResolver.Resolve(pfs.RootPath, repoUri, repoUri.Scheme);
                        var relativePath = Path.GetRelativePath(pfs.RootPath, resolved.AbsolutePath)
                            .Replace('\\', '/');
                        return string.IsNullOrEmpty(relativePath) ? null : (pfs.RootPath, relativePath);
                    }
                }
                catch (NotSupportedException)
                {
                    // No VFS registered for this scheme — fall through
                }
            }

            return null;
        }

        // Raw path fallback
        var rawPath = input.TrimStart('/').Replace('\\', '/');
        return string.IsNullOrEmpty(rawPath) ? null : (_repoRoot, rawPath);
    }

    private static string? ExtractRelativePath(RepoUri repoUri)
    {
        var relativePath = Uri.UnescapeDataString(repoUri.AbsolutePath)
            .TrimStart('/')
            .Replace('\\', '/');
        return string.IsNullOrEmpty(relativePath) ? null : relativePath;
    }

    public record GitBlameRow(
        int LineNumber,
        string CommitHash,
        string AuthorName,
        string AuthorEmail,
        DateTimeOffset AuthorDate,
        string Message
    );
}
