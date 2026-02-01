using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.Tests;

/// <summary>
/// Tests for FindHandler semantic search within matched files.
/// Note: Full semantic search requires embeddings infrastructure which
/// may not be available in test context.
/// </summary>
internal sealed class FindHandlerTests
{
    [Test]
    public async Task FindHandler_NoKeywords_ReturnsError()
    {
        using var context = new FindTestContext();

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

        result.Content.Should().Contain("Missing search keywords");
        result.Content.Should().Contain("Usage:");
    }

    [Test]
    public async Task FindHandler_EmptyKeywords_ReturnsError()
    {
        using var context = new FindTestContext();

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
            parameter: "   ",
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Contain("Missing search keywords");
    }

    [Test]
    public async Task FindHandler_NoFiles_ReturnsError()
    {
        using var context = new FindTestContext();

        var result = await context.Handler.ExecuteAsync(
            documents: [],
            parameter: "authentication",
            tokenBudget: 1000,
            ct: CancellationToken.None);

        result.Content.Should().Be("No files matched pattern.");
    }

    [Test]
    public async Task FindHandler_NonFileUri_IsAccepted()
    {
        using var context = new FindTestContext();

        var documents = new[]
        {
            new ReadDocument(
                "repoql-docs:///quickstart.md",
                TextContent: null,
                MediaType: "text/plain",
                Headline: null,
                Summary: null,
                Structure: null)
        };

        var result = await context.Handler.ExecuteAsync(
            documents,
            parameter: "authentication",
            tokenBudget: 1000,
            ct: CancellationToken.None);

        // Should not reject based on URI scheme - will search (may find no matches without full infrastructure)
        result.Content.Should().NotContain("only available for file:/// URIs");
    }

    [Test]
    public async Task FindHandler_CanHandle_ReturnsTrue_ForFind()
    {
        using var context = new FindTestContext();

        context.Handler.CanHandle("find").Should().BeTrue();
        context.Handler.CanHandle("FIND").Should().BeTrue();
        context.Handler.CanHandle("Find").Should().BeTrue();
    }

    [Test]
    public async Task FindHandler_CanHandle_ReturnsFalse_ForOtherModifiers()
    {
        using var context = new FindTestContext();

        context.Handler.CanHandle("blame").Should().BeFalse();
        context.Handler.CanHandle("history").Should().BeFalse();
        context.Handler.CanHandle(null).Should().BeFalse();
    }

    [Test]
    public async Task FindHandler_ModifierName_IsFind()
    {
        using var context = new FindTestContext();

        context.Handler.ModifierName.Should().Be("find");
    }

    [Test]
    [Skip("Requires full schema with _search_candidates macro - run integration tests instead")]
    public async Task FindHandler_NoMatches_ReturnsNoMatchesMessage()
    {
        // This test requires the full search infrastructure
        await Task.CompletedTask;
    }

    [Test]
    [Skip("Requires full schema with _search_candidates macro - run integration tests instead")]
    public async Task FindHandler_ExtractsContainerUriFromFragment()
    {
        // This test requires the full search infrastructure
        await Task.CompletedTask;
    }

    [Test]
    [Skip("Requires full schema with _search_candidates macro - run integration tests instead")]
    public async Task FindHandler_MultipleFiles_ConsultsAll()
    {
        // This test requires the full search infrastructure
        await Task.CompletedTask;
    }

    [Test]
    [Skip("Requires indexed database with embeddings - run manually for integration testing")]
    public async Task FindHandler_RealSearch_ReturnsResults()
    {
        // This test would need a real indexed database with embeddings
        await Task.CompletedTask;
    }

    private sealed class FindTestContext : IDisposable
    {
        public FindTestContext()
        {
            RepoConfig = new RepositoryConfiguration { Path = Path.GetTempPath() };
            var services = new ServiceCollection();
            services.AddSingleton(RepoConfig);
            services.AddSingleton<UriRegistry>();
            services.AddSingleton<IEmbeddingProvider>(new DisabledEmbeddingProvider());
            services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
            services.AddSingleton<IMcpToolCaller?>(_ => null);
            var provider = services.BuildServiceProvider();

            Store = new DuckDbDataStore(":memory:", serviceProvider: provider);
            Handler = new FindHandler(Store);
        }

        private sealed class DisabledEmbeddingProvider : IEmbeddingProvider
        {
            public bool Enabled => false;
            public string Model => "disabled";
            public int Dimension => 384;

            public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default)
                => Task.FromResult<float[]?>(null);

            public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default)
                => Task.FromResult<float[]?>(null);

            public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

            public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

            public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
                => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        }

        private sealed class DisabledLlmProvider : ILlmProvider
        {
            public bool Enabled => false;
            public string Model => "disabled";

            public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
                => Task.FromResult("LLM disabled in tests");

            public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
                => Task.FromResult(new LlmSummaryResult("LLM disabled in tests"));

            public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
                => Task.FromResult("LLM disabled in tests");

            public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
                => Task.FromResult(string.Empty);
        }

        public RepositoryConfiguration RepoConfig { get; }
        public DuckDbDataStore Store { get; }
        public FindHandler Handler { get; }

        public void Dispose()
        {
            Store.Dispose();
        }
    }
}
