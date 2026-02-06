using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB.Tests;

/// <summary>
/// Extension methods for setting up DI in tests with all UDF dependencies.
/// </summary>
public static class TestServiceCollectionExtensions
{
    /// <summary>
    /// Adds all dependencies required by UDFs for testing.
    /// Uses test/mock implementations that don't require external services.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="repoPath">Optional repository path for git UDFs. Defaults to current directory.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTestUdfDependencies(
        this IServiceCollection services,
        string? repoPath = null)
    {
        // Repository configuration for git UDFs
        services.AddSingleton(new RepositoryConfiguration
        {
            Path = repoPath ?? Environment.CurrentDirectory
        });

        // URI Registry for pattern matching UDFs
        services.AddSingleton<UriRegistry>();

        // Embedding provider (disabled for most tests)
        services.AddSingleton<IEmbeddingProvider>(new DisabledEmbeddingProvider());

        // LLM provider (disabled for tests)
        services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());

        // MCP tool caller (not available in tests)
        services.AddSingleton<IMcpToolCaller?>(_ => null);

        return services;
    }

    /// <summary>
    /// Creates a DuckDbDataStore configured for testing with all UDF dependencies.
    /// </summary>
    public static DuckDbDataStore CreateTestDataStore(string? repoPath = null, string? databasePath = null)
    {
        var services = new ServiceCollection();
        services.AddTestUdfDependencies(repoPath);
        var serviceProvider = services.BuildServiceProvider();
        return new DuckDbDataStore(path: databasePath, serviceProvider: serviceProvider);
    }

    private class DisabledEmbeddingProvider : IEmbeddingProvider
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

    private class DisabledLlmProvider : ILlmProvider
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
}
