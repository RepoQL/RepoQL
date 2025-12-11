using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Unified DuckDB-backed database for RepoQL. Handles both reads and writes
/// with internal locking to ensure single-writer semantics.
/// </summary>
/// <remarks>
/// <para>
/// For file-based databases: uses two connections (read-only + read-write) with a
/// <see cref="ReaderWriterLockSlim"/> for concurrency control.
/// </para>
/// <para>
/// For in-memory databases (null path): uses a single connection since each
/// :memory: connection creates a separate database.
/// </para>
/// </remarks>
public sealed class DuckDbRepoDatabase : IRepoDatabase
{
    #region Metrics

    private static readonly Meter Meter = new("RepoQL.Database", "1.0.0");

    // Counters - track totals over time
    private static readonly Counter<long> DocumentsIndexed = Meter.CreateCounter<long>(
        "repoql.db.documents.indexed",
        unit: "documents",
        description: "Documents indexed (insert or update)");

    private static readonly Counter<long> DocumentsDeleted = Meter.CreateCounter<long>(
        "repoql.db.documents.deleted",
        unit: "documents",
        description: "Documents deleted from index");

    private static readonly Counter<long> AnnotationsWritten = Meter.CreateCounter<long>(
        "repoql.db.annotations.written",
        unit: "annotations",
        description: "Annotations written to database");

    private static readonly Counter<long> EmbeddingsWritten = Meter.CreateCounter<long>(
        "repoql.db.embeddings.written",
        unit: "embeddings",
        description: "Embeddings written to database");

    private static readonly Counter<long> QueriesExecuted = Meter.CreateCounter<long>(
        "repoql.db.queries.executed",
        unit: "queries",
        description: "SQL queries executed");

    private static readonly Counter<long> NodesWritten = Meter.CreateCounter<long>(
        "repoql.db.nodes.written",
        unit: "nodes",
        description: "Graph nodes written (child nodes of documents)");

    private static readonly Counter<long> EdgesWritten = Meter.CreateCounter<long>(
        "repoql.db.edges.written",
        unit: "edges",
        description: "Graph edges written");

    private static readonly Counter<long> SpansWritten = Meter.CreateCounter<long>(
        "repoql.db.spans.written",
        unit: "spans",
        description: "Source spans written");

    // Histograms - track latency distributions
    private static readonly Histogram<double> IndexDuration = Meter.CreateHistogram<double>(
        "repoql.db.index.duration",
        unit: "ms",
        description: "Time to index a document (full transaction)");

    private static readonly Histogram<double> DeleteDuration = Meter.CreateHistogram<double>(
        "repoql.db.delete.duration",
        unit: "ms",
        description: "Time to delete a document subtree");

    private static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        "repoql.db.query.duration",
        unit: "ms",
        description: "Time to execute a query");

    private static readonly Histogram<double> AnnotationDuration = Meter.CreateHistogram<double>(
        "repoql.db.annotation.duration",
        unit: "ms",
        description: "Time to replace annotations for a document");

    private static readonly Histogram<double> EmbeddingDuration = Meter.CreateHistogram<double>(
        "repoql.db.embedding.duration",
        unit: "ms",
        description: "Time to write embedding batch");

    private static readonly Histogram<double> LockWaitDuration = Meter.CreateHistogram<double>(
        "repoql.db.lock.wait",
        unit: "ms",
        description: "Time waiting to acquire read/write lock");

    #endregion

    private readonly DuckDBConnection _reader;
    private readonly DuckDBConnection _writer;
    private readonly bool _isInMemory;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly ILogger<DuckDbRepoDatabase> _logger;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IReadOnlyList<FormatSqlScript> _formatSchemaScripts;

    private bool _disposed;

    /// <summary>
    /// Create a new RepoQL database.
    /// </summary>
    /// <param name="databasePath">
    /// Path to the DuckDB file, or null for an in-memory database.
    /// </param>
    /// <param name="embeddingProvider">
    /// Optional embedding provider for UDF registration (cosine_similarity, etc.).
    /// </param>
    /// <param name="formatSchemaScripts">
    /// Optional SQL scripts from format loaders to run during schema initialization.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public DuckDbRepoDatabase(
        string? databasePath = null,
        IEmbeddingProvider? embeddingProvider = null,
        IEnumerable<FormatSqlScript>? formatSchemaScripts = null,
        ILogger<DuckDbRepoDatabase>? logger = null)
    {
        _logger = logger ?? NullLogger<DuckDbRepoDatabase>.Instance;
        _embeddingProvider = embeddingProvider;
        _formatSchemaScripts = formatSchemaScripts?.ToArray() ?? [];
        _isInMemory = databasePath is null;

        if (_isInMemory)
        {
            // In-memory: single connection (each :memory: is a separate database)
            _writer = new DuckDBConnection("Data Source=:memory:");
            _writer.Open();
            _reader = _writer;
            _logger.LogDebug("Opened in-memory database (single connection)");
        }
        else
        {
            // File-based: separate read/write connections
            _writer = new DuckDBConnection($"Data Source={databasePath};threads=1;ACCESS_MODE=READ_WRITE");
            _writer.Open();
            _reader = new DuckDBConnection($"Data Source={databasePath};threads=1;ACCESS_MODE=READ_ONLY");
            _reader.Open();
            _logger.LogDebug("Opened file database at {Path} (dual connections)", databasePath);
        }
    }

    /// <inheritdoc />
    public void EnsureSchema()
    {
        _lock.EnterWriteLock();
        try
        {
            _writer.Execute("""
                             CREATE TABLE IF NOT EXISTS repo_metadata(
                                 key TEXT PRIMARY KEY,
                                 value TEXT,
                                 updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                             );
                             """);
            
            // Register UDFs FIRST - some schema uses them
            RepositoryUserDefinedFunctions.RegisterAll(_writer, _embeddingProvider);

            // Run schema on writer connection
            ExecuteSqlResource(_writer, "Tables/artifact.sql");
            ExecuteSqlResource(_writer, "Tables/node.sql");
            ExecuteSqlResource(_writer, "Tables/span.sql");
            ExecuteSqlResource(_writer, "Tables/edge.sql");
            ExecuteSqlResource(_writer, "Macros/entities_by_uri.sql");
            ExecuteSqlResource(_writer, "Macros/json_extract_string_array.sql");
            ExecuteSqlResource(_writer, "Tables/annotation.sql");
            ExecuteSqlResource(_writer, "Views/annotations.sql");
            ExecuteSqlResource(_writer, "Macros/annotations_for.sql");
            ExecuteSqlResource(_writer, "Macros/annotations_all.sql");
            ExecuteSqlResource(_writer, "Macros/glob_match.sql");
            ExecuteSqlResource(_writer, "Tables/document_embedding.sql");
            ExecuteSqlResource(_writer, "Views/repo_index.sql");
            ExecuteSqlResource(_writer, "Macros/snippet.sql");
            ExecuteSqlResource(_writer, "Macros/node_primary_fragment.sql");
            ExecuteSqlResource(_writer, "Macros/xray_documents.sql");
            ExecuteSqlResource(_writer, "Macros/xray_items.sql");
            ExecuteSqlResource(_writer, "Macros/xray_lines.sql");
            ExecuteSqlResource(_writer, "Tables/document_search.sql");
            ExecuteSqlResource(_writer, "Macros/search.sql");
            ExecuteSqlResource(_writer, "Macros/hybrid_search.sql");

            // Format-specific schemas
            foreach (var script in _formatSchemaScripts)
            {
                try
                {
                    _writer.Execute(script.Sql);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply format schema {FormatSchema}", script.Identifier);
                }
            }

            // For file-based, also register UDFs on reader
            if (!_isInMemory)
            {
                RepositoryUserDefinedFunctions.RegisterAll(_reader, _embeddingProvider);
            }

            _logger.LogDebug("Schema initialized");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql)
    {
        var sw = Stopwatch.StartNew();
        var lockSw = Stopwatch.StartNew();
        _lock.EnterReadLock();
        LockWaitDuration.Record(lockSw.Elapsed.TotalMilliseconds, new TagList { { "lock_type", "read" } });

        try
        {
            using var cmd = _reader.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();

            var results = new List<Dictionary<string, object?>>();
            var columns = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                columns[i] = reader.GetName(i);

            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(columns.Length);
                for (int i = 0; i < columns.Length; i++)
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }

            QueriesExecuted.Add(1);
            QueryDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "row_count_bucket", results.Count.ToRowCountBucket() } });

            return results;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<T> Query<T>(string sql, Func<IDataRecord, T> mapper)
    {
        var sw = Stopwatch.StartNew();
        var lockSw = Stopwatch.StartNew();
        _lock.EnterReadLock();
        LockWaitDuration.Record(lockSw.Elapsed.TotalMilliseconds, new TagList { { "lock_type", "read" } });

        try
        {
            using var cmd = _reader.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();

            var results = new List<T>();
            while (reader.Read())
                results.Add(mapper(reader));

            QueriesExecuted.Add(1);
            QueryDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "row_count_bucket", results.Count.ToRowCountBucket() } });

            return results;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public IndexResult IndexArtifact(RepoUri uri, ParsedArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(artifact);

        var sw = Stopwatch.StartNew();
        var lockSw = Stopwatch.StartNew();
        _lock.EnterWriteLock();
        LockWaitDuration.Record(lockSw.Elapsed.TotalMilliseconds, new TagList { { "lock_type", "write" } });

        try
        {
            using var tx = _writer.BeginTransaction();

            // 1. Check if this is an update
            var existingDoc = GetDocumentByUriInternal(uri);
            var isUpdate = existingDoc is not null;

            // 2. Upsert artifact
            var savedArtifact = UpsertArtifactInternal(artifact.Artifact, tx);

            // 3. Create document node with artifact ID
            var docNode = artifact.DocumentNode with { ArtifactId = savedArtifact.Id };

            // 4. Upsert document
            var savedDoc = UpsertDocumentByUriInternal(uri, docNode, tx);

            // 5. Remap children with correct artifact IDs
            var children = artifact.Children.Select(c =>
                c with { ArtifactId = c.ArtifactId == artifact.Artifact.Id ? savedArtifact.Id : c.ArtifactId }
            ).ToList();

            // 6. Remap spans with document ID
            var spans = artifact.Spans.Select(s => s with { DocumentId = savedDoc.Id }).ToList();

            // 7. Remap edges with scope document ID
            var edges = artifact.Edges.Select(e => e with { ScopeDocumentId = savedDoc.Id }).ToList();

            // 8. Replace document content
            ReplaceDocumentContentInternal(savedDoc.Id, children, spans, edges, tx);

            // 9. Upsert document_search projection
            UpsertDocumentSearchInternal(savedDoc.Id, uri, tx);

            tx.Commit();

            // Record metrics with tags for drill-down
            var mediaType = artifact.Artifact.MediaType?.ToString() ?? "unknown";
            var tags = new TagList
            {
                { "operation", isUpdate ? "update" : "insert" },
                { "media_type", mediaType }
            };

            DocumentsIndexed.Add(1, tags);
            NodesWritten.Add(children.Count, tags);
            SpansWritten.Add(spans.Count, tags);
            EdgesWritten.Add(edges.Count, tags);
            IndexDuration.Record(sw.Elapsed.TotalMilliseconds, tags);

            _logger.LogDebug("Indexed artifact {Uri} (update={IsUpdate}) in {ElapsedMs:F1}ms",
                uri, isUpdate, sw.Elapsed.TotalMilliseconds);

            return new IndexResult(savedDoc.Id, isUpdate);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public bool DeleteArtifact(RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var sw = Stopwatch.StartNew();
        var lockSw = Stopwatch.StartNew();
        _lock.EnterWriteLock();
        LockWaitDuration.Record(lockSw.Elapsed.TotalMilliseconds, new TagList { { "lock_type", "write" } });

        try
        {
            var doc = GetDocumentByUriInternal(uri);
            if (doc is null)
                return false;

            using var tx = _writer.BeginTransaction();
            DeleteSubtreeInternal(doc.Id, tx);
            tx.Commit();

            DocumentsDeleted.Add(1);
            DeleteDuration.Record(sw.Elapsed.TotalMilliseconds);
            _logger.LogDebug("Deleted artifact {Uri} in {ElapsedMs:F1}ms", uri, sw.Elapsed.TotalMilliseconds);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public bool ReplaceAnnotations(RepoUri artifactUri, IReadOnlyList<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(artifactUri);
        ArgumentNullException.ThrowIfNull(annotations);

        var sw = Stopwatch.StartNew();
        var lockSw = Stopwatch.StartNew();
        _lock.EnterWriteLock();
        LockWaitDuration.Record(lockSw.Elapsed.TotalMilliseconds, new TagList { { "lock_type", "write" } });

        try
        {
            var doc = GetDocumentByUriInternal(artifactUri);
            if (doc is null)
                return false;

            using var tx = _writer.BeginTransaction();

            // Get sources from new annotations
            var sources = annotations
                .Where(a => !string.IsNullOrEmpty(a.Source))
                .Select(a => a.Source!)
                .Distinct()
                .ToHashSet(StringComparer.Ordinal);

            // Delete existing annotations from these sources
            if (sources.Count > 0)
            {
                var existing = GetAnnotationsForDocumentInternal(doc.Id);
                foreach (var stale in existing.Where(a => sources.Contains(a.Source ?? "")))
                {
                    DeleteAnnotationInternal(stale.Id, tx);
                }
            }

            // Insert new annotations
            var count = 0;
            foreach (var annotation in annotations)
            {
                var withDocId = annotation with { ScopeDocumentId = doc.Id };
                UpsertAnnotationInternal(withDocId, tx);
                count++;
            }

            tx.Commit();

            // Record metrics with source tag for drill-down by analyzer
            var sourceTags = sources.Count == 1 ? sources.First() : "multiple";
            var tags = new TagList { { "source", sourceTags } };
            AnnotationsWritten.Add(count, tags);
            AnnotationDuration.Record(sw.Elapsed.TotalMilliseconds, tags);

            _logger.LogDebug("Replaced {Count} annotations for {Uri} in {ElapsedMs:F1}ms",
                count, artifactUri, sw.Elapsed.TotalMilliseconds);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public void WriteEmbeddings(IReadOnlyList<DocumentEmbedding> embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        if (embeddings.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        var lockSw = Stopwatch.StartNew();
        _lock.EnterWriteLock();
        LockWaitDuration.Record(lockSw.Elapsed.TotalMilliseconds, new TagList { { "lock_type", "write" } });

        try
        {
            using var tx = _writer.BeginTransaction();

            foreach (var e in embeddings)
            {
                var vector = e.Vector.ToDuckDbArray();
                using var cmd = _writer.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO document_embedding(doc_id, node_id, chunk_index, embedding_type, uri, scope, model, dim, embedding, start_byte, end_byte, updated_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, CURRENT_TIMESTAMP)
                    ON CONFLICT(doc_id, node_id, chunk_index, embedding_type) DO UPDATE SET
                        uri=excluded.uri,
                        scope=excluded.scope,
                        model=excluded.model,
                        dim=excluded.dim,
                        embedding=excluded.embedding,
                        start_byte=excluded.start_byte,
                        end_byte=excluded.end_byte,
                        updated_at=excluded.updated_at;
                    """;
                cmd.AddParameters( e.DocumentId, e.NodeId, e.ChunkIndex, e.EmbeddingType,
                    e.Uri, e.Scope, e.Model, e.Dimension, vector, e.StartByte, e.EndByte);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();

            // Group metrics by type and scope for drill-down
            var byType = embeddings.GroupBy(e => e.EmbeddingType).ToList();
            var byScope = embeddings.GroupBy(e => e.Scope).ToList();
            var modelName = embeddings.FirstOrDefault()?.Model ?? "unknown";

            foreach (var group in byType)
            {
                var tags = new TagList
                {
                    { "model", modelName },
                    { "embedding_type", group.Key },
                    { "scope", byScope.Count == 1 ? byScope[0].Key : "mixed" }
                };
                EmbeddingsWritten.Add(group.Count(), tags);
            }

            EmbeddingDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList
            {
                { "model", modelName },
                { "embedding_type", byType.Count == 1 ? byType[0].Key : "mixed" }
            });

            _logger.LogDebug("Wrote {Count} embeddings in {ElapsedMs:F1}ms",
                embeddings.Count, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _lock.EnterWriteLock();
        try
        {
            if (!_isInMemory)
                _reader.Dispose();
            _writer.Dispose();
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }

    #region Internal Implementation

    private Node? GetDocumentByUriInternal(RepoUri uri)
    {
        var lc = uri.Container.AbsoluteUri.ToLowerInvariant();
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = "SELECT id, kind, uri, artifact_id, span_id, properties, headline, structure, created_at, updated_at FROM node WHERE container_uri_lowercase = ?;";
        cmd.AddParameters(lc);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return reader.MapToNode();
    }

    private Artifact UpsertArtifactInternal(Artifact artifact, DuckDBTransaction tx)
    {
        // Check if artifact with same digest exists
        using var check = _writer.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure FROM artifact WHERE digest = ?;";
        check.AddParameters(artifact.Digest);
        using var reader = check.ExecuteReader();
        if (reader.Read())
            return reader.MapToArtifact();

        // Insert new
        using var ins = _writer.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO artifact (id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);
            """;
        ins.AddParameters(artifact.Id, artifact.Digest, artifact.Size, artifact.MediaType?.ToString(),
            artifact.Text, artifact.StoreUri?.ToString(), artifact.Headline, artifact.Summary, artifact.Structure);
        ins.ExecuteNonQuery();

        return artifact;
    }

    private Node UpsertDocumentByUriInternal(RepoUri uri, Node document, DuckDBTransaction tx)
    {
        var lc = uri.Container.AbsoluteUri.ToLowerInvariant();
        var uriStr = uri.Container.AbsoluteUri;

        // Check if exists
        using var check = _writer.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT id FROM node WHERE container_uri_lowercase = ?;";
        check.AddParameters( lc);
        using var reader = check.ExecuteReader();

        if (reader.Read())
        {
            var id = reader.GetGuid(0);
            reader.Close();

            // Update existing
            using var upd = _writer.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE node
                SET kind=?, uri=?, container_uri_lowercase=?, artifact_id=?, span_id=?, properties=?, headline=?, structure=?, updated_at=?
                WHERE id=?;
                """;
            upd.AddParameters( document.Kind, uriStr, lc, document.ArtifactId, document.SpanId,
                document.Props.ToJsonString(), document.Headline, document.Structure,
                document.UpdatedAt.UtcDateTime, id);
            upd.ExecuteNonQuery();

            return document with { Id = id };
        }

        reader.Close();

        // Insert new
        using var ins = _writer.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO node (id, kind, uri, container_uri_lowercase, artifact_id, span_id, properties, headline, structure, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """;
        ins.AddParameters( document.Id, document.Kind, uriStr, lc, document.ArtifactId, document.SpanId,
            document.Props.ToJsonString(), document.Headline, document.Structure,
            document.CreatedAt.UtcDateTime, document.UpdatedAt.UtcDateTime);
        ins.ExecuteNonQuery();

        return document;
    }

    private void ReplaceDocumentContentInternal(Guid documentId, IReadOnlyList<Node> children, IReadOnlyList<Span> spans, IReadOnlyList<Edge> edges, DuckDBTransaction tx)
    {
        // Collect existing composition subtree (child nodes to delete)
        var childNodesToDelete = new HashSet<Guid>();
        var queue = new Queue<Guid>();

        using (var cmd = _writer.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT destination_node_id FROM edge WHERE source_node_id = ? AND is_composition = TRUE;";
            cmd.AddParameters( documentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                queue.Enqueue(r.GetGuid(0));
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!childNodesToDelete.Add(cur))
                continue;

            using var cmd = _writer.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT destination_node_id FROM edge WHERE source_node_id = ? AND is_composition = TRUE;";
            cmd.AddParameters( cur);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                queue.Enqueue(r.GetGuid(0));
        }

        // ALWAYS delete spans and edges scoped to this document (even if no children)
        // This ensures stale content is removed on reindex
        _writer.Execute(tx, "DELETE FROM span WHERE document_id = ?;", documentId);
        _writer.Execute(tx, "DELETE FROM edge WHERE scope_document_id = ?;", documentId);

        // Delete old child nodes
        foreach (var id in childNodesToDelete)
            _writer.Execute(tx, "DELETE FROM node WHERE id = ?;", id);

        // Insert new spans
        foreach (var span in spans)
        {
            using var cmd = _writer.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO span (id, document_id, start_line, start_column, end_line, end_column, start_byte, end_byte)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?);
                """;
            cmd.AddParameters( span.Id, span.DocumentId, span.StartLine, span.StartColumn,
                span.EndLine, span.EndColumn, span.StartByte, span.EndByte);
            cmd.ExecuteNonQuery();
        }

        // Insert new child nodes
        foreach (var node in children)
        {
            using var cmd = _writer.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO node (id, kind, uri, container_uri_lowercase, artifact_id, span_id, properties, headline, structure, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """;
            var uriStr = node.Uri?.AbsoluteUri;
            cmd.AddParameters( node.Id, node.Kind, uriStr, uriStr?.ToLowerInvariant(), node.ArtifactId,
                node.SpanId, node.Props.ToJsonString(), node.Headline, node.Structure,
                node.CreatedAt.UtcDateTime, node.UpdatedAt.UtcDateTime);
            cmd.ExecuteNonQuery();
        }

        // Insert new edges
        foreach (var edge in edges)
        {
            using var cmd = _writer.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO edge (id, source_node_id, destination_node_id, destination_uri, type, is_composition, ordinal,
                                  scope_document_id, semantic_key, source_span_id, destination_span_id, composition_child_id, properties, created_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """;
            // composition_child_id is set to DstId when is_composition=true to enforce single-parent constraint
            var compositionChildId = edge.IsComposition ? edge.DstId : null;
            cmd.AddParameters( edge.Id, edge.SrcId, edge.DstId, edge.DstUri?.ToString(), edge.Type, edge.IsComposition, edge.Ordinal,
                edge.ScopeDocumentId, edge.EdgeKey, edge.SrcSpanId, edge.DstSpanId, compositionChildId, edge.Props.ToJsonString(), edge.CreatedAt.UtcDateTime);
            cmd.ExecuteNonQuery();
        }
    }

    private IReadOnlyList<Annotation> GetAnnotationsForDocumentInternal(Guid documentId)
    {
        using var cmd = _writer.CreateCommand();
        cmd.CommandText = """
            SELECT id, semantic_key, kind, severity, source, rule_id, message, data,
                   scope_document_id, target_node_id, target_edge_id, target_span_id, target_uri,
                   created_at, expires_at
            FROM annotation WHERE scope_document_id = ?;
            """;
        cmd.AddParameters( documentId);
        using var reader = cmd.ExecuteReader();

        var results = new List<Annotation>();
        while (reader.Read())
            results.Add(reader.MapToAnnotation());
        return results;
    }

    private void DeleteAnnotationInternal(Guid id, DuckDBTransaction tx)
    {
        _writer.Execute(tx, "DELETE FROM annotation WHERE id = ?;", id);
    }

    private void UpsertAnnotationInternal(Annotation a, DuckDBTransaction tx)
    {
        using var cmd = _writer.CreateCommand();
        cmd.Transaction = tx;

        var useSemantic = !string.IsNullOrWhiteSpace(a.SemanticKey);
        cmd.CommandText = useSemantic
            ? """
              INSERT INTO annotation (id, semantic_key, kind, severity, source, rule_id, message, data,
                  scope_document_id, target_node_id, target_edge_id, target_span_id, target_uri, created_at, expires_at)
              VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
              ON CONFLICT(semantic_key) DO UPDATE SET
                  kind=excluded.kind, severity=excluded.severity, source=excluded.source,
                  rule_id=excluded.rule_id, message=excluded.message, data=excluded.data,
                  scope_document_id=excluded.scope_document_id, target_node_id=excluded.target_node_id,
                  target_edge_id=excluded.target_edge_id, target_span_id=excluded.target_span_id,
                  target_uri=excluded.target_uri, created_at=excluded.created_at, expires_at=excluded.expires_at;
              """
            : """
              INSERT INTO annotation (id, semantic_key, kind, severity, source, rule_id, message, data,
                  scope_document_id, target_node_id, target_edge_id, target_span_id, target_uri, created_at, expires_at)
              VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
              ON CONFLICT(id) DO UPDATE SET
                  kind=excluded.kind, severity=excluded.severity, source=excluded.source,
                  rule_id=excluded.rule_id, message=excluded.message, data=excluded.data,
                  scope_document_id=excluded.scope_document_id, target_node_id=excluded.target_node_id,
                  target_edge_id=excluded.target_edge_id, target_span_id=excluded.target_span_id,
                  target_uri=excluded.target_uri, created_at=excluded.created_at, expires_at=excluded.expires_at;
              """;

        cmd.AddParameters( a.Id, a.SemanticKey, a.Kind, a.Severity, a.Source, a.RuleId, a.Message, a.Data.ToJsonString(),
            a.ScopeDocumentId, a.TargetNodeId, a.TargetEdgeId, a.TargetSpanId, a.TargetUri?.ToString(),
            a.CreatedAt.UtcDateTime, a.ExpiresAt?.UtcDateTime);
        cmd.ExecuteNonQuery();
    }

    private void UpsertDocumentSearchInternal(Guid docId, RepoUri uri, DuckDBTransaction tx)
    {
        var uriStr = uri.Container.AbsoluteUri;
        var normalized = uriStr.Replace('\\', '/');
        var searchKey = normalized.ToLowerInvariant();

        // Extract basename (last path component)
        var lastSlash = normalized.LastIndexOf('/');
        var basename = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;

        // Extract dirname (everything before last slash)
        var dirname = lastSlash > 0 ? normalized[..lastSlash] : null;

        using var cmd = _writer.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO document_search (doc_id, uri, search_key, basename, dirname)
            VALUES (?, ?, ?, ?, ?)
            ON CONFLICT (doc_id) DO UPDATE SET
                uri = excluded.uri,
                search_key = excluded.search_key,
                basename = excluded.basename,
                dirname = excluded.dirname;
            """;
        cmd.AddParameters( docId, uriStr, searchKey, basename, dirname);
        cmd.ExecuteNonQuery();
    }

    private void DeleteSubtreeInternal(Guid rootId, DuckDBTransaction tx)
    {
        // Collect subtree
        var toDelete = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            using var cmd = _writer.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT destination_node_id FROM edge WHERE source_node_id = ? AND is_composition = TRUE;";
            cmd.AddParameters( cur);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var child = r.GetGuid(0);
                if (toDelete.Add(child))
                    queue.Enqueue(child);
            }
        }

        // Delete in order: document_search, document_embedding, annotations, edges, spans, nodes
        foreach (var id in toDelete)
            _writer.Execute(tx, "DELETE FROM document_search WHERE doc_id = ?;", id);

        foreach (var id in toDelete)
            _writer.Execute(tx, "DELETE FROM document_embedding WHERE doc_id = ?;", id);

        foreach (var id in toDelete)
            _writer.Execute(tx, "DELETE FROM annotation WHERE scope_document_id = ?;", id);

        // Delete edges scoped to this document, or composition edges where this node is the source
        // Note: We intentionally don't delete edges where destination_node_id matches because those
        // are reference edges from OTHER documents pointing TO this one - they become dangling references
        foreach (var id in toDelete)
            _writer.Execute(tx, "DELETE FROM edge WHERE scope_document_id = ? OR source_node_id = ?;", id, id);

        foreach (var id in toDelete)
            _writer.Execute(tx, "DELETE FROM span WHERE document_id = ?;", id);

        foreach (var id in toDelete)
            _writer.Execute(tx, "DELETE FROM node WHERE id = ?;", id);
    }

    #endregion

    #region Helpers

    private static void ExecuteSqlResource(DuckDBConnection conn, string relativePath)
    {
        var sql = LoadSqlResource(relativePath);
        if (!string.IsNullOrWhiteSpace(sql))
            conn.Execute(sql);
    }

    private static string LoadSqlResource(string relativePath)
    {
        var assembly = typeof(DuckDbRepoDatabase).Assembly;
        var normalized = relativePath.Trim()
            .TrimStart('/', '\\')
            .Replace('/', '.')
            .Replace('\\', '.');
        var resourceName = $"{typeof(DuckDbGraphStore).Namespace}.Schema.{normalized}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    #endregion
}
