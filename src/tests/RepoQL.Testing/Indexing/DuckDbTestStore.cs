using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Metrics;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Testing.Indexing;

/// <summary>
/// Provides an in-memory DuckDB store pre-wired with RepoQL schema for integration tests.
/// </summary>
public sealed class DuckDbTestStore : IDisposable
{
    public DuckDBConnection Connection { get; }
    public DuckDbGraphStore GraphStore { get; }
    public IndexingMetrics Metrics { get; }

    private DuckDbTestStore(DuckDBConnection connection, DuckDbGraphStore graphStore, IndexingMetrics metrics)
    {
        Connection = connection;
        GraphStore = graphStore;
        Metrics = metrics;
    }

    public static DuckDbTestStore CreateInMemory()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();

        var metrics = new IndexingMetrics();
        RepositoryUserDefinedFunctions.RegisterAll(connection, null);

        var graph = new DuckDbGraphStore(
            connection,
            metrics: metrics,
            enableExtensions: false,
            registerUdfs: false,
            logger: NullLogger<DuckDbGraphStore>.Instance);
        graph.EnsureSchema();

        return new DuckDbTestStore(connection, graph, metrics);
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
        GraphStore.UpsertArtifact(artifact);

        var docNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.Parse(uri),
            ArtifactId = artifact.Id,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var saved = GraphStore.UpsertDocumentByUri(docNode.Uri!, docNode);
        GraphStore.ReplaceDocumentContent(saved.Id, Array.Empty<Node>(), Array.Empty<Span>(), Array.Empty<Edge>());
        return docNode.Uri!;
    }

    public void Dispose()
    {
        GraphStore.Dispose();
        Connection.Dispose();
    }
}
