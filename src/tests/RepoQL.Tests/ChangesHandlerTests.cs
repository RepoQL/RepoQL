using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Inference;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.Tests;

internal sealed class ChangesHandlerTests
{
    [Test]
    [DisplayName("ChangesHandler returns no files matched when documents are empty")]
    public async Task ChangesHandler_NoDocuments_ReturnsNoFilesMatched()
    {
        using var context = new ChangesTestContext(gitRepo: true);

        var result = await context.Handler.ExecuteAsync(
            documents: [],
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Be("No files matched.");
    }

    [Test]
    [DisplayName("ChangesHandler rejects non-file URIs")]
    public async Task ChangesHandler_NonFileUris_ReturnsMessage()
    {
        using var context = new ChangesTestContext(gitRepo: true);

        var result = await context.Handler.ExecuteAsync(
            documents:
            [
                new ReadDocument("help:///quickstart.md", null, "text/plain", null, null, null)
            ],
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Be("Changes is only available for file:/// URIs.");
    }

    [Test]
    [DisplayName("ChangesHandler returns clean message when no matched changes exist")]
    public async Task ChangesHandler_NoChangesInMatchedFiles_ReturnsCleanMessage()
    {
        using var context = new ChangesTestContext(gitRepo: true);
        context.ConfigureGitMacros(
            statuses:
            [
                new StatusSeed("file:///src/Other.cs", "M", " ", "staged", false)
            ],
            patches: []);

        var result = await context.Handler.ExecuteAsync(
            documents:
            [
                new ReadDocument("file:///src/Auth/TokenService.cs", null, "text/plain", null, null, null)
            ],
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Be("No changes in matched files (working copy clean)");
    }

    [Test]
    [DisplayName("ChangesHandler groups staged unstaged and untracked sections")]
    public async Task ChangesHandler_GroupsSections()
    {
        using var context = new ChangesTestContext(gitRepo: true);
        context.ConfigureGitMacros(
            statuses:
            [
                new StatusSeed("file:///src/Auth/TokenService.cs", "M", "M", "staged+modified", false),
                new StatusSeed("file:///src/Auth/AuthMiddleware.cs", "M", " ", "staged", false),
                new StatusSeed("file:///src/Auth/Cache.cs", " ", "M", "modified", false),
                new StatusSeed("file:///src/Auth/NewFile.cs", " ", "?", "untracked", false)
            ],
            patches:
            [
                new PatchSeed("file:///src/Auth/TokenService.cs", "staged", "@@ -1 +1 @@\n-old\n+new", 1, 1, false),
                new PatchSeed("file:///src/Auth/TokenService.cs", "unstaged", "@@ -2 +2 @@\n-old2\n+new2", 1, 1, false),
                new PatchSeed("file:///src/Auth/AuthMiddleware.cs", "staged", "@@ -4 +4 @@\n-old\n+new", 1, 1, false),
                new PatchSeed("file:///src/Auth/Cache.cs", "unstaged", "@@ -7 +7 @@\n-old\n+new", 1, 1, false)
            ]);

        var result = await context.Handler.ExecuteAsync(
            documents:
            [
                new ReadDocument("file:///src/Auth/TokenService.cs", null, "text/plain", null, null, null),
                new ReadDocument("file:///src/Auth/AuthMiddleware.cs", null, "text/plain", null, null, null),
                new ReadDocument("file:///src/Auth/Cache.cs", null, "text/plain", null, null, null),
                new ReadDocument("file:///src/Auth/NewFile.cs", null, "text/plain", null, null, null)
            ],
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        result.Content.Should().Contain("Staged (ready to commit):");
        result.Content.Should().Contain("Unstaged (working copy):");
        result.Content.Should().Contain("Untracked:");
        result.Content.Should().Contain("[2 staged, 2 unstaged, 1 untracked]");
    }

    [Test]
    [DisplayName("ChangesHandler truncates large diffs and marks truncation")]
    public async Task ChangesHandler_TruncatesLargeDiffs()
    {
        using var context = new ChangesTestContext(gitRepo: true);
        var largePatch = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"line {i}"));
        context.ConfigureGitMacros(
            statuses:
            [
                new StatusSeed("file:///src/Auth/TokenService.cs", "M", " ", "staged", false)
            ],
            patches:
            [
                new PatchSeed("file:///src/Auth/TokenService.cs", "staged", largePatch, 200, 50, false)
            ]);

        var result = await context.Handler.ExecuteAsync(
            documents:
            [
                new ReadDocument("file:///src/Auth/TokenService.cs", null, "text/plain", null, null, null)
            ],
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        result.Content.Should().Contain("[diff truncated, +200 -50 lines]");
    }

    [Test]
    [DisplayName("ChangesHandler shows binary marker instead of patch content")]
    public async Task ChangesHandler_BinaryDiff_ShowsBinaryMarker()
    {
        using var context = new ChangesTestContext(gitRepo: true);
        context.ConfigureGitMacros(
            statuses:
            [
                new StatusSeed("file:///src/Auth/TokenService.dll", "M", " ", "staged", false)
            ],
            patches:
            [
                new PatchSeed("file:///src/Auth/TokenService.dll", "staged", "binarydata", 0, 0, true)
            ]);

        var result = await context.Handler.ExecuteAsync(
            documents:
            [
                new ReadDocument("file:///src/Auth/TokenService.dll", null, "application/octet-stream", null, null, null)
            ],
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        result.Content.Should().Contain("[binary]");
        result.Content.Should().NotContain("binarydata");
    }

    private static string Esc(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private readonly record struct StatusSeed(
        string Uri,
        string IndexStatus,
        string WorkTreeStatus,
        string Category,
        bool IsConflicted);

    private readonly record struct PatchSeed(
        string Uri,
        string DiffTarget,
        string Patch,
        int Insertions,
        int Deletions,
        bool IsBinary);

    private sealed class ChangesTestContext : IDisposable
    {
        public ChangesTestContext(bool gitRepo)
        {
            RepoRoot = Path.Combine(Path.GetTempPath(), $"repoql-changes-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RepoRoot);
            if (gitRepo)
            {
                Directory.CreateDirectory(Path.Combine(RepoRoot, ".git"));
            }

            RepoConfig = new RepositoryConfiguration { Path = RepoRoot };
            var services = new ServiceCollection();
            services.AddSingleton(RepoConfig);
            services.AddSingleton<UriRegistry>();
            services.AddSingleton<IEmbeddingProvider?>(sp => null);
            services.AddSingleton<IInferenceProvider?>(sp => null);
            services.AddSingleton<IMcpToolCaller?>(sp => null);
            var provider = services.BuildServiceProvider();

            Store = new DuckDbDataStore(":memory:", serviceProvider: provider);
            Handler = new ChangesHandler(Store, RepoConfig);
            ConfigureGitMacros([], []);
        }

        public string RepoRoot { get; }
        public RepositoryConfiguration RepoConfig { get; }
        public DuckDbDataStore Store { get; }
        public ChangesHandler Handler { get; }

        public void ConfigureGitMacros(IReadOnlyList<StatusSeed> statuses, IReadOnlyList<PatchSeed> patches)
        {
            Store.ExecuteRaw("""
                CREATE OR REPLACE TEMP TABLE test_git_status (
                    uri TEXT,
                    index_status TEXT,
                    work_tree_status TEXT,
                    category TEXT,
                    is_conflicted BOOLEAN
                );
                DELETE FROM test_git_status;
                """);

            foreach (var status in statuses)
            {
                Store.ExecuteRaw($"""
                    INSERT INTO test_git_status (uri, index_status, work_tree_status, category, is_conflicted)
                    VALUES ('{Esc(status.Uri)}', '{Esc(status.IndexStatus)}', '{Esc(status.WorkTreeStatus)}', '{Esc(status.Category)}', {(status.IsConflicted ? "TRUE" : "FALSE")});
                    """);
            }

            Store.ExecuteRaw("""
                CREATE OR REPLACE TEMP TABLE test_git_patches (
                    uri TEXT,
                    diff_target TEXT,
                    patch TEXT,
                    insertions INTEGER,
                    deletions INTEGER,
                    is_binary BOOLEAN
                );
                DELETE FROM test_git_patches;
                """);

            foreach (var patch in patches)
            {
                Store.ExecuteRaw($"""
                    INSERT INTO test_git_patches (uri, diff_target, patch, insertions, deletions, is_binary)
                    VALUES ('{Esc(patch.Uri)}', '{Esc(patch.DiffTarget)}', '{Esc(patch.Patch)}', {patch.Insertions}, {patch.Deletions}, {(patch.IsBinary ? "TRUE" : "FALSE")});
                    """);
            }

            Store.ExecuteRaw("""
                CREATE OR REPLACE MACRO git_status(scope := NULL, include_untracked := TRUE, include_ignored := FALSE) AS TABLE (
                    SELECT uri, index_status, work_tree_status, category, is_conflicted
                    FROM test_git_status
                    WHERE (scope IS NULL OR matches_glob(uri, scope))
                );
                """);

            Store.ExecuteRaw("""
                CREATE OR REPLACE MACRO git_patches(scope := NULL) AS TABLE (
                    SELECT uri, diff_target, patch, insertions, deletions, is_binary
                    FROM test_git_patches
                    WHERE (scope IS NULL OR matches_glob(uri, scope))
                );
                """);
        }

        public void Dispose()
        {
            Store.Dispose();
            try
            {
                if (Directory.Exists(RepoRoot))
                    Directory.Delete(RepoRoot, recursive: true);
            }
            catch
            {
            }
        }
    }
}
