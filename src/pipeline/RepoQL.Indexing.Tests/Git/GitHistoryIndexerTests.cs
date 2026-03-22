using AwesomeAssertions;
using LibGit2Sharp;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Git;

namespace RepoQL.Indexing.Tests.Git;

internal sealed class GitHistoryIndexerTests
{
    [Test]
    public async Task IndexAsync_UsesProvidedSourceUriPrefix()
    {
        var repoRoot = CreateTempGitRepository();
        using var store = new DuckDbDataStore(":memory:");
        var indexer = new GitHistoryIndexer(store);

        await indexer.IndexAsync(repoRoot, "github://owner/repo", CancellationToken.None);

        var uris = store.Read(
            "SELECT uri FROM git_file_change ORDER BY uri",
            r => r.GetString(0));

        uris.Should().NotBeEmpty();
        uris.Should().AllSatisfy(uri => uri.Should().StartWith("github://owner/repo/"));

        CleanupDirectory(repoRoot);
    }

    [Test]
    public async Task IndexAsync_DefaultSource_UsesCanonicalFileUriPrefix()
    {
        var repoRoot = CreateTempGitRepository();
        using var store = new DuckDbDataStore(":memory:");
        var indexer = new GitHistoryIndexer(store);

        await indexer.IndexAsync(repoRoot, CancellationToken.None);

        var uris = store.Read(
            "SELECT uri FROM git_file_change ORDER BY uri",
            r => r.GetString(0));

        uris.Should().NotBeEmpty();
        uris.Should().AllSatisfy(uri => uri.Should().StartWith("file:///"));

        CleanupDirectory(repoRoot);
    }

    [Test]
    public async Task IndexAsync_FullReindex_ClearsOnlyMatchingSource()
    {
        var repoRoot = CreateTempGitRepository();
        using var store = new DuckDbDataStore(":memory:");
        var indexer = new GitHistoryIndexer(store);

        await indexer.IndexAsync(repoRoot, "github://owner/repo-a", CancellationToken.None);
        await indexer.IndexAsync(repoRoot, "github://owner/repo-b", CancellationToken.None);
        await indexer.IndexAsync(repoRoot, "github://owner/repo-a", CancellationToken.None);

        var sourceARows = store.ReadScalar<int>(
            "SELECT COUNT(*) FROM git_file_change WHERE uri LIKE 'github://owner/repo-a/%'");
        var sourceBRows = store.ReadScalar<int>(
            "SELECT COUNT(*) FROM git_file_change WHERE uri LIKE 'github://owner/repo-b/%'");

        sourceARows.Should().BeGreaterThan(0);
        sourceBRows.Should().BeGreaterThan(0);

        CleanupDirectory(repoRoot);
    }

    [Test]
    public async Task IndexIncrementalAsync_IsScopedPerSource()
    {
        var repoRoot = CreateTempGitRepository();
        using var repo = new Repository(repoRoot);
        using var store = new DuckDbDataStore(":memory:");
        var indexer = new GitHistoryIndexer(store);

        await indexer.IndexAsync(repoRoot, "github://owner/repo-a", CancellationToken.None);

        AddCommit(repoRoot, repo, "second.txt", "second", "second commit");
        await indexer.IndexIncrementalAsync(repoRoot, "github://owner/repo-a", CancellationToken.None);
        await indexer.IndexIncrementalAsync(repoRoot, "github://owner/repo-b", CancellationToken.None);

        var sourceACommits = store.ReadScalar<int>(
            """
            SELECT COUNT(DISTINCT c.hash)
            FROM git_commit c
            JOIN git_file_change fc ON fc.commit_hash = c.hash
            WHERE fc.uri LIKE 'github://owner/repo-a/%'
            """);
        var sourceBCommits = store.ReadScalar<int>(
            """
            SELECT COUNT(DISTINCT c.hash)
            FROM git_commit c
            JOIN git_file_change fc ON fc.commit_hash = c.hash
            WHERE fc.uri LIKE 'github://owner/repo-b/%'
            """);

        sourceACommits.Should().BeGreaterThan(1);
        sourceBCommits.Should().BeGreaterThan(1);

        CleanupDirectory(repoRoot);
    }

    private static string CreateTempGitRepository()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), $"repoql-git-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoRoot);
        Repository.Init(repoRoot);
        using var repo = new Repository(repoRoot);
        AddCommit(repoRoot, repo, "first.txt", "first", "first commit");
        return repoRoot;
    }

    private static void AddCommit(
        string repoRoot,
        Repository repo,
        string fileName,
        string content,
        string message)
    {
        var filePath = Path.Combine(repoRoot, fileName);
        File.WriteAllText(filePath, content);
        Commands.Stage(repo, fileName);

        var now = DateTimeOffset.UtcNow;
        var signature = new Signature("RepoQL Test", "repoql-tests@example.com", now);
        repo.Commit(message, signature, signature);
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for temp test repositories.
        }
    }
}
