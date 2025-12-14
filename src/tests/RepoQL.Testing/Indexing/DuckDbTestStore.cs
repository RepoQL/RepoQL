using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
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

        var metrics = new IndexingMetrics();
        var dataStore = new DuckDbDataStore(
            path: tempPath,
            embeddingProvider: null,
            formatSchemaScripts: null,
            logger: NullLogger<DuckDbDataStore>.Instance);

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
}
