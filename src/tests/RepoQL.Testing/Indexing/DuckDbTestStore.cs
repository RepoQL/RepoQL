using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Metrics;
using System.Text.Json.Nodes;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Testing.Indexing;

/// <summary>
/// Provides a DuckDB store pre-wired with RepoQL schema for integration tests.
/// Uses a temp file to support file-based operations.
/// </summary>
public sealed class DuckDbTestStore : IDisposable
{
    public DuckDbDataStore DataStore { get; }
    public IndexingMetrics Metrics { get; }

    private readonly string? _tempDbPath;

    private DuckDbTestStore(DuckDbDataStore dataStore, IndexingMetrics metrics, string? tempDbPath)
    {
        DataStore = dataStore;
        Metrics = metrics;
        _tempDbPath = tempDbPath;
    }

    public static DuckDbTestStore CreateInMemory()
    {
        // Use a temp file to support file-based operations
        var tempPath = Path.Combine(Path.GetTempPath(), $"repoql-test-{Guid.NewGuid():N}.duckdb");

        // Build service provider with all UDF dependencies
        var services = new ServiceCollection();
        services.AddSingleton(new RepositoryConfiguration
        {
            Path = Environment.CurrentDirectory
        });
        services.AddSingleton<UriRegistry>();
        services.AddSingleton<IEmbeddingProvider>(new DisabledEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        var serviceProvider = services.BuildServiceProvider();

        var metrics = new IndexingMetrics();
        var dataStore = new DuckDbDataStore(
            path: tempPath,
            embeddingProvider: null,
            formatSchemaScripts: null,
            logger: NullLogger<DuckDbDataStore>.Instance,
            serviceProvider: serviceProvider);

        // Force schema initialization by performing a read
        _ = dataStore.GetAllNodes();

        return new DuckDbTestStore(dataStore, metrics, tempPath);
    }

    public RepoUri SeedDocument(string uri, string mediaType = "text/plain", string text = "seed")
    {
        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = Guid.NewGuid().ToString("N"),
            Size = text.Length,
            MediaType = SemanticMediaType.Parse(mediaType),
            Text = text
        };

        var docNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.Parse(uri),
            ArtifactId = artifact.Id,
            Props = new JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        DataStore.IndexArtifact(new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = docNode
        });

        return docNode.Uri!;
    }

    public void Dispose()
    {
        DataStore.Dispose();
        Metrics.Dispose();

        // Clean up temp file
        if (_tempDbPath is not null && File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
            // Also try to delete the WAL file
            try { File.Delete(_tempDbPath + ".wal"); } catch { }
        }
    }

    /// <summary>
    /// Disabled embedding provider for tests.
    /// </summary>
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

    /// <summary>
    /// Disabled LLM provider for tests.
    /// </summary>
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
