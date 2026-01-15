using LibGit2Sharp;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for on-demand git diff between refs.
///
/// Purpose: Returns file changes between two git refs (branches, commits, tags).
/// Useful for understanding what changed between versions.
///
/// Complexity: Uses LibGit2Sharp to compute diff on-demand.
/// </summary>
[UdfClass]
public class GitDiffUdf(RepositoryConfiguration repoConfig)
{
    private readonly string _repoRoot = repoConfig.Path;

    /// <summary>
    /// Returns file changes between two git refs.
    /// </summary>
    [StructuredUdf("_git_diff_internal", Description = "Returns file changes between two git refs")]
    public IEnumerable<GitDiffRow> Diff(
        string fromRef,
        [UdfDefault("'HEAD'")] string toRef)
    {
        if (!Repository.IsValid(_repoRoot))
            throw new InvalidOperationException($"git_diff: Not a valid git repository: {_repoRoot}");

        using var repo = new Repository(_repoRoot);

        Commit? fromCommit;
        Commit? toCommit;

        try
        {
            fromCommit = ResolveCommit(repo, fromRef);
            toCommit = ResolveCommit(repo, toRef);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"git_diff: Failed to resolve refs '{fromRef}' or '{toRef}': {ex.Message}", ex);
        }

        if (fromCommit is null)
            throw new InvalidOperationException($"git_diff: Could not resolve ref '{fromRef}' to a commit");

        if (toCommit is null)
            throw new InvalidOperationException($"git_diff: Could not resolve ref '{toRef}' to a commit");

        TreeChanges changes;
        Patch? patch = null;
        try
        {
            changes = repo.Diff.Compare<TreeChanges>(fromCommit.Tree, toCommit.Tree);
            // Get patch for line stats and binary detection
            patch = repo.Diff.Compare<Patch>(fromCommit.Tree, toCommit.Tree);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"git_diff: Failed to compute diff: {ex.Message}", ex);
        }

        foreach (var change in changes)
        {
            var insertions = 0;
            var deletions = 0;

            var patchEntry = patch?.FirstOrDefault(p => p.Path == change.Path);
            if (patchEntry != null)
            {
                insertions = patchEntry.LinesAdded;
                deletions = patchEntry.LinesDeleted;
            }

            // Detect binary: file changed but no line changes reported
            var isBinary = change.Status != ChangeKind.Unmodified &&
                           insertions == 0 && deletions == 0 &&
                           patchEntry != null;

            yield return new GitDiffRow(
                RelativePathToUri(change.Path),
                MapChangeType(change.Status),
                change.OldPath != change.Path ? RelativePathToUri(change.OldPath) : null,
                insertions,
                deletions,
                isBinary
            );
        }
    }

    private static Commit? ResolveCommit(Repository repo, string refName)
    {
        // Try Lookup first - handles SHA, revparse expressions (HEAD~3, HEAD^2), branches, tags
        try
        {
            var commit = repo.Lookup<Commit>(refName);
            if (commit != null)
                return commit;
        }
        catch
        {
            // Lookup failed, try other methods
        }

        // Try branch
        try
        {
            var branch = repo.Branches[refName];
            if (branch != null)
                return branch.Tip;
        }
        catch
        {
            // Branch lookup failed, try tag
        }

        // Try tag
        try
        {
            var tag = repo.Tags[refName];
            if (tag?.PeeledTarget is Commit tagCommit)
                return tagCommit;
        }
        catch
        {
            // Tag lookup failed
        }

        // All resolution methods failed - return null, caller will throw with context
        return null;
    }

    /// <summary>
    /// Converts a git-relative path to a RepoQL file URI.
    /// </summary>
    private static string RelativePathToUri(string relativePath)
    {
        // Normalize to forward slashes for URI
        var normalized = relativePath.Replace('\\', '/');
        return $"file:///{normalized}";
    }

    private static string MapChangeType(ChangeKind status) => status switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Modified => "M",
        ChangeKind.Renamed => "R",
        ChangeKind.Copied => "C",
        ChangeKind.TypeChanged => "T",
        _ => "M"
    };

    public record GitDiffRow(
        string Uri,
        string ChangeType,
        string? OldUri,
        int Insertions,
        int Deletions,
        bool IsBinary
    );
}
