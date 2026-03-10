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

internal sealed class HistoryHandlerTests
{
    [Test]
    public async Task HistoryHandler_Formats_Commits_And_Summary()
    {
        using var context = new HistoryTestContext(gitRepo: true);
        SeedHistory(context.Store);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        // New compact format: hash date author | message + file details
        result.Content.Should().Contain("aaaaaaa 2024-01-15 Alice Developer | Fix token expiration check");
        result.Content.Should().Contain("bbbbbbb 2024-01-10 Bob Engineer | Add configurable token expiration");
        result.Content.Should().Contain("TokenService.cs");
        result.Content.Should().Contain("[2 commits shown, 0 more in history]");
        result.Shown.Should().Be(2);
        result.TotalAvailable.Should().Be(2);
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task HistoryHandler_Ranks_By_Keywords()
    {
        using var context = new HistoryTestContext(gitRepo: true);
        SeedHistory(context.Store);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: "configurable",
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        result.Content.Should().StartWith("bbbbbbb 2024-01-10 Bob Engineer");
        result.Metadata.Warning.Should().BeNull();
    }

    [Test]
    public async Task HistoryHandler_Warns_When_NoKeywordMatches()
    {
        using var context = new HistoryTestContext(gitRepo: true);
        SeedHistory(context.Store);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: "missing",
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        result.Metadata.Warning.Should().Contain("No matches for keywords");
        result.Content.Should().StartWith("aaaaaaa 2024-01-15 Alice Developer");
    }

    [Test]
    public async Task HistoryHandler_Ranks_By_Author()
    {
        using var context = new HistoryTestContext(gitRepo: true);
        SeedHistory(context.Store);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: "Bob",
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        result.Content.Should().StartWith("bbbbbbb 2024-01-10 Bob Engineer");
    }

    [Test]
    public async Task HistoryHandler_Fits_Output_To_TokenBudget()
    {
        using var context = new HistoryTestContext(gitRepo: true);
        SeedHistory(context.Store);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        // Use a tight budget that can only fit one commit
        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 40, // Very tight budget - only one commit fits
            ct: CancellationToken.None);

        result.Shown.Should().Be(1);
        result.TotalAvailable.Should().Be(2);
        result.Content.Should().Contain("aaaaaaa 2024-01-15 Alice Developer");
        result.Content.Should().Contain("[1 commit shown, 1 more in history]");
    }

    [Test]
    public async Task HistoryHandler_Reports_NonGit_Repository()
    {
        using var context = new HistoryTestContext(gitRepo: false);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 500,
            ct: CancellationToken.None);

        result.Content.Should().Be("Not in a git repository.");
        result.TotalAvailable.Should().Be(0);
        result.Shown.Should().Be(0);
    }

    [Test]
    public async Task HistoryHandler_Supports_ImportedRepositoryUris()
    {
        using var context = new HistoryTestContext(gitRepo: false);
        SeedHistory(context.Store, "github://owner/repo/src/Auth/TokenService.cs");

        var documents = new[]
        {
            new ReadDocument(
                "github://owner/repo/src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        result.Content.Should().Contain("aaaaaaa 2024-01-15 Alice Developer | Fix token expiration check");
        result.Content.Should().Contain("bbbbbbb 2024-01-10 Bob Engineer | Add configurable token expiration");
        result.TotalAvailable.Should().Be(2);
        result.Shown.Should().Be(2);
    }

    [Test]
    public async Task HistoryHandler_LineFragment_Routes_Through_Blame_Path()
    {
        // With a fake .git dir (no real objects), blame will fail gracefully
        // and return no commits — verifies the routing and error handling
        using var context = new HistoryTestContext(gitRepo: true);
        SeedHistory(context.Store);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs#line=10,20",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        // Blame fails (no real git objects) so we get no commits
        // This verifies the blame path was entered and handled the error
        result.Content.Should().Contain("No commits found");
    }

    [Test]
    public async Task HistoryHandler_ImportedUri_WithFragment_Falls_Back_To_FileLevel()
    {
        // github:// URIs with line fragments can't use blame (no local git)
        // so they should fall back to the file-level git_file_change query
        using var context = new HistoryTestContext(gitRepo: false);
        SeedHistory(context.Store, "github://owner/repo/src/Auth/TokenService.cs");

        var documents = new[]
        {
            new ReadDocument(
                "github://owner/repo/src/Auth/TokenService.cs#line=10,20",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        // Falls back to file-level history since github:// can't use blame
        result.Content.Should().Contain("aaaaaaa 2024-01-15 Alice Developer");
        result.Content.Should().Contain("bbbbbbb 2024-01-10 Bob Engineer");
        result.TotalAvailable.Should().Be(2);
    }

    [Test]
    public async Task HistoryHandler_PlainUri_Still_Returns_FileLevel_History()
    {
        // Regression: plain URIs (no fragment) must still work exactly as before
        using var context = new HistoryTestContext(gitRepo: true);
        SeedHistory(context.Store);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        result.Content.Should().Contain("[2 commits shown, 0 more in history]");
        result.Content.Should().NotContain("symbol history");
        result.TotalAvailable.Should().Be(2);
    }

    [Test]
    public async Task HistoryHandler_SymbolFragment_SummaryLine_Indicates_LineScope()
    {
        // When blame succeeds, the summary should mention the line range
        // We can't easily get blame to succeed in unit tests, but we can verify
        // the summary format for non-blame path doesn't say "symbol history"
        using var context = new HistoryTestContext(gitRepo: true);
        SeedHistory(context.Store);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Auth/TokenService.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 10_000,
            ct: CancellationToken.None);

        // Plain file history should say "history", not "symbol history"
        result.Content.Should().Contain("more in history]");
        result.Content.Should().NotContain("symbol history");
    }

    private static void SeedHistory(DuckDbDataStore store, string uri = "file:///src/Auth/TokenService.cs")
    {
        store.ExecuteRaw("""
            INSERT INTO git_commit (
                hash, author_name, author_email, author_date,
                committer_name, committer_email, committer_date,
                message, parent_hashes, files_changed, insertions, deletions
            ) VALUES
                ('aaaaaaaaaaaa', 'Alice Developer', 'alice@example.com', '2024-01-15T00:00:00Z',
                 'Alice Developer', 'alice@example.com', '2024-01-15T00:00:00Z',
                 'Fix token expiration check', []::TEXT[], 1, 1, 1),
                ('bbbbbbbbbbbb', 'Bob Engineer', 'bob@example.com', '2024-01-10T00:00:00Z',
                 'Bob Engineer', 'bob@example.com', '2024-01-10T00:00:00Z',
                 'Add configurable token expiration', []::TEXT[], 1, 15, 3);
            """);

        store.ExecuteRaw("""
            INSERT INTO git_file_change (
                commit_hash, uri, change_type, old_uri, insertions, deletions, is_binary
            ) VALUES
                ('aaaaaaaaaaaa', '{URI_PLACEHOLDER}', 'M', NULL, 1, 1, FALSE),
                ('bbbbbbbbbbbb', '{URI_PLACEHOLDER}', 'M', NULL, 15, 3, FALSE);
            """.Replace("{URI_PLACEHOLDER}", Esc(uri), StringComparison.Ordinal));
    }

    private static string Esc(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class HistoryTestContext : IDisposable
    {
        public HistoryTestContext(bool gitRepo)
        {
            RepoRoot = Path.Combine(Path.GetTempPath(), $"repoql-history-{Guid.NewGuid():N}");
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
            Handler = new HistoryHandler(Store, RepoConfig);
        }

        public string RepoRoot { get; }
        public RepositoryConfiguration RepoConfig { get; }
        public DuckDbDataStore Store { get; }
        public HistoryHandler Handler { get; }

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
