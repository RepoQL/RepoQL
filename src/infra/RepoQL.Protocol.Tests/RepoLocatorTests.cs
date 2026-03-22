namespace RepoQL.Protocol.Tests;

public class RepoLocatorTests
{
    private static readonly object EnvironmentLock = new();

    [Test]
    public void TryFindRepoRoot_FindsNearestMarker()
    {
        using var temp = new TempDir();
        var markerRoot = Path.Combine(temp.Path, "repo");
        var nested = Path.Combine(markerRoot, "nested", "child");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(markerRoot, ".git"), string.Empty);

        var found = RepoQL.Contracts.RepoLocator.TryFindRepoRoot(
            nested,
            out var root,
            out var searchedFrom,
            allowFallback: false);

        found.Should().BeTrue();
        root.Should().Be(Path.GetFullPath(markerRoot));
        searchedFrom.Should().Be(Path.GetFullPath(nested));
    }

    [Test]
    public void FindRepoRoot_UsesPwd_WhenImplicitCwdUnsuitableAndPwdIsRepo()
    {
        using var temp = new TempDir();
        var unsuitableCwd = Path.Combine(temp.Path, "outside", "work");
        var repoRoot = Path.Combine(temp.Path, "project");
        var repoChild = Path.Combine(repoRoot, "src");
        Directory.CreateDirectory(unsuitableCwd);
        Directory.CreateDirectory(repoChild);
        File.WriteAllText(Path.Combine(repoRoot, ".git"), string.Empty);

        WithCurrentDirectoryAndPwd(unsuitableCwd, repoChild, () =>
        {
            var found = RepoQL.Contracts.RepoLocator.FindRepoRoot();

            found.Should().Be(Path.GetFullPath(repoRoot));
        });
    }

    [Test]
    public void TryFindRepoRoot_UsesPwd_WhenImplicitLookupFails()
    {
        using var temp = new TempDir();
        var unsuitableCwd = Path.Combine(temp.Path, "outside", "work");
        var repoRoot = Path.Combine(temp.Path, "project");
        var repoChild = Path.Combine(repoRoot, "src");
        Directory.CreateDirectory(unsuitableCwd);
        Directory.CreateDirectory(repoChild);
        File.WriteAllText(Path.Combine(repoRoot, ".git"), string.Empty);

        WithCurrentDirectoryAndPwd(unsuitableCwd, repoChild, () =>
        {
            var found = RepoQL.Contracts.RepoLocator.TryFindRepoRoot(
                startPath: null,
                out var root,
                out var searchedFrom,
                allowFallback: false);

            found.Should().BeTrue();
            root.Should().Be(Path.GetFullPath(repoRoot));
            searchedFrom.Should().Be(Path.GetFullPath(unsuitableCwd));
        });
    }

    [Test]
    public void TryFindRepoRoot_DoesNotUsePwd_ForExplicitUnsuitableStartPath()
    {
        using var temp = new TempDir();
        var currentDir = Path.Combine(temp.Path, "current");
        var explicitStart = Path.Combine(temp.Path, "explicit");
        var repoRoot = Path.Combine(temp.Path, "project");
        var repoChild = Path.Combine(repoRoot, "src");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(explicitStart);
        Directory.CreateDirectory(repoChild);
        File.WriteAllText(Path.Combine(repoRoot, ".git"), string.Empty);

        var withPwdFound = false;
        string? withPwdRoot = null;
        string? withPwdSearchedFrom = null;
        WithCurrentDirectoryAndPwd(currentDir, repoChild, () =>
        {
            withPwdFound = RepoQL.Contracts.RepoLocator.TryFindRepoRoot(
                explicitStart,
                out withPwdRoot,
                out withPwdSearchedFrom,
                allowFallback: false);
        });

        var withoutPwdFound = false;
        string? withoutPwdRoot = null;
        string? withoutPwdSearchedFrom = null;
        WithCurrentDirectoryAndPwd(currentDir, null, () =>
        {
            withoutPwdFound = RepoQL.Contracts.RepoLocator.TryFindRepoRoot(
                explicitStart,
                out withoutPwdRoot,
                out withoutPwdSearchedFrom,
                allowFallback: false);
        });

        withPwdFound.Should().Be(withoutPwdFound);
        withPwdRoot.Should().Be(withoutPwdRoot);
        withPwdSearchedFrom.Should().Be(Path.GetFullPath(explicitStart));
        withoutPwdSearchedFrom.Should().Be(Path.GetFullPath(explicitStart));
    }

    [Test]
    public void FindRepoRoot_KeepsExistingFallback_WhenPwdUnsuitable()
    {
        using var temp = new TempDir();
        var unsuitableCwd = Path.Combine(temp.Path, "outside", "work");
        var unsuitablePwd = Path.Combine(temp.Path, "also-unsuitable");
        Directory.CreateDirectory(unsuitableCwd);
        Directory.CreateDirectory(unsuitablePwd);

        string? expectedFallback = null;
        WithCurrentDirectoryAndPwd(unsuitableCwd, null, () =>
        {
            expectedFallback = RepoQL.Contracts.RepoLocator.FindRepoRoot();
        });

        WithCurrentDirectoryAndPwd(unsuitableCwd, unsuitablePwd, () =>
        {
            var found = RepoQL.Contracts.RepoLocator.FindRepoRoot();

            found.Should().Be(expectedFallback);
        });
    }

    private static void WithCurrentDirectoryAndPwd(string currentDirectory, string? pwd, Action action)
    {
        lock (EnvironmentLock)
        {
            var originalCurrentDirectory = Environment.CurrentDirectory;
            var originalPwd = Environment.GetEnvironmentVariable("PWD");

            try
            {
                Environment.CurrentDirectory = currentDirectory;
                Environment.SetEnvironmentVariable("PWD", pwd);
                action();
            }
            finally
            {
                Environment.CurrentDirectory = originalCurrentDirectory;
                Environment.SetEnvironmentVariable("PWD", originalPwd);
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            var basePath = System.IO.Path.GetTempPath();
            if (string.IsNullOrWhiteSpace(basePath))
            {
                basePath = Environment.CurrentDirectory;
            }

            Path = System.IO.Path.Combine(basePath, $"repoql-protocol-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
