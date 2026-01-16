using AwesomeAssertions;
using LibGit2Sharp;
using RepoQL.Contracts;
using TUnit.Core;

namespace RepoQL.Data.DuckDB.Tests;

public class GitUdfTests
{
    [Test]
    public void ResolveCommit_WithHeadTilde_ReturnsCorrectCommit()
    {
        // Find the actual RepoQL repo root by walking up from assembly location
        var testDir = Path.GetDirectoryName(typeof(GitUdfTests).Assembly.Location)!;
        var repoRoot = FindGitRoot(testDir) ?? RepoLocator.FindRepoRoot();

        Console.WriteLine($"TestDir: {testDir}");
        Console.WriteLine($"RepoRoot: {repoRoot}");
        Console.WriteLine($"IsValid: {Repository.IsValid(repoRoot)}");

        // Skip test if not in a git repo
        if (!Repository.IsValid(repoRoot))
        {
            Console.WriteLine("Not a valid git repository, skipping");
            return;
        }

        using var repo = new Repository(repoRoot);

        // Get HEAD commit
        var headCommit = repo.Head.Tip;
        Console.WriteLine($"HEAD: {headCommit.Sha}");

        // Try Lookup<Commit> for HEAD~1
        var lookup = repo.Lookup<Commit>("HEAD~1");
        Console.WriteLine($"HEAD~1 via Lookup<Commit>: {lookup?.Sha}");

        // Skip if shallow clone (CI often does shallow checkout)
        if (lookup is null)
        {
            Console.WriteLine("HEAD~1 not available - likely shallow clone, skipping");
            return;
        }

        lookup.Sha.Should().NotBe(headCommit.Sha, "HEAD~1 should be different from HEAD");

        // Verify HEAD~3 also works (may not be available in shallow clone)
        var head3 = repo.Lookup<Commit>("HEAD~3");
        Console.WriteLine($"HEAD~3 via Lookup<Commit>: {head3?.Sha}");
        if (head3 is not null)
        {
            head3.Sha.Should().NotBe(headCommit.Sha, "HEAD~3 should be different from HEAD");
        }
    }

    private static string? FindGitRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
