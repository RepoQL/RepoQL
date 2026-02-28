using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.Tests;

/// <summary>
/// Tests for BlameHandler. Note: git_blame() computes blame on-demand via LibGit2Sharp,
/// so tests that need actual blame data require a real git repository with commits.
/// </summary>
internal sealed class BlameHandlerTests
{
    [Test]
    public async Task BlameHandler_NoFiles_ReturnsMessage()
    {
        using var context = new BlameTestContext(gitRepo: true);

        var result = await context.Handler.ExecuteAsync(
            documents: [],
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Be("No files matched.");
        result.TotalAvailable.Should().Be(0);
        result.Shown.Should().Be(0);
    }

    [Test]
    public async Task BlameHandler_NonGitRepository_ReturnsError()
    {
        using var context = new BlameTestContext(gitRepo: false);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Example.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Be("Not in a git repository.");
        result.TotalAvailable.Should().Be(0);
        result.Shown.Should().Be(0);
    }

    [Test]
    public async Task BlameHandler_NonFileUri_ReturnsError()
    {
        using var context = new BlameTestContext(gitRepo: true);

        var documents = new[]
        {
            new ReadDocument(
                "help:///quickstart.md",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Be("Blame is only available for file:/// URIs.");
    }

    [Test]
    public async Task BlameHandler_UntrackedFile_ReturnsNoBlameMessage()
    {
        using var context = new BlameTestContext(gitRepo: true);

        // Create a file that exists but is not tracked by git
        var filePath = Path.Combine(context.RepoRoot, "untracked.cs");
        await File.WriteAllTextAsync(filePath, "public class Untracked { }");

        var documents = new[]
        {
            new ReadDocument(
                "file:///untracked.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Contain("No blame available");
    }

    [Test]
    public async Task BlameHandler_CanHandle_ReturnsTrue_ForBlame()
    {
        using var context = new BlameTestContext(gitRepo: true);

        context.Handler.CanHandle("blame").Should().BeTrue();
        context.Handler.CanHandle("BLAME").Should().BeTrue();
        context.Handler.CanHandle("Blame").Should().BeTrue();
    }

    [Test]
    public async Task BlameHandler_CanHandle_ReturnsFalse_ForOtherModifiers()
    {
        using var context = new BlameTestContext(gitRepo: true);

        context.Handler.CanHandle("history").Should().BeFalse();
        context.Handler.CanHandle("headline").Should().BeFalse();
        context.Handler.CanHandle(null).Should().BeFalse();
    }

    [Test]
    public async Task BlameHandler_ModifierName_IsBlame()
    {
        using var context = new BlameTestContext(gitRepo: true);

        context.Handler.ModifierName.Should().Be("blame");
    }

    [Test]
    [Skip("Requires real git repository with commits - run manually for integration testing")]
    public async Task BlameHandler_RealRepo_ReturnsBlameData()
    {
        // This test would need a real git repo with committed files
        // to verify the full blame flow works end-to-end
        await Task.CompletedTask;
    }

    [Test]
    public async Task BlameHandler_ParsesLineRangeFromFragment()
    {
        using var context = new BlameTestContext(gitRepo: true);

        // File with line range fragment - even though file doesn't exist,
        // this tests that the handler parses the URI correctly
        var documents = new[]
        {
            new ReadDocument(
                "file:///src/Example.cs#line=10,20",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        // Should attempt to get blame (and fail because file doesn't exist)
        // but the important thing is it doesn't crash on the fragment parsing
        result.Metadata.FilesConsulted.Should().Contain("file:///src/Example.cs");
    }

    [Test]
    public async Task BlameHandler_MultipleFiles_ConsultsAll()
    {
        using var context = new BlameTestContext(gitRepo: true);

        var documents = new[]
        {
            new ReadDocument(
                "file:///src/A.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null),
            new ReadDocument(
                "file:///src/B.cs",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: null,
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Metadata.FilesConsulted.Should().HaveCount(2);
        result.Metadata.FilesConsulted.Should().Contain("file:///src/A.cs");
        result.Metadata.FilesConsulted.Should().Contain("file:///src/B.cs");
    }

    private sealed class BlameTestContext : IDisposable
    {
        public BlameTestContext(bool gitRepo)
        {
            RepoRoot = Path.Combine(Path.GetTempPath(), $"repoql-blame-{Guid.NewGuid():N}");
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
            services.AddSingleton<ILlmProvider?>(sp => null);
            services.AddSingleton<IMcpToolCaller?>(sp => null);
            var provider = services.BuildServiceProvider();

            Store = new DuckDbDataStore(":memory:", serviceProvider: provider);
            Handler = new BlameHandler(Store, RepoConfig);
        }

        public string RepoRoot { get; }
        public RepositoryConfiguration RepoConfig { get; }
        public DuckDbDataStore Store { get; }
        public BlameHandler Handler { get; }

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
                // Ignore cleanup failures in tests
            }
        }
    }
}
