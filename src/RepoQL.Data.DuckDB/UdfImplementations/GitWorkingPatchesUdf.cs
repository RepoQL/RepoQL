using LibGit2Sharp;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for on-demand git working copy patches.
///
/// Purpose: Returns unified patch text and line stats for staged and unstaged
/// working copy changes without requiring history indexing.
///
/// Complexity: Uses LibGit2Sharp patch comparison for two diff targets
/// (HEAD->index and index->working directory) and normalizes file paths to URIs.
/// </summary>
[UdfClass]
public class GitWorkingPatchesUdf(RepositoryConfiguration repoConfig)
{
    private readonly string _repoRoot = repoConfig.Path;

    /// <summary>
    /// Returns staged and unstaged working copy patches.
    /// </summary>
    [StructuredUdf("_git_working_patches_internal", Description = "Returns staged and unstaged working copy patches")]
    public IEnumerable<GitWorkingPatchRow> WorkingPatches(
        [UdfDefault("'true'")] bool includeUnstaged)
    {
        if (!Repository.IsValid(_repoRoot))
            yield break; // Not a git repo - return empty results

        using var repo = new Repository(_repoRoot);

        Patch stagedPatch;
        try
        {
            stagedPatch = repo.Diff.Compare<Patch>(repo.Head?.Tip?.Tree, DiffTargets.Index);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"git_patches: Failed to compute staged patches: {ex.Message}", ex);
        }

        foreach (var entry in stagedPatch)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;

            yield return new GitWorkingPatchRow(
                RelativePathToUri(entry.Path),
                "staged",
                entry.Patch ?? string.Empty,
                entry.LinesAdded,
                entry.LinesDeleted,
                entry.IsBinaryComparison
            );
        }

        if (!includeUnstaged)
            yield break;

        Patch unstagedPatch;
        try
        {
            unstagedPatch = repo.Diff.Compare<Patch>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"git_patches: Failed to compute unstaged patches: {ex.Message}", ex);
        }

        foreach (var entry in unstagedPatch)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;

            yield return new GitWorkingPatchRow(
                RelativePathToUri(entry.Path),
                "unstaged",
                entry.Patch ?? string.Empty,
                entry.LinesAdded,
                entry.LinesDeleted,
                entry.IsBinaryComparison
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

    public record GitWorkingPatchRow(
        string Uri,
        string DiffTarget,
        string Patch,
        int Insertions,
        int Deletions,
        bool IsBinary
    );
}
