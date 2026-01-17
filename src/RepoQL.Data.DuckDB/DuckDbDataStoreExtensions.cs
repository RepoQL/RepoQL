using System.Data;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Extension methods for DuckDbDataStore providing high-level write operations.
/// These methods implement the IRepoDatabase write interface on top of the data store primitives.
/// </summary>
public static class DuckDbDataStoreExtensions
{
    #region URI Normalization

    /// <summary>
    /// Normalize a URI string for storage. Removes problematic characters, validates format,
    /// and ensures consistency to prevent constraint violations.
    /// </summary>
    private static string NormalizeUri(string? uri)
    {
        return RepoUri.Normalize(uri);
    }

    /// <summary>
    /// Normalize a RepoUri for storage.
    /// </summary>
    private static string NormalizeUri(RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return NormalizeUri(uri.ToString());
    }

    #endregion

    #region Public Write Methods

    /// <summary>
    /// Index a parsed artifact (content + nodes + spans + edges).
    /// Replaces any existing artifact at the same URI (inferred from DocumentNode.Uri).
    /// </summary>
    public static IndexResult IndexArtifact(this DuckDbDataStore store, ParsedArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(artifact);
        var uri = artifact.DocumentNode.Uri
            ?? throw new ArgumentException("DocumentNode.Uri must not be null", nameof(artifact));
        return IndexArtifact(store, uri, artifact);
    }

    /// <summary>
    /// Index a parsed artifact (content + nodes + spans + edges).
    /// Replaces any existing artifact at the same URI.
    /// </summary>
    public static IndexResult IndexArtifact(this DuckDbDataStore store, RepoUri uri, ParsedArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(artifact);

        return store.WriteTransaction((conn, tx) =>
        {
            // 1. Check if this is an update
            var existingDoc = GetDocumentByUri(conn, tx, uri);
            var isUpdate = existingDoc is not null;

            // 2. If updating, clean up old content BEFORE upserting (handles ID changes)
            if (existingDoc is not null)
            {
                CleanupDocumentContent(conn, tx, existingDoc.Id);
            }

            // 3. Upsert artifact
            var savedArtifact = UpsertArtifact(conn, tx, artifact.Artifact);

            // 4. Create document node with artifact ID
            var docNode = artifact.DocumentNode with { ArtifactId = savedArtifact.Id };

            // 5. Upsert document
            var savedDoc = UpsertDocumentByUri(conn, tx, uri, docNode, store.Logger);

            // 6. Remap children with correct artifact IDs
            var children = artifact.Children.Select(c =>
                c with { ArtifactId = c.ArtifactId == artifact.Artifact.Id ? savedArtifact.Id : c.ArtifactId }
            ).ToList();

            // 7. Remap spans with document ID
            var spans = artifact.Spans.Select(s => s with { DocumentId = savedDoc.Id }).ToList();

            // 8. Remap edges with scope document ID
            var edges = artifact.Edges.Select(e => e with { ScopeDocumentId = savedDoc.Id }).ToList();

            // 9. Insert new document content
            InsertDocumentContent(conn, tx, children, spans, edges, store.Logger);

            return new IndexResult(savedDoc.Id, isUpdate);
        });
    }

    /// <summary>
    /// Index multiple artifacts in a single transaction for better performance.
    /// </summary>
    public static IReadOnlyList<IndexResult> IndexArtifactBatch(this DuckDbDataStore store, IReadOnlyList<(RepoUri Uri, ParsedArtifact Artifact)> items)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
            return [];

        return store.WriteTransaction((conn, tx) =>
        {
            var results = new List<IndexResult>(items.Count);
            foreach (var (uri, artifact) in items)
            {
                // 1. Check if this is an update
                var existingDoc = GetDocumentByUri(conn, tx, uri);
                var isUpdate = existingDoc is not null;

                // 2. If updating, clean up old content BEFORE upserting (handles ID changes)
                if (existingDoc is not null)
                {
                    CleanupDocumentContent(conn, tx, existingDoc.Id);
                }

                // 3. Upsert artifact
                var savedArtifact = UpsertArtifact(conn, tx, artifact.Artifact);

                // 4. Create document node with artifact ID
                var docNode = artifact.DocumentNode with { ArtifactId = savedArtifact.Id };

                // 5. Upsert document
                var savedDoc = UpsertDocumentByUri(conn, tx, uri, docNode, store.Logger);

                // 6. Remap children with correct artifact IDs
                var children = artifact.Children.Select(c =>
                    c with { ArtifactId = c.ArtifactId == artifact.Artifact.Id ? savedArtifact.Id : c.ArtifactId }
                ).ToList();

                // 7. Remap spans with document ID
                var spans = artifact.Spans.Select(s => s with { DocumentId = savedDoc.Id }).ToList();

                // 8. Remap edges with scope document ID
                var edges = artifact.Edges.Select(e => e with { ScopeDocumentId = savedDoc.Id }).ToList();

                // 9. Insert new document content
                InsertDocumentContent(conn, tx, children, spans, edges, store.Logger);

                results.Add(new IndexResult(savedDoc.Id, isUpdate));
            }

            return results;
        });
    }

    /// <summary>
    /// Delete an artifact and its entire subtree by URI.
    /// </summary>
    public static bool DeleteArtifact(this DuckDbDataStore store, RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(uri);

        return store.WriteTransaction((conn, tx) =>
        {
            var doc = GetDocumentByUri(conn, tx, uri);
            if (doc is null)
                return false;

            DeleteSubtree(conn, tx, doc.Id);
            return true;
        });
    }

    /// <summary>
    /// Replace annotations for an artifact. Deletes existing annotations from
    /// the specified sources, then inserts the new ones.
    /// </summary>
    public static bool ReplaceAnnotations(this DuckDbDataStore store, RepoUri artifactUri, IReadOnlyList<Annotation> annotations, IReadOnlyCollection<string>? sourcesToClear = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(artifactUri);
        ArgumentNullException.ThrowIfNull(annotations);

        return store.WriteTransaction((conn, tx) =>
        {
            var doc = GetDocumentByUri(conn, tx, artifactUri);
            if (doc is null)
                return false;

            // Get sources to clear - either explicitly provided or inferred from annotations
            var sources = sourcesToClear?.ToHashSet(StringComparer.Ordinal)
                ?? annotations
                    .Where(a => !string.IsNullOrEmpty(a.Source))
                    .Select(a => a.Source!)
                    .Distinct()
                    .ToHashSet(StringComparer.Ordinal);

            // Delete existing annotations from these sources
            if (sources.Count > 0)
            {
                var existing = GetAnnotationsForDocument(conn, tx, doc.Id);
                foreach (var stale in existing.Where(a => sources.Contains(a.Source ?? "")))
                {
                    DeleteAnnotation(conn, tx, stale.Id);
                }
            }

            // Insert new annotations
            foreach (var annotation in annotations)
            {
                var withDocId = annotation with { ScopeDocumentId = doc.Id };
                UpsertAnnotation(conn, tx, withDocId);
            }

            return true;
        });
    }

    /// <summary>
    /// Write embeddings in batch. Supports both structure and full embeddings,
    /// document and object scope, and chunked content.
    /// </summary>
    public static void WriteEmbeddings(this DuckDbDataStore store, IReadOnlyList<DocumentEmbedding> embeddings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embeddings);

        if (embeddings.Count == 0)
            return;

        const int defaultBatchSize = 128; // keeps SQL+parameter count bounded

        store.WriteTransaction((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var offset = 0; offset < embeddings.Count; offset += defaultBatchSize)
            {
                cmd.Parameters.Clear();

                var batchCount = Math.Min(defaultBatchSize, embeddings.Count - offset);
                var sb = new StringBuilder(capacity: 512 + (batchCount * 128));
                sb.AppendLine("""
                    INSERT INTO document_embedding(doc_id, node_id, chunk_index, embedding_type, uri, scope, model, dim, embedding, start_byte, end_byte, updated_at)
                    VALUES
                    """);

                for (var i = 0; i < batchCount; i++)
                {
                    if (i > 0) sb.AppendLine(",");

                    sb.Append("(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, CURRENT_TIMESTAMP)");

                    var e = embeddings[offset + i];
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.DocumentId });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.NodeId });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.ChunkIndex });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.EmbeddingType });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.Uri });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.Scope });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.Model });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.Dimension });
                    cmd.Parameters.Add(new DuckDBParameter { Value = new List<float>(e.Vector) });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.StartByte ?? (object)DBNull.Value });
                    cmd.Parameters.Add(new DuckDBParameter { Value = e.EndByte ?? (object)DBNull.Value });
                }

                sb.AppendLine("""

                    ON CONFLICT(doc_id, node_id, chunk_index, embedding_type) DO UPDATE SET
                        uri=excluded.uri,
                        scope=excluded.scope,
                        model=excluded.model,
                        dim=excluded.dim,
                        embedding=excluded.embedding,
                        start_byte=excluded.start_byte,
                        end_byte=excluded.end_byte,
                        updated_at=excluded.updated_at;
                    """);

                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Execute a read-only query and return results as dictionary rows.
    /// Used for dynamic SQL execution (MCP queries, CLI).
    /// Uses DuckDB's read-only transaction mode to enforce that no writes can occur,
    /// regardless of what SQL is passed. This is enforced at the database engine level.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(this DuckDbDataStore store, string sql)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sql);

        // Use ReadUntrusted which wraps the query in a read-only transaction
        // DuckDB enforces this at the engine level - any write attempt will fail
        return store.ReadUntrusted(sql, reader =>
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                dict[name] = value;
            }
            return (IReadOnlyDictionary<string, object?>)dict;
        });
    }

    /// <summary>
    /// Get a document node by its URI (for testing and diagnostics).
    /// </summary>
    public static Node? GetDocumentByUri(this DuckDbDataStore store, RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(uri);

        var lc = NormalizeUri(uri.Container.AbsoluteUri).ToLowerInvariant();
        return store.Read(
            $"SELECT id, kind, uri, artifact_id, span_id, properties, headline, structure, created_at, updated_at FROM node WHERE container_uri_lowercase = '{lc}'",
            r => r.MapToNode()).FirstOrDefault();
    }

    /// <summary>
    /// Alias for Query - executes raw SQL and returns dictionary rows.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> RawQuery(this DuckDbDataStore store, string sql)
        => Query(store, sql);

    /// <summary>
    /// Get all nodes in the database.
    /// </summary>
    public static IReadOnlyList<Node> GetAllNodes(this DuckDbDataStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.Read(
            "SELECT id, kind, uri, artifact_id, span_id, properties, headline, structure, created_at, updated_at FROM node",
            r => r.MapToNode());
    }

    /// <summary>
    /// Get an artifact by ID.
    /// </summary>
    public static Artifact? GetArtifact(this DuckDbDataStore store, Guid id)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.Read(
            $"SELECT id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure, token_count FROM artifact WHERE id = '{id}'",
            r => r.MapToArtifact()).FirstOrDefault();
    }

    /// <summary>
    /// Upsert an artifact (insert or update by digest).
    /// </summary>
    public static Artifact UpsertArtifact(this DuckDbDataStore store, Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(artifact);

        return store.WriteTransaction((conn, tx) => UpsertArtifact(conn, tx, artifact));
    }

    /// <summary>
    /// Upsert a document node by URI.
    /// </summary>
    public static Node UpsertDocumentByUri(this DuckDbDataStore store, RepoUri uri, Node document)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(document);

        return store.WriteTransaction((conn, tx) => UpsertDocumentByUri(conn, tx, uri, document, store.Logger));
    }

    /// <summary>
    /// Upsert a node.
    /// </summary>
    public static Node UpsertNode(this DuckDbDataStore store, Node node)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(node);

        return store.WriteTransaction((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO node (id, kind, uri, container_uri_lowercase, artifact_id, span_id, properties, headline, structure, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT (id) DO UPDATE SET
                    kind = excluded.kind,
                    uri = excluded.uri,
                    container_uri_lowercase = excluded.container_uri_lowercase,
                    artifact_id = excluded.artifact_id,
                    span_id = excluded.span_id,
                    properties = excluded.properties,
                    headline = excluded.headline,
                    structure = excluded.structure,
                    updated_at = excluded.updated_at;
                """;
            var uriStr = node.Uri is not null ? NormalizeUri(node.Uri) : null;
            var containerLc = uriStr?.ToLowerInvariant();
            cmd.AddParameters(node.Id, node.Kind, uriStr, containerLc,
                node.ArtifactId, node.SpanId, node.Props?.ToJsonString(),
                node.Headline, node.Structure, node.CreatedAt, node.UpdatedAt);
            cmd.ExecuteNonQuery();
            return node;
        });
    }

    /// <summary>
    /// Get annotations for a document.
    /// </summary>
    public static IReadOnlyList<Annotation> GetAnnotationsForDocument(this DuckDbDataStore store, Guid documentId)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.Read(
            $"SELECT id, semantic_key, kind, severity, source, rule_id, message, data, scope_document_id, target_node_id, target_edge_id, target_span_id, target_uri, created_at, expires_at FROM annotation WHERE scope_document_id = '{documentId}'",
            r => r.MapToAnnotation());
    }

    /// <summary>
    /// Upsert an annotation.
    /// </summary>
    public static void UpsertAnnotation(this DuckDbDataStore store, Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(annotation);

        store.WriteTransaction((conn, tx) => UpsertAnnotation(conn, tx, annotation));
    }

    /// <summary>
    /// Get an annotation by ID.
    /// </summary>
    public static Annotation? GetAnnotation(this DuckDbDataStore store, Guid id)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.Read(
            $"SELECT id, semantic_key, kind, severity, source, rule_id, message, data, scope_document_id, target_node_id, target_edge_id, target_span_id, target_uri, created_at, expires_at FROM annotation WHERE id = '{id}'",
            r => r.MapToAnnotation()).FirstOrDefault();
    }

    #endregion

    #region File System Mount Methods

    /// <summary>
    /// Save or update a file system mount record.
    /// </summary>
    public static void SaveMount(this DuckDbDataStore store, FileSystemMountRecord mount)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mount);

        store.WriteTransaction((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO file_system_mount (id, scheme, authority, path_prefix, source_uri, local_path, mounted_at, include_in_enumeration, enable_watching, enable_analysis)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT (id) DO UPDATE SET
                    scheme = excluded.scheme,
                    authority = excluded.authority,
                    path_prefix = excluded.path_prefix,
                    source_uri = excluded.source_uri,
                    local_path = excluded.local_path,
                    mounted_at = excluded.mounted_at,
                    include_in_enumeration = excluded.include_in_enumeration,
                    enable_watching = excluded.enable_watching,
                    enable_analysis = excluded.enable_analysis;
                """;
            cmd.AddParameters(
                mount.Id,
                mount.Scheme,
                mount.Authority,
                mount.PathPrefix,
                mount.SourceUri,
                mount.LocalPath,
                mount.MountedAt ?? DateTimeOffset.UtcNow,
                mount.IncludeInEnumeration,
                mount.EnableWatching,
                mount.EnableAnalysis);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Delete a file system mount record by ID.
    /// </summary>
    public static bool DeleteMount(this DuckDbDataStore store, string id)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return store.WriteTransaction((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM file_system_mount WHERE id = ?;";
            cmd.AddParameters(id);
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>
    /// Get all persisted file system mounts.
    /// </summary>
    public static IReadOnlyList<FileSystemMountRecord> GetAllMounts(this DuckDbDataStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        return store.Read(
            "SELECT id, scheme, authority, path_prefix, source_uri, local_path, mounted_at, include_in_enumeration, enable_watching, enable_analysis FROM file_system_mount ORDER BY mounted_at",
            MapToMountRecord);
    }

    /// <summary>
    /// Get a single mount by ID.
    /// </summary>
    public static FileSystemMountRecord? GetMount(this DuckDbDataStore store, string id)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return store.Read(
            $"SELECT id, scheme, authority, path_prefix, source_uri, local_path, mounted_at, include_in_enumeration, enable_watching, enable_analysis FROM file_system_mount WHERE id = '{id}'",
            MapToMountRecord).FirstOrDefault();
    }

    private static FileSystemMountRecord MapToMountRecord(IDataRecord r)
    {
        return new FileSystemMountRecord
        {
            Id = r.GetString(0),
            Scheme = r.GetString(1),
            Authority = r.IsDBNull(2) ? null : r.GetString(2),
            PathPrefix = r.GetString(3),
            SourceUri = r.GetString(4),
            LocalPath = r.GetString(5),
            MountedAt = r.IsDBNull(6) ? null : r.GetDateTime(6),
            IncludeInEnumeration = r.GetBoolean(7),
            EnableWatching = r.GetBoolean(8),
            EnableAnalysis = r.GetBoolean(9)
        };
    }

    #endregion

    #region Private Helper Methods

    private static Node? GetDocumentByUri(DuckDBConnection conn, DuckDBTransaction tx, RepoUri uri)
    {
        var lc = NormalizeUri(uri.Container.AbsoluteUri).ToLowerInvariant();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, kind, uri, artifact_id, span_id, properties, headline, structure, created_at, updated_at FROM node WHERE container_uri_lowercase = ?;";
        cmd.AddParameters(lc);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return reader.MapToNode();
    }

    private static Artifact UpsertArtifact(DuckDBConnection conn, DuckDBTransaction tx, Artifact artifact)
    {
        // Check if artifact with same digest exists
        using var check = conn.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure, token_count FROM artifact WHERE digest = ?;";
        check.AddParameters(artifact.Digest);
        using var reader = check.ExecuteReader();
        if (reader.Read())
            return reader.MapToArtifact();

        reader.Close();

        // Insert new
        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO artifact (id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure, token_count)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """;
        ins.AddParameters(artifact.Id, artifact.Digest, artifact.Size, artifact.MediaType?.ToString(),
            artifact.Text, artifact.StoreUri?.ToString(), artifact.Headline, artifact.Summary, artifact.Structure, artifact.TokenCount);
        ins.ExecuteNonQuery();

        return artifact;
    }

    private static Node UpsertDocumentByUri(
        DuckDBConnection conn,
        DuckDBTransaction tx,
        RepoUri uri,
        Node document,
        ILogger logger)
    {
        var uriStr = NormalizeUri(uri.Container.AbsoluteUri);
        var lc = uriStr.ToLowerInvariant();
        List<(Guid Id, string Kind, string? Uri)>? preDelete = null;
        List<(Guid Id, string Kind, string? Uri)>? postDelete = null;
        List<(Guid Id, string Kind, string? Uri)>? postFailure = null;

        List<(Guid Id, string Kind, string? Uri)> ReadConflicts()
        {
            using var probe = conn.CreateCommand();
            probe.Transaction = tx;
            probe.CommandText = "SELECT id, kind, uri FROM node WHERE container_uri_lowercase = ?;";
            probe.AddParameters(lc);

            using var reader = probe.ExecuteReader();
            var rows = new List<(Guid, string, string?)>();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)
                ));
            }
            return rows;
        }

        List<(Guid Id, string Kind, string? Uri)>? ReadConflictsOutside()
        {
            try
            {
                var dataSource = TryGetDataSource(conn.ConnectionString);
                if (string.IsNullOrWhiteSpace(dataSource))
                    return null;

                if (string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
                    return null;

                using var alt = new DuckDBConnection($"Data Source={dataSource};ACCESS_MODE=READ_ONLY");
                alt.Open();
                using var probe = alt.CreateCommand();
                probe.CommandText = "SELECT id, kind, uri FROM node WHERE container_uri_lowercase = ?;";
                probe.Parameters.Add(new DuckDBParameter(lc));

                using var reader = probe.ExecuteReader();
                var rows = new List<(Guid, string, string?)>();
                while (reader.Read())
                {
                    rows.Add((
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)
                    ));
                }
                return rows;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[DuckDB] uri-conflict external-read failed");
                return null;
            }
        }

        static string? TryGetDataSource(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return null;

            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                    return trimmed["Data Source=".Length..].Trim();
                if (trimmed.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase))
                    return trimmed["DataSource=".Length..].Trim();
            }

            return null;
        }

        void LogConflict(string stage, IReadOnlyList<(Guid Id, string Kind, string? Uri)> rows, Exception ex)
        {
            var pid = Environment.ProcessId;
            var tid = Environment.CurrentManagedThreadId;
            logger.LogError(ex,
                "[DuckDB] URI conflict {Stage} pid={Pid} tid={Tid} docId={DocId} lc={Lc} uri={Uri}",
                stage, pid, tid, document.Id, lc, uriStr);

            if (preDelete is not null)
                DumpRows("pre-delete", preDelete);
            if (postDelete is not null)
                DumpRows("post-delete", postDelete);
            if (postFailure is not null)
                DumpRows("post-failure", postFailure);

            DumpRows("current", rows);
        }

        void DumpRows(string label, IReadOnlyList<(Guid Id, string Kind, string? Uri)> rows)
        {
            logger.LogWarning("[DuckDB] uri-conflict {Label} count={Count}", label, rows.Count);
            foreach (var row in rows)
                logger.LogWarning("[DuckDB] uri-conflict {Label} id={Id} kind={Kind} uri={Uri}",
                    label, row.Id, row.Kind, row.Uri ?? string.Empty);
        }

        // Clean up any orphaned row with same URI but different id
        // This handles dirty startup scenarios where deterministic IDs may differ from stored IDs
        preDelete = ReadConflicts();
        conn.Execute(tx, "DELETE FROM node WHERE container_uri_lowercase = ? AND id != ?;", lc, document.Id);
        postDelete = ReadConflicts();

        // Atomic upsert using INSERT ... ON CONFLICT ... DO UPDATE
        // Handle conflicts on id (primary key) since deterministic IDs may match existing rows
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO node (id, kind, uri, container_uri_lowercase, artifact_id, span_id, properties, headline, structure, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT (id) DO UPDATE SET
                kind = excluded.kind,
                uri = excluded.uri,
                container_uri_lowercase = excluded.container_uri_lowercase,
                artifact_id = excluded.artifact_id,
                span_id = excluded.span_id,
                properties = excluded.properties,
                headline = excluded.headline,
                structure = excluded.structure,
                updated_at = excluded.updated_at
            RETURNING id;
            """;
        cmd.AddParameters(document.Id, document.Kind, uriStr, lc, document.ArtifactId, document.SpanId,
            document.Props.ToJsonString(), document.Headline, document.Structure,
            document.CreatedAt.UtcDateTime, document.UpdatedAt.UtcDateTime);

        try
        {
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var id = reader.GetGuid(0);
                return document with { Id = id };
            }
        }
        catch (DuckDBException ex)
        {
            var rows = postDelete ?? preDelete ?? new List<(Guid, string, string?)>();
            postFailure = ReadConflictsOutside();
            LogConflict("insert-failed", rows, ex);
            throw;
        }

        // Fallback (should not happen with RETURNING)
        return document;
    }

    /// <summary>
    /// Clean up all content (spans, edges, child nodes) for a document.
    /// Called before upserting to handle document ID changes on reindex.
    /// </summary>
    private static void CleanupDocumentContent(DuckDBConnection conn, DuckDBTransaction tx, Guid documentId)
    {
        // Collect existing composition subtree (child nodes to delete)
        var childNodesToDelete = new HashSet<Guid>();
        var queue = new Queue<Guid>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT destination_node_id FROM edge WHERE source_node_id = ? AND is_composition = TRUE;";
            cmd.AddParameters(documentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                queue.Enqueue(r.GetGuid(0));
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!childNodesToDelete.Add(cur))
                continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT destination_node_id FROM edge WHERE source_node_id = ? AND is_composition = TRUE;";
            cmd.AddParameters(cur);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                queue.Enqueue(r.GetGuid(0));
        }

        // Delete spans and edges scoped to this document
        conn.Execute(tx, "DELETE FROM span WHERE document_id = ?;", documentId);
        conn.Execute(tx, "DELETE FROM edge WHERE scope_document_id = ?;", documentId);

        // Delete old child nodes in batch
        BatchDeleteByIds(conn, tx, childNodesToDelete, "node", "id");
    }

    /// <summary>
    /// Insert new document content (spans, edges, child nodes).
    /// </summary>
    private static void InsertDocumentContent(
        DuckDBConnection conn,
        DuckDBTransaction tx,
        IReadOnlyList<Node> children,
        IReadOnlyList<Span> spans,
        IReadOnlyList<Edge> edges,
        ILogger? logger)
    {
        // Bulk insert spans (much faster than individual inserts)
        if (spans.Count > 0)
            BulkInsertSpans(conn, tx, spans);

        // Bulk insert child nodes
        if (children.Count > 0)
            BulkInsertNodes(conn, tx, children);

        // Bulk insert edges
        if (edges.Count > 0)
            BulkInsertEdges(conn, tx, edges, logger);
    }

    private static void BulkInsertSpans(DuckDBConnection conn, DuckDBTransaction tx, IReadOnlyList<Span> spans)
    {
        const int batchSize = 100; // DuckDB handles large parameter lists well
        for (var offset = 0; offset < spans.Count; offset += batchSize)
        {
            var batch = spans.Skip(offset).Take(batchSize).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("INSERT INTO span (id, document_id, start_line, start_column, end_line, end_column, start_byte, end_byte) VALUES");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = i * 8;
                sb.Append($"(${p + 1},${p + 2},${p + 3},${p + 4},${p + 5},${p + 6},${p + 7},${p + 8})");

                var span = batch[i];
                cmd.Parameters.Add(new DuckDBParameter { Value = span.Id });
                cmd.Parameters.Add(new DuckDBParameter { Value = span.DocumentId });
                cmd.Parameters.Add(new DuckDBParameter { Value = span.StartLine });
                cmd.Parameters.Add(new DuckDBParameter { Value = span.StartColumn });
                cmd.Parameters.Add(new DuckDBParameter { Value = span.EndLine });
                cmd.Parameters.Add(new DuckDBParameter { Value = span.EndColumn });
                cmd.Parameters.Add(new DuckDBParameter { Value = span.StartByte });
                cmd.Parameters.Add(new DuckDBParameter { Value = span.EndByte });
            }

            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }
    }

    private static void BulkInsertNodes(DuckDBConnection conn, DuckDBTransaction tx, IReadOnlyList<Node> nodes)
    {
        // Sort for zonemap efficiency: queries typically filter by kind first, then URI patterns.
        // Sorting before insert creates tighter min/max ranges per row group, enabling DuckDB to skip
        // entire row groups that don't match filter predicates (potential 10x+ improvement on selective queries).
        var sortedNodes = nodes
            .OrderBy(n => n.Kind)
            .ThenBy(n => n.Uri is not null ? NormalizeUri(n.Uri).ToLowerInvariant() : null)
            .ToList();

        const int batchSize = 50; // Nodes have more columns, smaller batches
        for (var offset = 0; offset < sortedNodes.Count; offset += batchSize)
        {
            var batch = sortedNodes.Skip(offset).Take(batchSize).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("INSERT INTO node (id, kind, uri, container_uri_lowercase, artifact_id, span_id, properties, headline, structure, created_at, updated_at) VALUES");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = i * 11;
                sb.Append($"(${p + 1},${p + 2},${p + 3},${p + 4},${p + 5},${p + 6},${p + 7},${p + 8},${p + 9},${p + 10},${p + 11})");

                var node = batch[i];
                var uriStr = node.Uri is not null ? NormalizeUri(node.Uri) : null;
                cmd.Parameters.Add(new DuckDBParameter { Value = node.Id });
                cmd.Parameters.Add(new DuckDBParameter { Value = node.Kind });
                cmd.Parameters.Add(new DuckDBParameter { Value = uriStr ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = uriStr?.ToLowerInvariant() ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = node.ArtifactId });
                cmd.Parameters.Add(new DuckDBParameter { Value = node.SpanId ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = node.Props.ToJsonString() ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = node.Headline ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = node.Structure ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = node.CreatedAt.UtcDateTime });
                cmd.Parameters.Add(new DuckDBParameter { Value = node.UpdatedAt.UtcDateTime });
            }

            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }
    }

    private static void BulkInsertEdges(DuckDBConnection conn, DuckDBTransaction tx, IReadOnlyList<Edge> edges, ILogger? logger)
    {
        // Deduplicate composition edges - each child can only have one parent
        // Keep the first edge for each composition_child_id (DstId when IsComposition=true)
        var seenCompositionChildren = new HashSet<Guid>();
        var compositionEdges = new List<Edge>();
        var referenceEdges = new List<Edge>();
        var duplicateCount = 0;

        foreach (var edge in edges)
        {
            if (edge.IsComposition && edge.DstId.HasValue)
            {
                if (!seenCompositionChildren.Add(edge.DstId.Value))
                {
                    duplicateCount++;
                    continue; // Skip duplicate composition edge
                }
                compositionEdges.Add(edge);
            }
            else
            {
                referenceEdges.Add(edge);
            }
        }

        if (duplicateCount > 0)
        {
            logger?.LogWarning(
                "[DuckDB] Skipped {Count} duplicate composition edges (same child with multiple parents)",
                duplicateCount);
        }

        // Insert composition edges with ON CONFLICT handling (for deterministic IDs)
        if (compositionEdges.Count > 0)
            BulkInsertEdgesBatch(conn, tx, compositionEdges, useConflictHandling: true);

        // Insert reference edges without ON CONFLICT (NULL composition_child_id can't conflict)
        if (referenceEdges.Count > 0)
            BulkInsertEdgesBatch(conn, tx, referenceEdges, useConflictHandling: false);
    }

    private static void BulkInsertEdgesBatch(DuckDBConnection conn, DuckDBTransaction tx, IReadOnlyList<Edge> edges, bool useConflictHandling)
    {
        // Sort for zonemap efficiency: deletion and traversal queries filter by document, then edge type.
        // Sorting before insert creates tighter min/max ranges per row group, enabling DuckDB to skip
        // entire row groups that don't match filter predicates (potential 10x+ improvement on selective queries).
        var sortedEdges = edges
            .OrderBy(e => e.ScopeDocumentId)
            .ThenBy(e => e.Type)
            .ToList();

        const int batchSize = 50; // Edges have many columns
        for (var offset = 0; offset < sortedEdges.Count; offset += batchSize)
        {
            var batch = sortedEdges.Skip(offset).Take(batchSize).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("INSERT INTO edge (id, source_node_id, destination_node_id, destination_uri, type, is_composition, ordinal, scope_document_id, semantic_key, source_span_id, destination_span_id, composition_child_id, properties, created_at) VALUES");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = i * 14;
                sb.Append($"(${p + 1},${p + 2},${p + 3},${p + 4},${p + 5},${p + 6},${p + 7},${p + 8},${p + 9},${p + 10},${p + 11},${p + 12},${p + 13},${p + 14})");

                var edge = batch[i];
                var compositionChildId = edge.IsComposition ? edge.DstId : (Guid?)null;
                var dstUriStr = edge.DstUri is not null ? NormalizeUri(edge.DstUri.ToString()) : null;
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.Id });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.SrcId });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.DstId });
                cmd.Parameters.Add(new DuckDBParameter { Value = dstUriStr ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.Type });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.IsComposition });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.Ordinal });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.ScopeDocumentId ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.EdgeKey ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.SrcSpanId ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.DstSpanId ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = compositionChildId ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.Props.ToJsonString() ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.CreatedAt.UtcDateTime });
            }

            // Only use ON CONFLICT for composition edges - reference edges have NULL composition_child_id
            // which can cause false conflicts in some DuckDB versions
            if (useConflictHandling)
            {
                sb.AppendLine();
                sb.AppendLine(@"ON CONFLICT (composition_child_id) DO UPDATE SET
    id = excluded.id,
    source_node_id = excluded.source_node_id,
    destination_node_id = excluded.destination_node_id,
    destination_uri = excluded.destination_uri,
    type = excluded.type,
    is_composition = excluded.is_composition,
    ordinal = excluded.ordinal,
    scope_document_id = excluded.scope_document_id,
    semantic_key = excluded.semantic_key,
    source_span_id = excluded.source_span_id,
    destination_span_id = excluded.destination_span_id,
    properties = excluded.properties,
    created_at = excluded.created_at");
            }

            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<Annotation> GetAnnotationsForDocument(DuckDBConnection conn, DuckDBTransaction tx, Guid documentId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, semantic_key, kind, severity, source, rule_id, message, data,
                   scope_document_id, target_node_id, target_edge_id, target_span_id, target_uri,
                   created_at, expires_at
            FROM annotation WHERE scope_document_id = ?;
            """;
        cmd.AddParameters(documentId);
        using var reader = cmd.ExecuteReader();

        var results = new List<Annotation>();
        while (reader.Read())
            results.Add(reader.MapToAnnotation());
        return results;
    }

    private static void DeleteAnnotation(DuckDBConnection conn, DuckDBTransaction tx, Guid id)
    {
        conn.Execute(tx, "DELETE FROM annotation WHERE id = ?;", id);
    }

    private static void UpsertAnnotation(DuckDBConnection conn, DuckDBTransaction tx, Annotation a)
    {
        using var cmd = conn.CreateCommand();
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

        cmd.AddParameters(a.Id, a.SemanticKey, a.Kind, a.Severity, a.Source, a.RuleId, a.Message, a.Data.ToJsonString(),
            a.ScopeDocumentId, a.TargetNodeId, a.TargetEdgeId, a.TargetSpanId, a.TargetUri?.ToString(),
            a.CreatedAt.UtcDateTime, a.ExpiresAt?.UtcDateTime);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteSubtree(DuckDBConnection conn, DuckDBTransaction tx, Guid rootId)
    {
        // Collect subtree
        var toDelete = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT destination_node_id FROM edge WHERE source_node_id = ? AND is_composition = TRUE;";
            cmd.AddParameters(cur);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var child = r.GetGuid(0);
                if (toDelete.Add(child))
                    queue.Enqueue(child);
            }
        }

        // Delete in order: document_embedding, annotations, edges, spans, nodes
        // Use batch DELETE with IN clause for better performance
        BatchDeleteByIds(conn, tx, toDelete, "document_embedding", "doc_id");
        BatchDeleteByIds(conn, tx, toDelete, "annotation", "scope_document_id");

        // Delete edges scoped to this document, or composition edges where this node is the source
        // Note: We intentionally don't delete edges where destination_node_id matches because those
        // are reference edges from OTHER documents pointing TO this one - they become dangling references
        BatchDeleteEdgesByIds(conn, tx, toDelete);

        BatchDeleteByIds(conn, tx, toDelete, "span", "document_id");
        BatchDeleteByIds(conn, tx, toDelete, "node", "id");
    }

    /// <summary>
    /// Batch delete records from a table where a column matches any of the given IDs.
    /// Uses IN clause for efficiency and chunks large lists to avoid query length limits.
    /// </summary>
    private static void BatchDeleteByIds(
        DuckDBConnection conn,
        DuckDBTransaction tx,
        IReadOnlyCollection<Guid> ids,
        string tableName,
        string columnName,
        int chunkSize = 1000)
    {
        if (ids.Count == 0)
            return;

        foreach (var chunk in ids.Chunk(chunkSize))
        {
            var idList = string.Join(",", chunk.Select(id => $"'{id}'"));
            conn.Execute(tx, $"DELETE FROM {tableName} WHERE {columnName} IN ({idList});");
        }
    }

    /// <summary>
    /// Batch delete edges where scope_document_id OR source_node_id matches any of the given IDs.
    /// Uses IN clause for efficiency and chunks large lists to avoid query length limits.
    /// </summary>
    private static void BatchDeleteEdgesByIds(
        DuckDBConnection conn,
        DuckDBTransaction tx,
        IReadOnlyCollection<Guid> ids,
        int chunkSize = 1000)
    {
        if (ids.Count == 0)
            return;

        foreach (var chunk in ids.Chunk(chunkSize))
        {
            var idList = string.Join(",", chunk.Select(id => $"'{id}'"));
            conn.Execute(tx, $"DELETE FROM edge WHERE scope_document_id IN ({idList}) OR source_node_id IN ({idList});");
        }
    }

    #endregion

}
