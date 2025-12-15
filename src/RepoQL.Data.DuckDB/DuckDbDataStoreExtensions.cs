using System.Data;
using System.Text;
using DuckDB.NET.Data;
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

            // 2. Upsert artifact
            var savedArtifact = UpsertArtifact(conn, tx, artifact.Artifact);

            // 3. Create document node with artifact ID
            var docNode = artifact.DocumentNode with { ArtifactId = savedArtifact.Id };

            // 4. Upsert document
            var savedDoc = UpsertDocumentByUri(conn, tx, uri, docNode);

            // 5. Remap children with correct artifact IDs
            var children = artifact.Children.Select(c =>
                c with { ArtifactId = c.ArtifactId == artifact.Artifact.Id ? savedArtifact.Id : c.ArtifactId }
            ).ToList();

            // 6. Remap spans with document ID
            var spans = artifact.Spans.Select(s => s with { DocumentId = savedDoc.Id }).ToList();

            // 7. Remap edges with scope document ID
            var edges = artifact.Edges.Select(e => e with { ScopeDocumentId = savedDoc.Id }).ToList();

            // 8. Replace document content
            ReplaceDocumentContent(conn, tx, savedDoc.Id, children, spans, edges);

            // 9. Upsert document_search projection
            UpsertDocumentSearch(conn, tx, savedDoc.Id, uri);

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

                // 2. Upsert artifact
                var savedArtifact = UpsertArtifact(conn, tx, artifact.Artifact);

                // 3. Create document node with artifact ID
                var docNode = artifact.DocumentNode with { ArtifactId = savedArtifact.Id };

                // 4. Upsert document
                var savedDoc = UpsertDocumentByUri(conn, tx, uri, docNode);

                // 5. Remap children with correct artifact IDs
                var children = artifact.Children.Select(c =>
                    c with { ArtifactId = c.ArtifactId == artifact.Artifact.Id ? savedArtifact.Id : c.ArtifactId }
                ).ToList();

                // 6. Remap spans with document ID
                var spans = artifact.Spans.Select(s => s with { DocumentId = savedDoc.Id }).ToList();

                // 7. Remap edges with scope document ID
                var edges = artifact.Edges.Select(e => e with { ScopeDocumentId = savedDoc.Id }).ToList();

                // 8. Replace document content
                ReplaceDocumentContent(conn, tx, savedDoc.Id, children, spans, edges);

                // 9. Upsert document_search projection
                UpsertDocumentSearch(conn, tx, savedDoc.Id, uri);

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

        store.WriteTransaction((conn, tx) =>
        {
            foreach (var e in embeddings)
            {
                var vector = new List<float>(e.Vector);
                using var cmd = conn.CreateCommand();
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
                cmd.AddParameters(e.DocumentId, e.NodeId, e.ChunkIndex, e.EmbeddingType,
                    e.Uri, e.Scope, e.Model, e.Dimension, vector, e.StartByte, e.EndByte);
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Refresh the search projection for all documents.
    /// </summary>
    public static void RefreshSearchProjection(this DuckDbDataStore store, bool incremental = false)
    {
        ArgumentNullException.ThrowIfNull(store);

        store.WriteTransaction((conn, tx) =>
        {
            if (incremental)
            {
                // Only insert documents that are missing from the projection
                conn.Execute(tx, """
                    INSERT INTO document_search (doc_id, uri, search_key, basename, dirname)
                    SELECT
                        n.id,
                        n.uri,
                        LOWER(REPLACE(n.uri, '\', '/')),
                        CASE
                            WHEN POSITION('/' IN REPLACE(n.uri, '\', '/')) > 0
                            THEN SUBSTRING(REPLACE(n.uri, '\', '/') FROM LENGTH(REPLACE(n.uri, '\', '/')) - POSITION('/' IN REVERSE(REPLACE(n.uri, '\', '/'))) + 2)
                            ELSE REPLACE(n.uri, '\', '/')
                        END,
                        CASE
                            WHEN POSITION('/' IN REPLACE(n.uri, '\', '/')) > 0
                            THEN SUBSTRING(REPLACE(n.uri, '\', '/') FROM 1 FOR LENGTH(REPLACE(n.uri, '\', '/')) - POSITION('/' IN REVERSE(REPLACE(n.uri, '\', '/'))))
                            ELSE NULL
                        END
                    FROM node n
                    WHERE n.kind = 'document'
                      AND n.uri IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM document_search ds WHERE ds.doc_id = n.id)
                    ON CONFLICT (doc_id) DO NOTHING;
                    """);
            }
            else
            {
                // Full refresh - clear and rebuild
                // First check for duplicate URIs - this indicates a data integrity issue
                using var dupCheck = conn.CreateCommand();
                dupCheck.Transaction = tx;
                dupCheck.CommandText = """
                    SELECT uri, COUNT(*) as cnt
                    FROM node
                    WHERE kind = 'document' AND uri IS NOT NULL
                    GROUP BY uri
                    HAVING COUNT(*) > 1
                    LIMIT 10;
                    """;
                using var dupReader = dupCheck.ExecuteReader();
                var duplicates = new List<string>();
                while (dupReader.Read())
                {
                    var uri = dupReader.GetString(0);
                    var count = dupReader.GetInt64(1);
                    duplicates.Add($"{uri} ({count}x)");
                }
                dupReader.Close();

                if (duplicates.Count > 0)
                {
                    Console.Error.WriteLine($"[DuckDB] WARNING: Found {duplicates.Count} URIs with duplicate document nodes: {string.Join(", ", duplicates)}");
                }

                // Use ROW_NUMBER to deduplicate by URI in case multiple document nodes exist for same URI
                conn.Execute(tx, "DELETE FROM document_search;");
                conn.Execute(tx, """
                    INSERT INTO document_search (doc_id, uri, search_key, basename, dirname)
                    WITH ranked AS (
                        SELECT
                            n.id,
                            n.uri,
                            ROW_NUMBER() OVER (PARTITION BY n.uri ORDER BY n.updated_at DESC, n.id) AS rn
                        FROM node n
                        WHERE n.kind = 'document' AND n.uri IS NOT NULL
                    )
                    SELECT
                        r.id,
                        r.uri,
                        LOWER(REPLACE(r.uri, '\', '/')),
                        CASE
                            WHEN POSITION('/' IN REPLACE(r.uri, '\', '/')) > 0
                            THEN SUBSTRING(REPLACE(r.uri, '\', '/') FROM LENGTH(REPLACE(r.uri, '\', '/')) - POSITION('/' IN REVERSE(REPLACE(r.uri, '\', '/'))) + 2)
                            ELSE REPLACE(r.uri, '\', '/')
                        END,
                        CASE
                            WHEN POSITION('/' IN REPLACE(r.uri, '\', '/')) > 0
                            THEN SUBSTRING(REPLACE(r.uri, '\', '/') FROM 1 FOR LENGTH(REPLACE(r.uri, '\', '/')) - POSITION('/' IN REVERSE(REPLACE(r.uri, '\', '/'))))
                            ELSE NULL
                        END
                    FROM ranked r
                    WHERE r.rn = 1;
                    """);
            }
        });
    }

    /// <summary>
    /// Execute a query and return results as dictionary rows.
    /// Used for dynamic SQL execution (MCP queries, CLI).
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(this DuckDbDataStore store, string sql)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sql);

        return store.Read(sql, reader =>
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

        var lc = uri.Container.AbsoluteUri.ToLowerInvariant();
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
            $"SELECT id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure FROM artifact WHERE id = '{id}'",
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

        return store.WriteTransaction((conn, tx) => UpsertDocumentByUri(conn, tx, uri, document));
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
            var containerLc = node.Uri?.Container.AbsoluteUri.ToLowerInvariant();
            cmd.AddParameters(node.Id, node.Kind, node.Uri?.AbsoluteUri, containerLc,
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
        var lc = uri.Container.AbsoluteUri.ToLowerInvariant();
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
        check.CommandText = "SELECT id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure FROM artifact WHERE digest = ?;";
        check.AddParameters(artifact.Digest);
        using var reader = check.ExecuteReader();
        if (reader.Read())
            return reader.MapToArtifact();

        reader.Close();

        // Insert new
        using var ins = conn.CreateCommand();
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

    private static Node UpsertDocumentByUri(DuckDBConnection conn, DuckDBTransaction tx, RepoUri uri, Node document)
    {
        var lc = uri.Container.AbsoluteUri.ToLowerInvariant();
        var uriStr = uri.Container.AbsoluteUri;

        // Check if exists
        using var check = conn.CreateCommand();
        check.Transaction = tx;
        check.CommandText = "SELECT id FROM node WHERE container_uri_lowercase = ?;";
        check.AddParameters(lc);
        using var reader = check.ExecuteReader();

        if (reader.Read())
        {
            var id = reader.GetGuid(0);
            reader.Close();

            // Update existing
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE node
                SET kind=?, uri=?, container_uri_lowercase=?, artifact_id=?, span_id=?, properties=?, headline=?, structure=?, updated_at=?
                WHERE id=?;
                """;
            upd.AddParameters(document.Kind, uriStr, lc, document.ArtifactId, document.SpanId,
                document.Props.ToJsonString(), document.Headline, document.Structure,
                document.UpdatedAt.UtcDateTime, id);
            upd.ExecuteNonQuery();

            return document with { Id = id };
        }

        reader.Close();

        // Insert new
        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO node (id, kind, uri, container_uri_lowercase, artifact_id, span_id, properties, headline, structure, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """;
        ins.AddParameters(document.Id, document.Kind, uriStr, lc, document.ArtifactId, document.SpanId,
            document.Props.ToJsonString(), document.Headline, document.Structure,
            document.CreatedAt.UtcDateTime, document.UpdatedAt.UtcDateTime);
        ins.ExecuteNonQuery();

        return document;
    }

    private static void ReplaceDocumentContent(DuckDBConnection conn, DuckDBTransaction tx, Guid documentId, IReadOnlyList<Node> children, IReadOnlyList<Span> spans, IReadOnlyList<Edge> edges)
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

        // ALWAYS delete spans and edges scoped to this document (even if no children)
        // This ensures stale content is removed on reindex
        conn.Execute(tx, "DELETE FROM span WHERE document_id = ?;", documentId);
        conn.Execute(tx, "DELETE FROM edge WHERE scope_document_id = ?;", documentId);

        // Delete old child nodes
        foreach (var id in childNodesToDelete)
            conn.Execute(tx, "DELETE FROM node WHERE id = ?;", id);

        // Bulk insert spans (much faster than individual inserts)
        if (spans.Count > 0)
            BulkInsertSpans(conn, tx, spans);

        // Bulk insert child nodes
        if (children.Count > 0)
            BulkInsertNodes(conn, tx, children);

        // Bulk insert edges
        if (edges.Count > 0)
            BulkInsertEdges(conn, tx, edges);
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
        const int batchSize = 50; // Nodes have more columns, smaller batches
        for (var offset = 0; offset < nodes.Count; offset += batchSize)
        {
            var batch = nodes.Skip(offset).Take(batchSize).ToList();
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
                var uriStr = node.Uri?.AbsoluteUri;
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

    private static void BulkInsertEdges(DuckDBConnection conn, DuckDBTransaction tx, IReadOnlyList<Edge> edges)
    {
        // Deduplicate composition edges - each child can only have one parent
        // Keep the first edge for each composition_child_id (DstId when IsComposition=true)
        var seenCompositionChildren = new HashSet<Guid>();
        var deduplicatedEdges = new List<Edge>(edges.Count);
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
            }
            deduplicatedEdges.Add(edge);
        }

        if (duplicateCount > 0)
        {
            Console.Error.WriteLine($"[DuckDB] WARNING: Skipped {duplicateCount} duplicate composition edges (same child with multiple parents)");
        }

        const int batchSize = 50; // Edges have many columns
        for (var offset = 0; offset < deduplicatedEdges.Count; offset += batchSize)
        {
            var batch = deduplicatedEdges.Skip(offset).Take(batchSize).ToList();
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
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.Id });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.SrcId });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.DstId });
                cmd.Parameters.Add(new DuckDBParameter { Value = edge.DstUri?.ToString() ?? (object)DBNull.Value });
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

    private static void UpsertDocumentSearch(DuckDBConnection conn, DuckDBTransaction tx, Guid docId, RepoUri uri)
    {
        var uriStr = uri.Container.AbsoluteUri;
        var normalized = uriStr.Replace('\\', '/');
        var searchKey = normalized.ToLowerInvariant();

        // Extract basename (last path component)
        var lastSlash = normalized.LastIndexOf('/');
        var basename = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;

        // Extract dirname (everything before last slash)
        var dirname = lastSlash > 0 ? normalized[..lastSlash] : null;

        using var cmd = conn.CreateCommand();
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
        cmd.AddParameters(docId, uriStr, searchKey, basename, dirname);
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

        // Delete in order: document_search, document_embedding, annotations, edges, spans, nodes
        foreach (var id in toDelete)
            conn.Execute(tx, "DELETE FROM document_search WHERE doc_id = ?;", id);

        foreach (var id in toDelete)
            conn.Execute(tx, "DELETE FROM document_embedding WHERE doc_id = ?;", id);

        foreach (var id in toDelete)
            conn.Execute(tx, "DELETE FROM annotation WHERE scope_document_id = ?;", id);

        // Delete edges scoped to this document, or composition edges where this node is the source
        // Note: We intentionally don't delete edges where destination_node_id matches because those
        // are reference edges from OTHER documents pointing TO this one - they become dangling references
        foreach (var id in toDelete)
            conn.Execute(tx, "DELETE FROM edge WHERE scope_document_id = ? OR source_node_id = ?;", id, id);

        foreach (var id in toDelete)
            conn.Execute(tx, "DELETE FROM span WHERE document_id = ?;", id);

        foreach (var id in toDelete)
            conn.Execute(tx, "DELETE FROM node WHERE id = ?;", id);
    }

    #endregion
}
