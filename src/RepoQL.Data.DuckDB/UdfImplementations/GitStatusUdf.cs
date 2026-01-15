using LibGit2Sharp;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for on-demand git working copy status.
///
/// Purpose: Returns current working copy status (modified, staged, untracked files)
/// without pre-indexing. Equivalent to `git status --porcelain`.
///
/// Complexity: Uses LibGit2Sharp to query status on-demand.
/// </summary>
[UdfClass]
public class GitStatusUdf(RepositoryConfiguration repoConfig)
{
    private readonly string _repoRoot = repoConfig.Path;

    /// <summary>
    /// Returns working copy status for all changed files.
    /// </summary>
    /// <param name="includeUntracked">Include untracked files (default true)</param>
    /// <param name="includeIgnored">Include ignored files (default false)</param>
    [StructuredUdf("_git_status_internal", Description = "Returns working copy status")]
    public IEnumerable<GitStatusRow> Status(
        [UdfDefault("'true'")] bool includeUntracked,
        [UdfDefault("'false'")] bool includeIgnored)
    {
        if (!Repository.IsValid(_repoRoot))
            throw new InvalidOperationException($"git_status: Not a valid git repository: {_repoRoot}");

        using var repo = new Repository(_repoRoot);

        var options = new StatusOptions
        {
            IncludeUntracked = includeUntracked,
            IncludeIgnored = includeIgnored,
            RecurseUntrackedDirs = true
        };

        RepositoryStatus status;
        try
        {
            status = repo.RetrieveStatus(options);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"git_status: Failed to retrieve status: {ex.Message}", ex);
        }

        foreach (var entry in status)
        {
            // Skip clean files
            if (entry.State == FileStatus.Unaltered)
                continue;

            yield return new GitStatusRow(
                RelativePathToUri(entry.FilePath),
                MapIndexStatus(entry.State),
                MapWorkTreeStatus(entry.State),
                GetStatusCategory(entry.State),
                entry.State.HasFlag(FileStatus.Conflicted)
            );
        }
    }

    /// <summary>
    /// Converts a git-relative path to a RepoQL file URI.
    /// </summary>
    private static string RelativePathToUri(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return $"file:///{normalized}";
    }

    /// <summary>
    /// Maps FileStatus to index (staged) status character.
    /// </summary>
    private static string MapIndexStatus(FileStatus state)
    {
        if (state.HasFlag(FileStatus.NewInIndex)) return "A";
        if (state.HasFlag(FileStatus.ModifiedInIndex)) return "M";
        if (state.HasFlag(FileStatus.DeletedFromIndex)) return "D";
        if (state.HasFlag(FileStatus.RenamedInIndex)) return "R";
        if (state.HasFlag(FileStatus.TypeChangeInIndex)) return "T";
        return " ";
    }

    /// <summary>
    /// Maps FileStatus to worktree (unstaged) status character.
    /// </summary>
    private static string MapWorkTreeStatus(FileStatus state)
    {
        if (state.HasFlag(FileStatus.NewInWorkdir)) return "?";
        if (state.HasFlag(FileStatus.ModifiedInWorkdir)) return "M";
        if (state.HasFlag(FileStatus.DeletedFromWorkdir)) return "D";
        if (state.HasFlag(FileStatus.RenamedInWorkdir)) return "R";
        if (state.HasFlag(FileStatus.TypeChangeInWorkdir)) return "T";
        if (state.HasFlag(FileStatus.Conflicted)) return "U";
        return " ";
    }

    /// <summary>
    /// Categorizes the file status for easier filtering.
    /// </summary>
    private static string GetStatusCategory(FileStatus state)
    {
        if (state.HasFlag(FileStatus.Conflicted)) return "conflict";
        if (state.HasFlag(FileStatus.NewInWorkdir)) return "untracked";
        if (state.HasFlag(FileStatus.Ignored)) return "ignored";

        // Check if staged
        var isStaged = state.HasFlag(FileStatus.NewInIndex) ||
                       state.HasFlag(FileStatus.ModifiedInIndex) ||
                       state.HasFlag(FileStatus.DeletedFromIndex) ||
                       state.HasFlag(FileStatus.RenamedInIndex) ||
                       state.HasFlag(FileStatus.TypeChangeInIndex);

        // Check if has unstaged changes
        var isUnstaged = state.HasFlag(FileStatus.ModifiedInWorkdir) ||
                         state.HasFlag(FileStatus.DeletedFromWorkdir) ||
                         state.HasFlag(FileStatus.RenamedInWorkdir) ||
                         state.HasFlag(FileStatus.TypeChangeInWorkdir);

        if (isStaged && isUnstaged) return "staged+modified";
        if (isStaged) return "staged";
        if (isUnstaged) return "modified";

        return "unknown";
    }

    public record GitStatusRow(
        string Uri,
        string IndexStatus,
        string WorkTreeStatus,
        string Category,
        bool IsConflicted
    );
}
