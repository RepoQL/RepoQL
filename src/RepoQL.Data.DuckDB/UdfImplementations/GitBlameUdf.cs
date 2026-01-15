using LibGit2Sharp;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for on-demand git blame queries.
///
/// Purpose: Returns line-by-line authorship information for a file without
/// pre-indexing. Useful for understanding who wrote specific code.
///
/// Complexity: Uses LibGit2Sharp to compute blame on-demand. Can be slow
/// for large files but avoids the storage cost of pre-computing blame for all files.
/// </summary>
[UdfClass]
public class GitBlameUdf(RepositoryConfiguration repoConfig)
{
    private readonly string _repoRoot = repoConfig.Path;

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
        if (!Repository.IsValid(_repoRoot))
            throw new InvalidOperationException($"git_blame: Not a valid git repository: {_repoRoot}");

        // Convert URI to relative path for LibGit2Sharp
        var relativePath = UriToRelativePath(uri);
        if (relativePath is null)
            throw new InvalidOperationException($"git_blame: Invalid URI format: '{uri}'. Expected file:///path or relative path.");

        using var repo = new Repository(_repoRoot);

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
            var hunkStart = hunk.FinalStartLineNumber;
            var hunkEnd = hunkStart + hunk.LineCount - 1;

            // Apply line filters
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
    /// Converts a repository URI to a relative path for LibGit2Sharp.
    /// Accepts: file:///src/Foo.cs, src/Foo.cs, /src/Foo.cs
    /// Returns: src/Foo.cs (forward slashes)
    /// </summary>
    private static string? UriToRelativePath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        string relativePath;

        // Try to parse as URI first
        if (RepoUri.TryParse(input, out var repoUri))
        {
            // Only handle file:// URIs
            if (!string.Equals(repoUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                return null;

            // Get path portion (URL-decoded)
            relativePath = Uri.UnescapeDataString(repoUri.AbsolutePath);
        }
        else
        {
            // Treat as raw path
            relativePath = input;
        }

        // Remove leading slash and normalize to forward slashes
        relativePath = relativePath.TrimStart('/').Replace('\\', '/');

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
