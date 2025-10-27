using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Metrics;

namespace RepoQL.Data.DuckDB;

/// <summary>
///     DuckDB-backed implementation of <see cref="IGraphStore" />. Provides a self-describing schema,
///     enables helpful extensions, registers UDFs, and installs the “anything by URI” macro.
/// </summary>
    public sealed class DuckDbGraphStore : IGraphStore
    {
        private readonly DuckDBConnection _connection;
        private readonly bool _ownsConnection;
        private readonly ILogger<DuckDbGraphStore> _logger;
        private readonly IndexingMetrics _metrics;
        private readonly IReadOnlyList<FormatSqlScript> _formatSchemaScripts;
        private readonly object _annotationGate = new();

    // OpenTelemetry-style instrumentation
    private static readonly ActivitySource ActivitySource = new("RepoQL.Data.DuckDB");
    private readonly string? _databaseLabel;

    private static string JsonFromNode(JsonNode? node)
    {
        if (node is null) return "{}";
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        node.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    ///     Recompute embeddings for all documents using the provided local embedding provider.
    ///     Upserts rows into document_embedding (model, dim, embedding JSON, updated_at).
    /// </summary>
    public void RefreshDocumentEmbeddings(Contracts.Embeddings.IEmbeddingProvider provider, CancellationToken ct = default)
    {
        if (provider is null || !provider.Enabled)
            return;

        // Read documents with text content
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"SELECT n.id, a.text_content FROM node n LEFT JOIN artifact a ON a.id = n.artifact_id WHERE n.kind='document' AND a.text_content IS NOT NULL;";
        using var activity = StartDbActivity(cmd.CommandText);

        var rows = new List<(Guid Id, string Text)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var id = r.GetGuid(0);
                var text = r.IsDBNull(1) ? string.Empty : r.GetString(1);
                rows.Add((id, text));
            }
        }

        if (rows.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        var success = 0;
        var skipped = 0;
        var batchSize = 8;
        if (int.TryParse(Environment.GetEnvironmentVariable("REPOQL_EMBED_BATCH_SIZE"), out var bs) && bs > 0)
        {
            batchSize = bs;
        }
        for (var ofs = 0; ofs < rows.Count; ofs += batchSize)
        {
            using var tx = _connection.BeginTransaction();
            var slice = rows.GetRange(ofs, Math.Min(batchSize, rows.Count - ofs));
            foreach (var (id, text) in slice)
            {
                ct.ThrowIfCancellationRequested();
                var t0 = Stopwatch.StartNew();
                var vec = provider.EmbedAsync(text, ct).GetAwaiter().GetResult();
                t0.Stop();
                if (vec is null)
                {
                    skipped++;
                    _metrics.EmbedErrors.Add(1, new TagList
                    {
                        { "source", "refresh" }, 
                        { "model", provider.Model }, 
                        { "dim", provider.Dimension }
                    });
                    _metrics.EmbedRequests.Add(1, new TagList
                    {
                        { "source", "refresh" }, 
                        { "model", provider.Model }, 
                        { "dim", provider.Dimension }, 
                        { "status", "error" }
                    });
                    _metrics.EmbedDuration.Record(t0.Elapsed.TotalMilliseconds, new TagList
                    {
                        { "source", "refresh" }, 
                        { "model", provider.Model }, 
                        { "dim", provider.Dimension }, 
                        { "status", "error" }
                    });
                    continue;
                }
                var json = SerializeFloatArray(vec);
                using var up = _connection.CreateCommand();
                up.Transaction = tx;
                up.CommandText = """
                                 INSERT INTO document_embedding(doc_id, model, dim, embedding, updated_at)
                                 VALUES (?,?,?,?, CURRENT_TIMESTAMP)
                                 ON CONFLICT (doc_id) DO UPDATE SET model=excluded.model, dim=excluded.dim, embedding=excluded.embedding, updated_at=excluded.updated_at;
                                 """;
                AddParameters(up, id, provider.Model, provider.Dimension, json);
                up.ExecuteNonQuery();
                success++;
                _metrics.EmbedRequests.Add(1, new TagList
                {
                    { "source", "refresh" }, 
                    { "model", provider.Model }, 
                    { "dim", provider.Dimension }, 
                    { "status", "ok" }
                });
                _metrics.EmbedDuration.Record(t0.Elapsed.TotalMilliseconds, new TagList
                {
                    { "source", "refresh" },
                    { "model", provider.Model },
                    { "dim", provider.Dimension },
                    { "status", "ok" }
                });
            }
            tx.Commit();
        }
        sw.Stop();
        _logger.LogInformation("Embeddings refreshed: docs={Success}, skipped={Skipped}, model={Model}, dim={Dim}, ms={Duration}", success, skipped, provider.Model, provider.Dimension, (long)sw.Elapsed.TotalMilliseconds);
    }

    private static string SerializeFloatArray(float[] vec)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var f in vec)
            {
                w.WriteNumberValue(f);
            }
            w.WriteEndArray();
            w.Flush();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    ///     Creates a DuckDbGraphStore with an existing connection.
    /// </summary>
    /// <param name="connection">An open DuckDB connection.</param>
    /// <param name="metrics">Shared indexing metrics instance.</param>
    /// <param name="enableExtensions">Install/Load recommended extensions when true.</param>
    /// <param name="registerUdfs">Register repository URI and media type scalar UDFs when true.</param>
    /// <param name="logger">Optional logger for macro/view creation warnings.</param>
    /// <param name="embeddingProvider">Optional embedding provider for document embeddings.</param>
    /// <param name="formatSchemaScripts">Optional SQL snippets supplied by format loaders.</param>
    public DuckDbGraphStore(
        DuckDBConnection connection,
        IndexingMetrics? metrics = null,
        bool enableExtensions = true,
        bool registerUdfs = true,
        ILogger<DuckDbGraphStore>? logger = null,
        Contracts.Embeddings.IEmbeddingProvider? embeddingProvider = null,
        IEnumerable<FormatSqlScript>? formatSchemaScripts = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ownsConnection = false;
        _logger = logger ?? NullLogger<DuckDbGraphStore>.Instance;
        _metrics = metrics ?? new IndexingMetrics();
        _formatSchemaScripts = formatSchemaScripts?.ToArray() ?? Array.Empty<FormatSqlScript>();
        _databaseLabel = TryExtractDbNameSafe(_connection.ConnectionString);

        if (enableExtensions)
            EnableExtensions();
        if (registerUdfs)
            RepositoryUserDefinedFunctions.RegisterAll(connection, _metrics, embeddingProvider);
    }

    /// <summary>
    ///     Opens a DuckDB database from a file path. Optionally enables extensions and registers UDFs.
    /// </summary>
    /// <param name="filePath">Path to a DuckDB file.</param>
    /// <param name="metrics">Shared indexing metrics instance.</param>
    /// <param name="enableExtensions">Install/Load recommended extensions when true.</param>
    /// <param name="registerUdfs">Register repository URI and media type scalar UDFs when true.</param>
    /// <param name="logger">Optional logger for macro/view creation warnings.</param>
    /// <param name="embeddingProvider">Optional embedding provider for document embeddings.</param>
    /// <param name="formatSchemaScripts">Optional SQL snippets supplied by format loaders.</param>
    public DuckDbGraphStore(
        string filePath,
        IndexingMetrics? metrics = null,
        bool enableExtensions = true,
        bool registerUdfs = true,
        ILogger<DuckDbGraphStore>? logger = null,
        Contracts.Embeddings.IEmbeddingProvider? embeddingProvider = null,
        IEnumerable<FormatSqlScript>? formatSchemaScripts = null)
    {
        _connection = new DuckDBConnection($"Data Source={filePath}");
        _connection.Open();
        _ownsConnection = true;
        _logger = logger ?? NullLogger<DuckDbGraphStore>.Instance;
        _metrics = metrics ?? new IndexingMetrics();
        _formatSchemaScripts = formatSchemaScripts?.ToArray() ?? Array.Empty<FormatSqlScript>();
        _databaseLabel = TryExtractDbNameSafe(_connection.ConnectionString);

        if (enableExtensions)
            EnableExtensions();
        if (registerUdfs)
            RepositoryUserDefinedFunctions.RegisterAll(_connection, _metrics, embeddingProvider);
    }

    /// <summary>
    ///     Disposes the underlying database connection if owned by this instance.
    /// </summary>
    public void Dispose()
    {
        if (_ownsConnection)
        {
            _connection.Dispose();
        }
    }

    /// <summary>
    ///     Creates tables, indexes, comments, and the <c>entities_by_uri</c> macro. Idempotent.
    /// </summary>
    public void EnsureSchema()
    {
        ExecuteSqlResource("Tables/artifact.sql");
        ExecuteSqlResource("Tables/node.sql");
        ExecuteSqlResource("Tables/span.sql");
        ExecuteSqlResource("Tables/edge.sql");
        ExecuteSqlResource("Macros/entities_by_uri.sql");
        ExecuteSqlResource("Macros/json_extract_string_array.sql");
        ExecuteSqlResource("Tables/annotation.sql");
        ExecuteSqlResource("Views/annotations.sql");
        ExecuteSqlResource("Macros/annotations_for.sql");
        ExecuteSqlResource("Macros/annotations_all.sql");
        ExecuteSqlResource("Macros/glob_match.sql");
        ExecuteSqlResource("Tables/document_embedding.sql");
        ExecuteSqlResource("Macros/snippet.sql");
        // First create the node_primary_fragment macro as a workaround for the 6-parameter limitation
        ExecuteSqlResource("Macros/node_primary_fragment.sql");
        ExecuteSqlResource("Macros/xray_documents.sql");
        // Add the items-within-documents macro for exploring document structure
        ExecuteSqlResource("Macros/xray_items.sql");
        ExecuteSqlResource("Macros/xray_lines.sql");
        ExecuteSqlResource("Tables/document_search.sql");

        foreach (var script in _formatSchemaScripts)
        {
            try
            {
                Execute(script.Sql);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply format schema {FormatSchema}", script.Identifier);
            }
        }

    }



    public Artifact? GetArtifactByDigest(string digest)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT id,digest,byte_size,media_type,text_content,storage_uri,headline,summary,structure FROM artifact WHERE digest = ?;";
        AddParameters(cmd, digest);
        using var activity = StartDbActivity(cmd.CommandText);
        try
        {
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var storeUriValue = r.GetValue(5);
            RepoUri? storeUri = null;
            if (storeUriValue is string rawUri && !string.IsNullOrWhiteSpace(rawUri))
            {
                try
                {
                    storeUri = RepoUri.Parse(rawUri);
                }
                catch (FormatException)
                {
                    storeUri = null;
                }
            }

            return new Artifact
            {
                Id = r.GetGuid(0),
                Digest = r.GetString(1),
                Size = r.GetInt64(2),
                MediaType = ParseMediaType(r.IsDBNull(3) ? null : r.GetString(3)),
                Text = r.IsDBNull(4) ? null : r.GetString(4),
                StoreUri = storeUri,
                Headline = r.IsDBNull(6) ? null : r.GetString(6),
                Summary = r.IsDBNull(7) ? null : r.GetString(7),
                Structure = r.IsDBNull(8) ? null : r.GetString(8)
            };
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    public Artifact? GetArtifact(Guid id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT id,digest,byte_size,media_type,text_content,storage_uri,headline,summary,structure FROM artifact WHERE id = ?;";
        AddParameters(cmd, id);
        using var activity = StartDbActivity(cmd.CommandText);
        try
        {
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var storeUriValue = r.GetValue(5);
            RepoUri? storeUri = null;
            if (storeUriValue is string rawUri && !string.IsNullOrWhiteSpace(rawUri))
            {
                try
                {
                    storeUri = RepoUri.Parse(rawUri);
                }
                catch (FormatException)
                {
                    storeUri = null;
                }
            }

            return new Artifact
            {
                Id = r.GetGuid(0),
                Digest = r.GetString(1),
                Size = r.GetInt64(2),
                MediaType = ParseMediaType(r.IsDBNull(3) ? null : r.GetString(3)),
                Text = r.IsDBNull(4) ? null : r.GetString(4),
                StoreUri = storeUri,
                Headline = r.IsDBNull(6) ? null : r.GetString(6),
                Summary = r.IsDBNull(7) ? null : r.GetString(7),
                Structure = r.IsDBNull(8) ? null : r.GetString(8)
            };
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    public void RefreshSearchProjection(bool incrementalRefresh)
    {
        using var activity = ActivitySource.StartActivity("repoql.search.refresh", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("repoql.search.refresh.phase", incrementalRefresh ? "incremental" : "initial");
        }

        var sw = Stopwatch.StartNew();
        int inserted;
        using (var tx = _connection.BeginTransaction())
        {
            try
            {
                Execute("DELETE FROM document_search;");
                inserted = Execute(@"
INSERT INTO document_search (doc_id, uri, search_key, basename, dirname)
SELECT
    base.id,
    base.uri,
    lower(base.normalized_uri) AS search_key,
    COALESCE(regexp_extract(base.normalized_uri, '([^/]+)$', 1), base.normalized_uri) AS basename,
    regexp_extract(base.normalized_uri, '^(.*)/[^/]*$', 1) AS dirname
FROM (
    SELECT
        n.id,
        n.uri,
        REPLACE(n.uri, CHR(92), '/') AS normalized_uri
    FROM node n
    WHERE n.kind = 'document' AND n.uri IS NOT NULL
) AS base;
", tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                RecordException(activity, ex);
                throw;
            }
        }

        // Rebuild FTS index best effort; missing extension is tolerated
        TryExec("PRAGMA drop_fts_index('document_search');");
        var ftsCreated = TryExec("PRAGMA create_fts_index('document_search', 'doc_id', 'basename', 'dirname', 'search_key');");

        activity?.SetTag("repoql.search.refresh.documents", inserted);
        activity?.SetTag("repoql.search.refresh.duration_ms", sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("repoql.search.refresh.fts", ftsCreated);
        _logger.LogInformation("Search projection refreshed: docs={Docs}, fts={Fts}, ms={Duration}", inserted, ftsCreated, (long)sw.Elapsed.TotalMilliseconds);
    }

    public Artifact UpsertArtifact(Artifact artifact)
    {
        using var opActivity = StartOperationActivity("UpsertArtifact");
        var existing = GetArtifactByDigest(artifact.Digest);
        if (existing is not null) return existing;

        using var tx = _connection.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO artifact (id,digest,byte_size,media_type,text_content,storage_uri,headline,summary,structure)
                  VALUES (?,?,?,?,?,?,?,?,?);";
            AddParameters(cmd,
                artifact.Id,
                artifact.Digest,
                artifact.Size,
                artifact.MediaType?.ToString(),
                artifact.Text,
                artifact.StoreUri?.ToString(),
                artifact.Headline,
                artifact.Summary,
                artifact.Structure);
            using (var activity = StartDbActivity(cmd.CommandText))
            {
                try
                {
                    var rows = cmd.ExecuteNonQuery();
                    activity?.SetTag("db.sql.rows_affected", rows);
                }
                catch (Exception ex)
                {
                    RecordException(activity, ex);
                    throw;
                }
            }
            tx.Commit();
            return artifact;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            RecordException(opActivity, ex);
            throw;
        }
    }

    public Span InsertSpan(Span span)
    {
        using var opActivity = StartOperationActivity("InsertSpan");
        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"INSERT INTO span (id,document_id,start_byte,end_byte,start_line,start_column,end_line,end_column)
                  VALUES (?,?,?,?,?,?,?,?);";
        AddParameters(cmd,
            span.Id, span.DocumentId, span.StartByte, span.EndByte,
            span.StartLine, span.StartColumn, span.EndLine, span.EndColumn);
        using (var activity = StartDbActivity(cmd.CommandText))
        {
            try
            {
                var rows = cmd.ExecuteNonQuery();
                activity?.SetTag("db.sql.rows_affected", rows);
            }
            catch (Exception ex)
            {
                RecordException(activity, ex);
                RecordException(opActivity, ex);
                throw;
            }
        }
        tx.Commit();
        return span;
    }

    public Span? GetSpan(Guid id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"SELECT id,document_id,start_byte,end_byte,start_line,start_column,end_line,end_column
                  FROM span WHERE id=?;";
        AddParameters(cmd, id);
        using var activity = StartDbActivity(cmd.CommandText);
        try
        {
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new Span
            {
                Id = r.GetGuid(0),
                DocumentId = r.GetGuid(1),
                StartByte = r.IsDBNull(2) ? null : r.GetInt64(2),
                EndByte = r.IsDBNull(3) ? null : r.GetInt64(3),
                StartLine = r.IsDBNull(4) ? null : r.GetInt32(4),
                StartColumn = r.IsDBNull(5) ? null : r.GetInt32(5),
                EndLine = r.IsDBNull(6) ? null : r.GetInt32(6),
                EndColumn = r.IsDBNull(7) ? null : r.GetInt32(7)
            };
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    public bool DeleteSpan(Guid id)
    {
        using var tx = _connection.BeginTransaction();
        var n = Execute("DELETE FROM span WHERE id=?;", tx, id);
        tx.Commit();
        return n > 0;
    }

    public Node? GetNode(Guid id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"SELECT id,kind,uri,container_uri_lowercase,artifact_id,span_id,properties,created_at,updated_at
                  FROM node WHERE id=?;";
        AddParameters(cmd, id);
        using var activity = StartDbActivity(cmd.CommandText);
        try
        {
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return MapNode(r);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    public Node? GetDocumentByUri(RepoUri uri)
    {
        var lc = uri.Container.AbsoluteUri.ToLowerInvariant();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"SELECT id,kind,uri,container_uri_lowercase,artifact_id,span_id,properties,created_at,updated_at
                  FROM node WHERE container_uri_lowercase=?;";
        AddParameters(cmd, lc);
        using var activity = StartDbActivity(cmd.CommandText);
        try
        {
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return MapNode(r);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    public void DeleteDocumentByUri(RepoUri uri)
    {
        using var opActivity = StartOperationActivity("DeleteDocumentByUri");
        try
        {
            if (uri is null) throw new ArgumentNullException(nameof(uri));
            var doc = GetDocumentByUri(uri);
            if (doc is null)
                return;

            using var tx = _connection.BeginTransaction();
            DeleteSubtreeInternal(doc.Id, tx);
            tx.Commit();
        }
        catch (Exception ex)
        {
            RecordException(opActivity, ex);
            throw;
        }
    }

    public void MoveDocumentUri(RepoUri oldUri, RepoUri newUri)
    {
        using var opActivity = StartOperationActivity("MoveDocumentUri");
        try
        {
            if (oldUri is null) throw new ArgumentNullException(nameof(oldUri));
            if (newUri is null) throw new ArgumentNullException(nameof(newUri));

            var doc = GetDocumentByUri(oldUri);
            if (doc is null)
                return;

            var existing = GetDocumentByUri(newUri);
            if (existing is not null && existing.Id != doc.Id)
                throw new InvalidOperationException($"Another node already exists at URI: {newUri.Container.AbsoluteUri}");

            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE node
                                    SET uri=?, container_uri_lowercase=?, updated_at=?
                                    WHERE id=?;";
                var uriStr = newUri.Container.AbsoluteUri;
                AddParameters(cmd,
                    uriStr,
                    uriStr.ToLowerInvariant(),
                    DateTimeOffset.UtcNow.UtcDateTime,
                    doc.Id);
                using var activity = StartDbActivity(cmd.CommandText);
                var rows = cmd.ExecuteNonQuery();
                activity?.SetTag("db.sql.rows_affected", rows);
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            RecordException(opActivity, ex);
            throw;
        }
    }

    public Node UpsertDocumentByUri(RepoUri uri, Node document)
    {
        using var opActivity = StartOperationActivity("UpsertDocumentByUri");
        try
        {
            if (uri is null) throw new ArgumentNullException(nameof(uri));
            if (document is null) throw new ArgumentNullException(nameof(document));

            var lc = uri.Container.AbsoluteUri.ToLowerInvariant();
            using var tx = _connection.BeginTransaction();
            try
            {

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"SELECT id FROM node WHERE container_uri_lowercase=?;";
                AddParameters(cmd, lc);
                using var activitySel = StartDbActivity(cmd.CommandText);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    var id = r.GetGuid(0);
                    using var upd = _connection.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = @"UPDATE node
                                    SET kind=?, uri=?, container_uri_lowercase=?, artifact_id=?, span_id=?, properties=?, updated_at=?
                                  WHERE id=?;";
                    var uriStr = uri.Container.AbsoluteUri;
                    AddParameters(upd,
                        document.Kind,
                        uriStr,
                        uriStr.ToLowerInvariant(),
                        document.ArtifactId,
                        document.SpanId,
                        JsonFromNode(document.Props),
                        document.UpdatedAt.UtcDateTime,
                        id);
                    using (var activityUpd = StartDbActivity(upd.CommandText))
                    {
                        var rows = upd.ExecuteNonQuery();
                        activityUpd?.SetTag("db.sql.rows_affected", rows);
                    }
                    tx.Commit();
                    return GetNode(id)!;
                }
            }

            // Insert new
            using (var ins = _connection.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO node
                  (id,kind,uri,container_uri_lowercase,artifact_id,span_id,properties,created_at,updated_at)
                  VALUES (?,?,?,?,?,?,?,?,?);";
                var uriStr = uri.Container.AbsoluteUri;
                AddParameters(ins,
                    document.Id,
                    document.Kind,
                    uriStr,
                    uriStr.ToLowerInvariant(),
                    document.ArtifactId,
                    document.SpanId,
                    JsonFromNode(document.Props),
                    document.CreatedAt.UtcDateTime,
                    document.UpdatedAt.UtcDateTime);
                using (var activityIns = StartDbActivity(ins.CommandText))
                {
                    var rows = ins.ExecuteNonQuery();
                    activityIns?.SetTag("db.sql.rows_affected", rows);
                }
            }
            tx.Commit();
            return GetNode(document.Id)!;
            }
            catch (Exception ex)
            {
                tx.Rollback();
                RecordException(opActivity, ex);
                throw;
            }
        }
        catch (Exception ex)
        {
            RecordException(opActivity, ex);
            throw;
        }
    }

    public void ReplaceDocumentContent(Guid documentId, IEnumerable<Node> children, IEnumerable<Span> spans, IEnumerable<Edge> edges)
    {
        using var opActivity = StartOperationActivity("ReplaceDocumentContent");
        try
        {
            using var tx = _connection.BeginTransaction();

            // Collect composition subtree nodes under the document (direct and transitive)
            var toDelete = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"SELECT destination_node_id FROM edge WHERE source_node_id=? AND is_composition=TRUE;";
                AddParameters(cmd, documentId);
                using (var activity = StartDbActivity(cmd.CommandText))
                {
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) queue.Enqueue(r.GetGuid(0));
                }
            }
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!toDelete.Add(cur)) continue;
                using var c2 = _connection.CreateCommand();
                c2.Transaction = tx;
                c2.CommandText = @"SELECT destination_node_id FROM edge WHERE source_node_id=? AND is_composition=TRUE;";
                AddParameters(c2, cur);
                using (var activity2 = StartDbActivity(c2.CommandText))
                {
                    using var r2 = c2.ExecuteReader();
                    while (r2.Read()) queue.Enqueue(r2.GetGuid(0));
                }
            }

            // Remove scoped edges and old spans for this document
            Execute("DELETE FROM edge WHERE scope_document_id=?;", tx, documentId);
            Execute("DELETE FROM span WHERE document_id=?;", tx, documentId);

            if (toDelete.Count > 0)
            {
                var ids = toDelete.ToList();
                var placeholders = string.Join(",", ids.Select((_, i) => "?"));
                var idParams = ids.Cast<object>().ToArray();
                // Remove all edges touching those nodes
                Execute($@"DELETE FROM edge WHERE source_node_id IN ({placeholders}) OR destination_node_id IN ({placeholders});",
                    tx, idParams.Concat(idParams).ToArray());
                // Remove nodes
                Execute($"DELETE FROM node WHERE id IN ({placeholders});", tx, idParams);
            }

            // Insert children
            foreach (var n in children)
            {
                using var ins = _connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO node
                  (id,kind,uri,container_uri_lowercase,artifact_id,span_id,properties,created_at,updated_at)
                  VALUES (?,?,?,?,?,?,?,?,?);";
                var uriStr = n.Uri?.Container.AbsoluteUri;
                AddParameters(ins,
                    n.Id,
                    n.Kind,
                    uriStr,
                    uriStr?.ToLowerInvariant(),
                    n.ArtifactId,
                    n.SpanId,
                    JsonFromNode(n.Props),
                    n.CreatedAt.UtcDateTime,
                    n.UpdatedAt.UtcDateTime);
                using (var activity = StartDbActivity(ins.CommandText))
                {
                    var rows = ins.ExecuteNonQuery();
                    activity?.SetTag("db.sql.rows_affected", rows);
                }
            }

            // Insert spans (already mapped to documentId)
            foreach (var s in spans)
            {
                using var ins = _connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO span
                 (id,document_id,start_byte,end_byte,start_line,start_column,end_line,end_column)
                 VALUES (?,?,?,?,?,?,?,?);";
                AddParameters(ins,
                    s.Id, s.DocumentId, s.StartByte, s.EndByte, s.StartLine, s.StartColumn, s.EndLine, s.EndColumn);
                using (var activity = StartDbActivity(ins.CommandText))
                {
                    var rows = ins.ExecuteNonQuery();
                    activity?.SetTag("db.sql.rows_affected", rows);
                }
            }

            // Insert edges
            foreach (var e in edges)
            {
                using var ins = _connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO edge
                  (id,source_node_id,destination_node_id,type,is_composition,ordinal,scope_document_id,semantic_key,
                   source_span_id,destination_span_id,composition_child_id,properties,created_at)
                  VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?);";
                AddParameters(ins,
                    e.Id, e.SrcId, e.DstId, e.Type, e.IsComposition, e.Ordinal, e.ScopeDocumentId, e.EdgeKey,
                    e.SrcSpanId, e.DstSpanId,
                    e.IsComposition ? e.DstId : null, JsonFromNode(e.Props), e.CreatedAt.UtcDateTime);
                using (var activity = StartDbActivity(ins.CommandText))
                {
                    var rows = ins.ExecuteNonQuery();
                    activity?.SetTag("db.sql.rows_affected", rows);
                }
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            RecordException(opActivity, ex);
            throw;
        }
    }

    public IEnumerable<Node> GetAllNodes()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"SELECT id,kind,uri,container_uri_lowercase,artifact_id,span_id,properties,created_at,updated_at
                  FROM node ORDER BY created_at;";
        using var activity = StartDbActivity(cmd.CommandText);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return MapNode(r);
        }
    }

    public bool MoveNode(Guid id, RepoUri newUri)
    {
        using var opActivity = StartOperationActivity("MoveNode");
        try
        {
            if (newUri == null)
                throw new ArgumentNullException(nameof(newUri));

        // Check if node exists and is a document node
        var node = GetNode(id);
        if (node == null)
            return false;

        if (node.Kind != "document")
            throw new InvalidOperationException("Only document nodes can be moved.");

        // Check if another node already exists at the target URI
        var existingNode = GetDocumentByUri(newUri);
        if (existingNode != null && existingNode.Id != id)
            throw new InvalidOperationException($"Another node already exists at URI: {newUri.Container.AbsoluteUri}");

        // Update the node's URI
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"UPDATE node 
                           SET uri = ?, container_uri_lowercase = ?, updated_at = ?
                           WHERE id = ?;";

        var uriStr = newUri.Container.AbsoluteUri;
        AddParameters(cmd,
            uriStr,
            uriStr.ToLowerInvariant(),
            DateTimeOffset.UtcNow.UtcDateTime,
            id);

        using var activity = StartDbActivity(cmd.CommandText);
        var rowsAffected = 0;
        try
        {
            rowsAffected = cmd.ExecuteNonQuery();
            activity?.SetTag("db.sql.rows_affected", rowsAffected);
        }
        catch (Exception ex)
        {
            RecordException(opActivity, ex);
            throw;
        }
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            RecordException(opActivity, ex);
            throw;
        }
    }

    public Node UpsertNode(Node node)
    {
        using var opActivity = StartOperationActivity("UpsertNode");
        try
        {
            if (node.Kind == "document" && node.Uri is null)
                throw new InvalidOperationException("Document node requires a non-null URI.");

            if (node.ArtifactId is Guid aId && !ArtifactExists(aId))
                throw new InvalidOperationException($"Artifact {aId} does not exist.");

            var exists = GetNode(node.Id) is not null;

            using var tx = _connection.BeginTransaction();
            try
            {
            if (exists)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    @"UPDATE node
                          SET kind=?, uri=?, container_uri_lowercase=?, artifact_id=?, span_id=?, properties=?, updated_at=?
                          WHERE id=?;";
                var uriStr = node.Uri?.Container.AbsoluteUri;
                AddParameters(cmd,
                    node.Kind,
                    uriStr,
                    uriStr?.ToLowerInvariant(),
                    node.ArtifactId,
                    node.SpanId,
                    JsonFromNode(node.Props),
                    node.UpdatedAt.UtcDateTime,
                    node.Id);
                using (var activity = StartDbActivity(cmd.CommandText))
                {
                    var rows = cmd.ExecuteNonQuery();
                    activity?.SetTag("db.sql.rows_affected", rows);
                }
            }
            else
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    @"INSERT INTO node
                          (id,kind,uri,container_uri_lowercase,artifact_id,span_id,properties,created_at,updated_at)
                          VALUES (?,?,?,?,?,?,?,?,?);";
                var uriStr = node.Uri?.Container.AbsoluteUri;
                AddParameters(cmd,
                    node.Id,
                    node.Kind,
                    uriStr,
                    uriStr?.ToLowerInvariant(),
                    node.ArtifactId,
                    node.SpanId,
                    JsonFromNode(node.Props),
                    node.CreatedAt.UtcDateTime,
                    node.UpdatedAt.UtcDateTime);
                using (var activity = StartDbActivity(cmd.CommandText))
                {
                    var rows = cmd.ExecuteNonQuery();
                    activity?.SetTag("db.sql.rows_affected", rows);
                }
            }

            tx.Commit();
            return node;
            }
            catch (Exception ex)
            {
                tx.Rollback();
                RecordException(opActivity, ex);
                throw;
            }
        }
        catch (Exception ex)
        {
            RecordException(opActivity, ex);
            throw;
        }
    }

    public bool DeleteNode(Guid id, bool cascadeComposition = false)
    {
        using var opActivity = StartOperationActivity("DeleteNode");
        using var tx = _connection.BeginTransaction();
        try
        {

            if (!cascadeComposition)
            {
                using var chk = _connection.CreateCommand();
                chk.CommandText = "SELECT 1 FROM edge WHERE source_node_id=? AND is_composition=TRUE LIMIT 1;";
                chk.Transaction = tx;
                AddParameters(chk, id);
                using var chkActivity = StartDbActivity(chk.CommandText);
                using var r = chk.ExecuteReader();
                if (r.Read())
                    throw new InvalidOperationException("Node has composition children; use cascade.");
            }
            else
            {
                DeleteSubtreeInternal(id, tx);
                tx.Commit();
                return true;
            }

            Execute("DELETE FROM edge WHERE source_node_id=? OR destination_node_id=? OR scope_document_id=?;", tx, id, id, id);
            var n = Execute("DELETE FROM node WHERE id=?;", tx, id);

            tx.Commit();
            return n > 0;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            RecordException(opActivity, ex);
            throw;
        }
    }

    public IEnumerable<Edge> GetEdgesForNode(Guid nodeId, bool outgoing = true, bool incoming = true)
    {
        if (!outgoing && !incoming) yield break;
        var where = outgoing && incoming ? "(source_node_id=? OR destination_node_id=?)"
            : outgoing ? "source_node_id=?"
            : "destination_node_id=?";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $@"SELECT id,source_node_id,destination_node_id,type,is_composition,ordinal,scope_document_id,semantic_key,
                          source_span_id,destination_span_id,properties,created_at
                   FROM edge WHERE {where};";
        if (outgoing && incoming) AddParameters(cmd, nodeId, nodeId);
        else AddParameters(cmd, nodeId);
        using var activity = StartDbActivity(cmd.CommandText);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return MapEdge(r);
    }

    public Edge? GetEdge(Guid id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"SELECT id,source_node_id,destination_node_id,type,is_composition,ordinal,scope_document_id,semantic_key,
                         source_span_id,destination_span_id,properties,created_at
                  FROM edge WHERE id=?;";
        AddParameters(cmd, id);
        using var activity = StartDbActivity(cmd.CommandText);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapEdge(r);
    }

    public Edge UpsertEdge(Edge edge)
    {
        using var opActivity = StartOperationActivity("UpsertEdge");
        edge.Validate();
        if (GetNode(edge.SrcId) is null || GetNode(edge.DstId) is null)
            throw new InvalidOperationException("Src or Dst node does not exist.");

        using var tx = _connection.BeginTransaction();
        try
        {

        if (!string.IsNullOrWhiteSpace(edge.EdgeKey))
        {
            using var upd = _connection.CreateCommand();
            upd.CommandText =
                @"UPDATE edge SET
                        source_node_id=?, destination_node_id=?, type=?, is_composition=?, ordinal=?, scope_document_id=?,
                        source_span_id=?, destination_span_id=?, composition_child_id=?, properties=?
                      WHERE semantic_key=?;";
            AddParameters(upd,
                edge.SrcId, edge.DstId, edge.Type, edge.IsComposition, edge.Ordinal, edge.ScopeDocumentId,
                edge.SrcSpanId, edge.DstSpanId,
                edge.IsComposition ? edge.DstId : null, JsonFromNode(edge.Props),
                edge.EdgeKey);
            int rows;
            using (var activity = StartDbActivity(upd.CommandText))
            {
                rows = upd.ExecuteNonQuery();
                activity?.SetTag("db.sql.rows_affected", rows);
            }
            if (rows > 0)
            {
                tx.Commit();
                return edge;
            }
        }

        using (var ins = _connection.CreateCommand())
        {
            ins.CommandText =
                @"INSERT INTO edge
                      (id,source_node_id,destination_node_id,type,is_composition,ordinal,scope_document_id,semantic_key,
                       source_span_id,destination_span_id,composition_child_id,properties,created_at)
                      VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?);";
            AddParameters(ins,
                edge.Id, edge.SrcId, edge.DstId, edge.Type, edge.IsComposition, edge.Ordinal, edge.ScopeDocumentId,
                edge.EdgeKey, edge.SrcSpanId, edge.DstSpanId,
                edge.IsComposition ? edge.DstId : null, JsonFromNode(edge.Props),
                edge.CreatedAt.UtcDateTime);
            using var activity = StartDbActivity(ins.CommandText);
            var rows = ins.ExecuteNonQuery();
            activity?.SetTag("db.sql.rows_affected", rows);
        }

        tx.Commit();
        return edge;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            RecordException(opActivity, ex);
            throw;
        }
    }

    public int DeleteSubtree(params Guid[] rootIds)
    {
        using var opActivity = StartOperationActivity("DeleteSubtree");
        if (rootIds == null || rootIds.Length == 0)
            return 0;

        using var tx = _connection.BeginTransaction();
        try
        {
            var totalDeleted = 0;
            foreach (var rootId in rootIds)
                totalDeleted += DeleteSubtreeInternal(rootId, tx);
            tx.Commit();
            return totalDeleted;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            RecordException(opActivity, ex);
            throw;
        }
    }

    private int DeleteSubtreeInternal(Guid rootId, IDbTransaction tx)
    {
        // Phase 1: Collect all nodes in the composition subtree
        var queue = new Queue<Guid>();
        var subtreeNodes = new HashSet<Guid>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!subtreeNodes.Add(cur)) continue; // Already processed

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT destination_node_id FROM edge WHERE source_node_id=? AND is_composition=TRUE;";
            cmd.Transaction = tx as DuckDBTransaction;
            AddParameters(cmd, cur);
            using (var activity = StartDbActivity(cmd.CommandText))
            {
                using var r = cmd.ExecuteReader();
                while (r.Read()) queue.Enqueue(r.GetGuid(0));
            }
        }

        // Phase 2: Delete entities in reverse dependency order to respect FK constraints
        // Convert subtree nodes to a list for SQL IN clause
        var nodeList = subtreeNodes.ToList();

        if (nodeList.Count == 0)
            return 0;

        // Build placeholders for IN clauses
        var placeholders = string.Join(",", nodeList.Select((_, i) => "?"));
        var nodeParams = nodeList.Cast<object>().ToArray();

        // 2a. First delete edges (they reference both nodes and spans)
        // Delete ALL edges that reference any node in the subtree
        Execute($@"DELETE FROM edge 
                  WHERE source_node_id IN ({placeholders}) 
                     OR destination_node_id IN ({placeholders})
                     OR scope_document_id IN ({placeholders})",
                tx, nodeParams.Concat(nodeParams).Concat(nodeParams).ToArray());

        // 2b. Then delete spans (they reference nodes)
        Execute($"DELETE FROM span WHERE document_id IN ({placeholders})", tx, nodeParams);

        // 2c. Finally delete nodes (no more references exist)
        var deleted = Execute($"DELETE FROM node WHERE id IN ({placeholders})", tx, nodeParams);

        return deleted;
    }

    public IEnumerable<T> RawQuery<T>(string sql, Func<IDataRecord, T> map, params object?[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, parameters);
        using var activity = StartDbActivity(cmd.CommandText);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return map(r);
    }

    public IEnumerable<IReadOnlyDictionary<string, object?>> RawQuery(string sql, params object?[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, parameters);
        using var activity = StartDbActivity(cmd.CommandText);
        using var r = cmd.ExecuteReader();
        var fieldCount = r.FieldCount;
        var names = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++) names[i] = r.GetName(i);

        while (r.Read())
        {
            var dict = new Dictionary<string, object?>(fieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < fieldCount; i++)
                dict[names[i]] = r.IsDBNull(i) ? null : r.GetValue(i);
            yield return dict;
        }
    }

    public IEnumerable<ResolvedEntity> EntitiesByUri(string repositoryUri)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"SELECT entity, id, aux, uri, container_uri, fragment FROM entities_by_uri(?);";
        AddParameters(cmd, repositoryUri);
        using var activity = StartDbActivity(cmd.CommandText);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new ResolvedEntity(
                Enum.TryParse<ResolvedEntityKind>(r.GetString(0), out var kind) ? kind : ResolvedEntityKind.Unknown,
                r.GetGuid(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5)
            );
        }
    }

    // ----- Annotations API -----

    public Annotation UpsertAnnotation(Annotation a)
    {
        lock (_annotationGate)
        {
            using var tx = _connection.BeginTransaction();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                var useSemantic = !string.IsNullOrWhiteSpace(a.SemanticKey);
                cmd.CommandText = useSemantic
                    ? @"INSERT INTO annotation
                      (id,semantic_key,kind,severity,source,rule_id,message,data,scope_document_id,
                       target_node_id,target_edge_id,target_span_id,target_uri,created_at,expires_at)
                      VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                      ON CONFLICT(semantic_key) DO UPDATE SET
                        kind=excluded.kind,
                        severity=excluded.severity,
                        source=excluded.source,
                        rule_id=excluded.rule_id,
                        message=excluded.message,
                        data=excluded.data,
                        scope_document_id=excluded.scope_document_id,
                        target_node_id=excluded.target_node_id,
                        target_edge_id=excluded.target_edge_id,
                        target_span_id=excluded.target_span_id,
                        target_uri=excluded.target_uri,
                        created_at=excluded.created_at,
                        expires_at=excluded.expires_at
                      RETURNING id;"
                    : @"INSERT INTO annotation
                      (id,semantic_key,kind,severity,source,rule_id,message,data,scope_document_id,
                       target_node_id,target_edge_id,target_span_id,target_uri,created_at,expires_at)
                      VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                      ON CONFLICT(id) DO UPDATE SET
                        semantic_key=excluded.semantic_key,
                        kind=excluded.kind,
                        severity=excluded.severity,
                        source=excluded.source,
                        rule_id=excluded.rule_id,
                        message=excluded.message,
                        data=excluded.data,
                        scope_document_id=excluded.scope_document_id,
                        target_node_id=excluded.target_node_id,
                        target_edge_id=excluded.target_edge_id,
                        target_span_id=excluded.target_span_id,
                        target_uri=excluded.target_uri,
                        created_at=excluded.created_at,
                        expires_at=excluded.expires_at
                      RETURNING id;";

                AddParameters(cmd,
                    a.Id,
                    (object?)a.SemanticKey ?? DBNull.Value,
                    a.Kind,
                    a.Severity,
                    a.Source,
                    (object?)a.RuleId ?? DBNull.Value,
                    a.Message,
                    JsonFromNode(a.Data),
                    a.ScopeDocumentId,
                    (object?)a.TargetNodeId ?? DBNull.Value,
                    (object?)a.TargetEdgeId ?? DBNull.Value,
                    (object?)a.TargetSpanId ?? DBNull.Value,
                    (object?)a.TargetUri ?? DBNull.Value,
                    a.CreatedAt.UtcDateTime,
                    a.ExpiresAt?.UtcDateTime ?? (object)DBNull.Value);
                using (var activity = StartDbActivity(cmd.CommandText))
                {
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        var id = r.GetGuid(0);
                        tx.Commit();
                        return a;
                    }
                    tx.Commit();
                    return a;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to upsert annotation: SemanticKey={SemanticKey}, Kind={Kind}, " +
                    "RuleId={RuleId}, TargetUri={TargetUri}, TargetNodeId={TargetNodeId}, " +
                    "TargetEdgeId={TargetEdgeId}, TargetSpanId={TargetSpanId}, ScopeDocumentId={ScopeDocumentId}",
                    a.SemanticKey, a.Kind, a.RuleId, a.TargetUri,
                    a.TargetNodeId, a.TargetEdgeId, a.TargetSpanId, a.ScopeDocumentId);
                tx.Rollback();
                throw;
            }
        }
    }

    public Annotation? GetAnnotation(Guid id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"SELECT id,semantic_key,kind,severity,source,rule_id,message,data,
                                   scope_document_id,target_node_id,target_edge_id,target_span_id,
                                   target_uri,created_at,expires_at
                            FROM annotation WHERE id=?;";
        AddParameters(cmd, id);
        using var activity = StartDbActivity(cmd.CommandText);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapAnnotation(r);
    }

    public bool DeleteAnnotation(Guid id)
    {
        return Execute("DELETE FROM annotation WHERE id=?;", id) > 0;
    }

    public IEnumerable<Annotation> GetAnnotationsForDocument(Guid documentId, string? kinds = null, string? minSeverity = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"SELECT id,semantic_key,kind,severity,source,rule_id,message,data,
                                   scope_document_id,target_node_id,target_edge_id,target_span_id,
                                   target_uri,created_at,expires_at
                           FROM annotation a
                           WHERE scope_document_id = ?
                             AND ( ? IS NULL OR EXISTS (
                                   SELECT 1 FROM UNNEST(string_split(?, ',')) k(value)
                                   WHERE lower(trim(k.value)) = lower(a.kind)))
                             AND ( ? IS NULL OR 
                                    CASE lower(a.severity)
                                      WHEN 'error' THEN 4
                                      WHEN 'warning' THEN 3
                                      WHEN 'info' THEN 2
                                      WHEN 'hint' THEN 1
                                      ELSE 0 END >=
                                    CASE lower(?)
                                      WHEN 'error' THEN 4
                                      WHEN 'warning' THEN 3
                                      WHEN 'info' THEN 2
                                      WHEN 'hint' THEN 1
                                      ELSE 0 END)
                           ORDER BY created_at DESC;";
        AddParameters(cmd,
            documentId,
            kinds ?? (object)DBNull.Value,
            kinds ?? (object)DBNull.Value,
            minSeverity ?? (object)DBNull.Value,
            minSeverity ?? (object)DBNull.Value);
        using var activity2 = StartDbActivity(cmd.CommandText);
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return MapAnnotation(r);
    }

    private Annotation MapAnnotation(IDataRecord r)
    {
        var idOrdinal = r.GetOrdinal("id");
        var semanticKeyOrdinal = r.GetOrdinal("semantic_key");
        var kindOrdinal = r.GetOrdinal("kind");
        var severityOrdinal = r.GetOrdinal("severity");
        var sourceOrdinal = r.GetOrdinal("source");
        var ruleIdOrdinal = r.GetOrdinal("rule_id");
        var messageOrdinal = r.GetOrdinal("message");
        var dataOrdinal = r.GetOrdinal("data");
        var scopeDocumentOrdinal = r.GetOrdinal("scope_document_id");
        var targetNodeOrdinal = r.GetOrdinal("target_node_id");
        var targetEdgeOrdinal = r.GetOrdinal("target_edge_id");
        var targetSpanOrdinal = r.GetOrdinal("target_span_id");
        var targetUriOrdinal = r.GetOrdinal("target_uri");
        var createdAtOrdinal = r.GetOrdinal("created_at");
        var expiresAtOrdinal = r.GetOrdinal("expires_at");

        var dataJson = r.IsDBNull(dataOrdinal) ? "{}" : r.GetString(dataOrdinal);
        var data = JsonNode.Parse(dataJson)?.AsObject() ?? new JsonObject();

        RepoUri? targetUri = null;
        var rawTargetUri = r.GetValue(targetUriOrdinal);
        if (rawTargetUri is string raw && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                targetUri = RepoUri.Parse(raw);
            }
            catch (FormatException)
            {
                targetUri = null;
            }
        }

        return new Annotation
        {
            Id = r.GetGuid(idOrdinal),
            SemanticKey = r.IsDBNull(semanticKeyOrdinal) ? null : r.GetString(semanticKeyOrdinal),
            Kind = r.GetString(kindOrdinal),
            Severity = r.GetString(severityOrdinal),
            Source = r.GetString(sourceOrdinal),
            RuleId = r.IsDBNull(ruleIdOrdinal) ? null : r.GetString(ruleIdOrdinal),
            Message = r.GetString(messageOrdinal),
            Data = data,
            ScopeDocumentId = r.GetGuid(scopeDocumentOrdinal),
            TargetNodeId = r.IsDBNull(targetNodeOrdinal) ? null : r.GetGuid(targetNodeOrdinal),
            TargetEdgeId = r.IsDBNull(targetEdgeOrdinal) ? null : r.GetGuid(targetEdgeOrdinal),
            TargetSpanId = r.IsDBNull(targetSpanOrdinal) ? null : r.GetGuid(targetSpanOrdinal),
            TargetUri = targetUri,
            CreatedAt = DateTime.SpecifyKind(r.GetDateTime(createdAtOrdinal), DateTimeKind.Utc),
            ExpiresAt = r.IsDBNull(expiresAtOrdinal) ? null : DateTime.SpecifyKind(r.GetDateTime(expiresAtOrdinal), DateTimeKind.Utc)
        };
    }

    // ---------- helpers ----------
    

    private void EnableExtensions()
    {
        string[] exts = ["icu", "fts", "httpfs", "parquet", "sqlite_scanner"];
        foreach (var ext in exts)
        {
            TryExec($"INSTALL {ext};");
            TryExec($"LOAD {ext};");
        }

        TryExec("PRAGMA threads=" + Math.Max(1, Environment.ProcessorCount));
        TryExec("PRAGMA enable_object_cache=true;");
    }

    private bool ArtifactExists(Guid id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM artifact WHERE id=?;";
        AddParameters(cmd, id);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    private Node MapNode(IDataRecord r)
    {
        var uriStr = r.IsDBNull(2) ? null : r.GetString(2);
        RepoUri? repoUri = null;
        if (!string.IsNullOrEmpty(uriStr) && RepoUri.TryParse(uriStr, out var tmp)) repoUri = tmp;

        var propsJson = r.IsDBNull(6) ? "{}" : r.GetString(6);
        var props = JsonNode.Parse(propsJson)?.AsObject() ?? new JsonObject();

        return new Node
        {
            Id = r.GetGuid(0),
            Kind = r.GetString(1),
            Uri = repoUri,
            ArtifactId = r.IsDBNull(4) ? null : r.GetGuid(4),
            SpanId = r.IsDBNull(5) ? null : r.GetGuid(5),
            Props = props,
            CreatedAt = DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc),
            UpdatedAt = DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc)
        };
    }

    private Edge MapEdge(IDataRecord r)
    {
        var propsJson = r.IsDBNull(10) ? "{}" : r.GetString(10);
        var props = JsonNode.Parse(propsJson)?.AsObject() ?? new JsonObject();

        return new Edge
        {
            Id = r.GetGuid(0),
            SrcId = r.GetGuid(1),
            DstId = r.GetGuid(2),
            Type = r.GetString(3),
            IsComposition = r.GetBoolean(4),
            Ordinal = r.IsDBNull(5) ? null : r.GetInt32(5),
            ScopeDocumentId = r.IsDBNull(6) ? null : r.GetGuid(6),
            EdgeKey = r.IsDBNull(7) ? null : r.GetString(7),
            SrcSpanId = r.IsDBNull(8) ? null : r.GetGuid(8),
            DstSpanId = r.IsDBNull(9) ? null : r.GetGuid(9),
            Props = props,
            CreatedAt = DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc)
        };
    }

    private int Execute(string sql, params object?[] values)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, values);
        using var activity = StartDbActivity(sql);
        try
        {
            var rows = cmd.ExecuteNonQuery();
            activity?.SetTag("db.sql.rows_affected", rows);
            return rows;
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    private int Execute(string sql, IDbTransaction? tx, params object?[] values)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx as DuckDBTransaction;
        AddParameters(cmd, values);
        using var activity = StartDbActivity(sql);
        try
        {
            var rows = cmd.ExecuteNonQuery();
            activity?.SetTag("db.sql.rows_affected", rows);
            return rows;
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
    }

    private bool TryExec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            // Some PRAGMAs (e.g., drop_fts_index on first run) are best-effort. Avoid surfacing them as errors.
            try
            {
                var op = ExtractOperation(sql) ?? "SQL";
                _logger.LogDebug(ex,
                    "Ignored DuckDB best-effort command op={Operation} db={Db}",
                    op,
                    _databaseLabel ?? "(unknown)");
            }
            catch
            {
                // logging must not throw
            }
            return false;
        }
    }

    private static void AddParameters(DuckDBCommand cmd, params object?[] values)
    {
        foreach (var v in values)
            cmd.Parameters.Add(new DuckDBParameter { Value = v ?? DBNull.Value });
    }

    private static string LoadSqlResource(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path must be provided", nameof(relativePath));

        var assembly = typeof(DuckDbGraphStore).Assembly;
        var normalized = relativePath.Trim()
            .TrimStart('/', '\\')
            .Replace('/', '.')
            .Replace('\\', '.');
        var resourceName = $"{typeof(DuckDbGraphStore).Namespace}.Schema.{normalized}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource '{resourceName}' was not found for '{relativePath}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void ExecuteSqlResource(string relativePath, IDbTransaction? tx = null)
    {
        var sql = LoadSqlResource(relativePath);
        if (string.IsNullOrWhiteSpace(sql))
            return;

        if (tx is null)
        {
            Execute(sql);
        }
        else
        {
            Execute(sql, tx);
        }
    }

    private static SemanticMediaType? ParseMediaType(string? s)
    {
        return string.IsNullOrWhiteSpace(s) ? null : SemanticMediaType.Parse(s);
    }

    // ----- instrumentation helpers -----

    private Activity? StartDbActivity(string sql, [System.Runtime.CompilerServices.CallerMemberName] string? operationSource = null)
    {
        var op = ExtractOperation(sql) ?? "SQL";
        var activity = ActivitySource.StartActivity(op, ActivityKind.Client);
        if (activity is null) return null;

        activity.SetTag("db.system", "duckdb");
        if (!string.IsNullOrEmpty(_databaseLabel)) activity.SetTag("db.name", _databaseLabel);
        activity.SetTag("db.operation.name", op);
        activity.SetTag("db.operation", op);
        activity.SetTag("db.statement", TrimStatement(sql));
        if (!string.IsNullOrEmpty(operationSource)) activity.SetTag("code.function", operationSource);
        return activity;
    }

    private Activity? StartOperationActivity(string operationName, [System.Runtime.CompilerServices.CallerMemberName] string? method = null)
    {
        var activity = ActivitySource.StartActivity(operationName, ActivityKind.Client);
        if (activity is null) return null;

        activity.SetTag("db.system", "duckdb");
        if (!string.IsNullOrEmpty(_databaseLabel)) activity.SetTag("db.name", _databaseLabel);
        activity.SetTag("db.operation.name", operationName);
        activity.SetTag("db.operation", operationName);
        if (!string.IsNullOrEmpty(method)) activity.SetTag("code.function", method);
        return activity;
    }

    private static string? ExtractOperation(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        foreach (var ch in sql!)
        {
            if (!char.IsWhiteSpace(ch))
            {
                var span = sql.AsSpan(sql.IndexOf(ch));
                var end = 0;
                while (end < span.Length && char.IsLetter(span[end])) end++;
                return span[..end].ToString().ToUpperInvariant();
            }
        }
        return null;
    }

    private static string TrimStatement(string s)
    {
        const int max = 1024;
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private void RecordException(Activity? activity, Exception ex, string? sql = null, string? operation = null, [System.Runtime.CompilerServices.CallerMemberName] string? method = null)
    {
        if (activity is not null)
        {
            var tags = new ActivityTagsCollection
            {
                {"exception.type", ex.GetType().FullName},
                {"exception.message", ex.Message},
                {"exception.stacktrace", ex.ToString()}
            };
            activity.AddEvent(new ActivityEvent("exception", default, tags));
            activity.SetTag("otel.status_code", "ERROR");
            activity.SetTag("otel.status_description", ex.Message);
        }

        try
        {
            var op = operation ?? ExtractOperation(sql) ?? "SQL";
            _logger.LogError(ex,
                "DuckDB operation failed in {Method} op={Operation} db={Db}",
                method,
                op,
                _databaseLabel ?? "(unknown)");
        }
        catch { /* logging must not throw */ }
    }

    private static string? TryExtractDbNameSafe(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        try
        {
            var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                var kv = p.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                {
                    var value = kv[1];
                    if (string.Equals(value, ":memory:", StringComparison.OrdinalIgnoreCase)) return ":memory:";
                    try { return Path.GetFileName(value); } catch { return value; }
                }
            }
        }
        catch { }
        return null;
    }
}
