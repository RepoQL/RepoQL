using AwesomeAssertions;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Metrics;

namespace RepoQL.Data.DuckDB.Tests;

public sealed class SingleThreadedDatabaseWriterTests : IAsyncDisposable
{
    private readonly IndexingMetrics _metrics;
    private readonly SingleThreadedDatabaseWriter _writer;
    private readonly TestConnectionFactory _connectionFactory;

    public SingleThreadedDatabaseWriterTests()
    {
        _metrics = new IndexingMetrics();
        _connectionFactory = new TestConnectionFactory();

        var storeFactory = new TestGraphStoreFactory(_metrics);

        _writer = new SingleThreadedDatabaseWriter(
            _connectionFactory,
            storeFactory,
            _metrics,
            NullLogger<SingleThreadedDatabaseWriter>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        _connectionFactory.Dispose();
        _metrics.Dispose();
    }

    [Test]
    [DisplayName("WriteStructureEmbeddings completes without transaction errors when processed in batch")]
    public async Task Given_MultipleWriteStructureEmbeddingsOps_When_ProcessedInBatch_Then_AllSucceed()
    {
        // Arrange - start the writer
        await _writer.StartAsync(CancellationToken.None);

        var docId1 = Guid.NewGuid();
        var nodeId1 = Guid.NewGuid();
        var docId2 = Guid.NewGuid();
        var nodeId2 = Guid.NewGuid();

        var embeddings1 = new List<StructureEmbeddingData>
        {
            new(docId1, nodeId1, "file:///test1.md", new float[] { 0.1f, 0.2f, 0.3f }, "test-model", 3)
        };

        var embeddings2 = new List<StructureEmbeddingData>
        {
            new(docId2, nodeId2, "file:///test2.md", new float[] { 0.4f, 0.5f, 0.6f }, "test-model", 3)
        };

        var op1 = new WriteOperation
        {
            Id = Guid.NewGuid(),
            Type = WriteOperationType.WriteStructureEmbeddings,
            Uri = RepoUri.Parse("mem://structure-embeddings-1"),
            ParsedData = Records.Empty,
            StructureEmbeddings = embeddings1
        };

        var op2 = new WriteOperation
        {
            Id = Guid.NewGuid(),
            Type = WriteOperationType.WriteStructureEmbeddings,
            Uri = RepoUri.Parse("mem://structure-embeddings-2"),
            ParsedData = Records.Empty,
            StructureEmbeddings = embeddings2
        };

        // Act - enqueue both operations and wait for them to complete
        // This will trigger batch processing since both are enqueued quickly
        var result1Task = _writer.EnqueueAndWaitAsync(op1);
        var result2Task = _writer.EnqueueAndWaitAsync(op2);

        var result1 = await result1Task;
        var result2 = await result2Task;

        // Assert - both should succeed without transaction errors
        result1.Success.Should().BeTrue();
        result2.Success.Should().BeTrue();
        result1.Error.Should().BeNull();
        result2.Error.Should().BeNull();

        // Cleanup
        await _writer.StopAsync(CancellationToken.None);
    }

    [Test]
    [DisplayName("WriteStructureEmbeddings completes successfully when processed individually")]
    public async Task Given_SingleWriteStructureEmbeddingsOp_When_Processed_Then_Succeeds()
    {
        // Arrange - start the writer
        await _writer.StartAsync(CancellationToken.None);

        var docId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        var embeddings = new List<StructureEmbeddingData>
        {
            new(docId, nodeId, "file:///test.md", new float[] { 0.1f, 0.2f, 0.3f }, "test-model", 3)
        };

        var op = new WriteOperation
        {
            Id = Guid.NewGuid(),
            Type = WriteOperationType.WriteStructureEmbeddings,
            Uri = RepoUri.Parse("mem://structure-embeddings"),
            ParsedData = Records.Empty,
            StructureEmbeddings = embeddings
        };

        // Act
        var result = await _writer.EnqueueAndWaitAsync(op);

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        // Cleanup
        await _writer.StopAsync(CancellationToken.None);
    }

    [Test]
    [DisplayName("WriteStructureEmbeddings with empty embeddings list completes successfully")]
    public async Task Given_EmptyEmbeddingsList_When_Processed_Then_SucceedsWithNoOp()
    {
        // Arrange - start the writer
        await _writer.StartAsync(CancellationToken.None);

        var op = new WriteOperation
        {
            Id = Guid.NewGuid(),
            Type = WriteOperationType.WriteStructureEmbeddings,
            Uri = RepoUri.Parse("mem://structure-embeddings"),
            ParsedData = Records.Empty,
            StructureEmbeddings = new List<StructureEmbeddingData>()
        };

        // Act
        var result = await _writer.EnqueueAndWaitAsync(op);

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        // Cleanup
        await _writer.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Test factory that creates in-memory DuckDB connections with the required schema.
    /// </summary>
    private sealed class TestConnectionFactory : IDuckDBConnectionFactory, IDisposable
    {
        private readonly List<DuckDBConnection> _connections = new();

        public DuckDBConnection CreateConnection()
        {
            var conn = new DuckDBConnection("Data Source=:memory:");
            conn.Open();

            // Create the document_embedding table schema
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS document_embedding (
                    doc_id UUID NOT NULL,
                    node_id UUID NOT NULL,
                    chunk_index INTEGER NOT NULL DEFAULT 0,
                    embedding_type VARCHAR NOT NULL,
                    uri VARCHAR NOT NULL,
                    scope VARCHAR NOT NULL,
                    model VARCHAR NOT NULL,
                    dim INTEGER NOT NULL,
                    embedding FLOAT[] NOT NULL,
                    start_byte BIGINT,
                    end_byte BIGINT,
                    updated_at TIMESTAMP NOT NULL,
                    PRIMARY KEY (doc_id, node_id, chunk_index, embedding_type)
                )
                """;
            cmd.ExecuteNonQuery();

            _connections.Add(conn);
            return conn;
        }

        public void Dispose()
        {
            foreach (var conn in _connections)
            {
                conn.Dispose();
            }
        }
    }

    /// <summary>
    /// Test factory that creates DuckDbGraphStore instances.
    /// </summary>
    private sealed class TestGraphStoreFactory(IndexingMetrics metrics) : IDuckDbGraphStoreFactory
    {
        public DuckDbGraphStore Create(DuckDBConnection connection, IEnumerable<FormatSqlScript>? formatSchemaScripts = null)
        {
            var store = new DuckDbGraphStore(connection, metrics);
            store.EnsureSchema();
            return store;
        }
    }
}
