using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Contracts.Snapshots;

namespace RepoQL.Data.DuckDB.Snapshots;

/// <summary>
/// Loads <see cref="ISnapshotSource"/> data into a <see cref="DuckDbDataStore"/>.
/// Version-gated: skips sources whose version matches the stored metadata.
/// All mutations for a single source happen in one transaction for crash safety.
/// </summary>
public static class SnapshotLoader
{
    /// <summary>
    /// Load all snapshot sources into the store. Call AFTER <c>EnsureSchema()</c> completes.
    /// </summary>
    public static void LoadAll(DuckDbDataStore store, IEnumerable<ISnapshotSource> sources, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sources);

        foreach (var source in sources)
        {
            try
            {
                LoadSource(store, source, logger);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[Snapshot] Failed to load snapshot source '{SourceId}'", source.Id);
            }
        }
    }

    /// <summary>
    /// Load a single snapshot source. Skips if the stored version matches.
    /// </summary>
    public static void LoadSource(DuckDbDataStore store, ISnapshotSource source, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(source);

        var metadataKey = $"snapshot:{source.Id}";

        // Check version outside the write transaction (read-only, no lock needed)
        var storedVersion = store.ReadMetadataValue(metadataKey);
        if (storedVersion == source.Version)
        {
            logger?.LogDebug("[Snapshot] Source '{SourceId}' version '{Version}' is current, skipping",
                source.Id, source.Version);
            return;
        }

        logger?.LogInformation("[Snapshot] Loading source '{SourceId}' (version {OldVersion} -> {NewVersion})",
            source.Id, storedVersion ?? "(none)", source.Version);

        var documents = source.GetDocuments();

        // Everything in one transaction: delete stale → insert new → update metadata
        store.WriteTransaction((conn, tx) =>
        {
            // 1. Delete existing data for this URI prefix
            var deleteCount = DeleteByUriPrefix(conn, tx, source.UriPrefix);
            if (deleteCount > 0)
            {
                logger?.LogDebug("[Snapshot] Deleted {Count} stale documents for prefix '{Prefix}'",
                    deleteCount, source.UriPrefix);
            }

            // 2. Insert each document
            var loaded = 0;
            foreach (var doc in documents)
            {
                var parsed = ParsedArtifact.FromRecords(doc.Records);
                IndexSingleArtifact(conn, tx, doc.Uri, parsed, logger);

                // Write annotations separately (IndexArtifactBatch pattern doesn't handle these)
                if (doc.Records.Annotations.Length > 0)
                {
                    WriteAnnotations(conn, tx, doc.Uri, doc.Records.Annotations,
                        doc.Records.AnnotationSources, logger);
                }

                loaded++;
            }

            // 3. Update metadata version (only if everything succeeded)
            UpsertMetadata(conn, tx, metadataKey, source.Version);

            logger?.LogInformation("[Snapshot] Loaded {Count} documents for source '{SourceId}' v{Version}",
                loaded, source.Id, source.Version);
        });
    }

    // ---- Internal helpers ----

    private static int DeleteByUriPrefix(DuckDBConnection conn, DuckDBTransaction tx, string uriPrefix)
    {
        // Find all document nodes matching the prefix
        using var selectCmd = conn.CreateCommand();
        selectCmd.Transaction = tx;
        selectCmd.CommandText = @"
            SELECT id, artifact_id
            FROM node
            WHERE uri LIKE $1 || '%' AND kind = 'document'";
        selectCmd.Parameters.Add(new DuckDBParameter { Value = uriPrefix });

        var nodesToDelete = new List<(Guid NodeId, Guid? ArtifactId)>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var nodeId = reader.GetGuid(0);
                var artifactId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
                nodesToDelete.Add((nodeId, artifactId));
            }
        }

        if (nodesToDelete.Count == 0)
            return 0;

        foreach (var (nodeId, artifactId) in nodesToDelete)
        {
            DeleteDocumentSubtree(conn, tx, nodeId, artifactId);
        }

        return nodesToDelete.Count;
    }

    private static void DeleteDocumentSubtree(DuckDBConnection conn, DuckDBTransaction tx, Guid nodeId, Guid? artifactId)
    {
        // Delete embeddings
        ExecuteNonQuery(conn, tx, "DELETE FROM document_embedding WHERE doc_id = $1", nodeId);

        // Delete annotations
        ExecuteNonQuery(conn, tx, "DELETE FROM annotation WHERE scope_document_id = $1", nodeId);

        // Delete spans
        ExecuteNonQuery(conn, tx, "DELETE FROM span WHERE document_id = $1", nodeId);

        // Delete edges involving nodes from this document
        if (artifactId.HasValue)
        {
            using var deleteEdgeCmd = conn.CreateCommand();
            deleteEdgeCmd.Transaction = tx;
            deleteEdgeCmd.CommandText = @"
                DELETE FROM edge
                WHERE source_node_id IN (SELECT id FROM node WHERE artifact_id = $1)
                   OR destination_node_id IN (SELECT id FROM node WHERE artifact_id = $1)";
            deleteEdgeCmd.Parameters.Add(new DuckDBParameter { Value = artifactId.Value });
            deleteEdgeCmd.ExecuteNonQuery();

            // Delete all nodes referencing this artifact
            ExecuteNonQuery(conn, tx, "DELETE FROM node WHERE artifact_id = $1", artifactId.Value);
        }

        // Ensure the matched document root is removed even if it has no artifact ID.
        ExecuteNonQuery(conn, tx, "DELETE FROM node WHERE id = $1", nodeId);
    }

    private static void IndexSingleArtifact(DuckDBConnection conn, DuckDBTransaction tx,
        RepoUri uri, ParsedArtifact artifact, ILogger? logger)
    {
        // 1. Insert artifact
        InsertArtifact(conn, tx, artifact.Artifact);

        // 2. Insert document node with artifact ID
        var docNode = artifact.DocumentNode with { ArtifactId = artifact.Artifact.Id };
        InsertNode(conn, tx, docNode);

        // 3. Insert child nodes
        foreach (var child in artifact.Children)
        {
            InsertNode(conn, tx, child);
        }

        // 4. Insert spans (remap document ID to the document node)
        foreach (var span in artifact.Spans)
        {
            var remapped = span with { DocumentId = docNode.Id };
            InsertSpan(conn, tx, remapped);
        }

        // 5. Insert edges (remap scope document ID)
        foreach (var edge in artifact.Edges)
        {
            var remapped = edge with { ScopeDocumentId = docNode.Id };
            InsertEdge(conn, tx, remapped);
        }
    }

    private static void InsertArtifact(DuckDBConnection conn, DuckDBTransaction tx, Artifact a)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO artifact (id, digest, byte_size, media_type, text_content, storage_uri,
                                  headline, summary, structure, token_count)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (id) DO UPDATE SET
                digest = $2, byte_size = $3, media_type = $4, text_content = $5, storage_uri = $6,
                headline = $7, summary = $8, structure = $9, token_count = $10";
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Id });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Digest });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Size });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.MediaType?.ToString() });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Text });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.StoreUri?.AbsoluteUri });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Headline });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Summary });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Structure });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)a.TokenCount ?? DBNull.Value });
        cmd.ExecuteNonQuery();
    }

    private static void InsertNode(DuckDBConnection conn, DuckDBTransaction tx, Node n)
    {
        // Only populate container_uri_lowercase for document nodes.
        // The column has a UNIQUE index — child nodes with fragment URIs (e.g., #symbol=Foo)
        // share the same container as their document and would violate the constraint.
        var containerLc = string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase) && n.Uri != null
            ? RepoUri.NormalizeContainerKey(n.Uri)
            : null;

        // Plain INSERT — no ON CONFLICT. DeleteByUriPrefix already cleaned up existing
        // data. ON CONFLICT (id) DO UPDATE SET container_uri_lowercase causes ART index
        // staleness on the container_uri_lowercase column, leading to phantom constraint
        // violations during live indexing.
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO node (id, kind, uri, container_uri_lowercase, artifact_id, span_id, properties, headline, structure, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";
        cmd.Parameters.Add(new DuckDBParameter { Value = n.Id });
        cmd.Parameters.Add(new DuckDBParameter { Value = n.Kind });
        cmd.Parameters.Add(new DuckDBParameter { Value = n.Uri?.AbsoluteUri });
        cmd.Parameters.Add(new DuckDBParameter { Value = containerLc });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)n.ArtifactId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)n.SpanId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = n.Props.Count > 0 ? n.Props.ToJsonString() : "{}" });
        cmd.Parameters.Add(new DuckDBParameter { Value = n.Headline });
        cmd.Parameters.Add(new DuckDBParameter { Value = n.Structure });
        cmd.Parameters.Add(new DuckDBParameter { Value = n.CreatedAt.UtcDateTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = n.UpdatedAt.UtcDateTime });
        cmd.ExecuteNonQuery();
    }

    private static void InsertSpan(DuckDBConnection conn, DuckDBTransaction tx, Span s)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO span (id, document_id, start_byte, end_byte, start_line, start_column, end_line, end_column)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)";
        cmd.Parameters.Add(new DuckDBParameter { Value = s.Id });
        cmd.Parameters.Add(new DuckDBParameter { Value = s.DocumentId });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)s.StartByte ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)s.EndByte ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)s.StartLine ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)s.StartColumn ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)s.EndLine ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)s.EndColumn ?? DBNull.Value });
        cmd.ExecuteNonQuery();
    }

    private static void InsertEdge(DuckDBConnection conn, DuckDBTransaction tx, Edge e)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO edge (id, source_node_id, destination_node_id, destination_uri, type,
                              is_composition, ordinal, scope_document_id, semantic_key,
                              source_span_id, destination_span_id, composition_child_id,
                              properties, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)";
        cmd.Parameters.Add(new DuckDBParameter { Value = e.Id });
        cmd.Parameters.Add(new DuckDBParameter { Value = e.SrcId });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)e.DstId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = e.DstUri?.AbsoluteUri });
        cmd.Parameters.Add(new DuckDBParameter { Value = e.Type });
        cmd.Parameters.Add(new DuckDBParameter { Value = e.IsComposition });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)e.Ordinal ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)e.ScopeDocumentId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = e.EdgeKey });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)e.SrcSpanId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)e.DstSpanId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)(e.IsComposition ? e.DstId : null) ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = e.Props.Count > 0 ? e.Props.ToJsonString() : "{}" });
        cmd.Parameters.Add(new DuckDBParameter { Value = e.CreatedAt.UtcDateTime });
        cmd.ExecuteNonQuery();
    }

    private static void WriteAnnotations(DuckDBConnection conn, DuckDBTransaction tx,
        RepoUri uri, Annotation[] annotations, string[] annotationSources, ILogger? logger)
    {
        // Find the document node for this URI
        using var findCmd = conn.CreateCommand();
        findCmd.Transaction = tx;
        findCmd.CommandText = "SELECT id FROM node WHERE uri = $1 AND kind = 'document'";
        findCmd.Parameters.Add(new DuckDBParameter { Value = uri.AbsoluteUri });
        var docId = findCmd.ExecuteScalar();
        if (docId is null or DBNull) return;
        var documentId = (Guid)docId;

        // Clear existing annotations from these sources
        if (annotationSources.Length > 0)
        {
            foreach (var source in annotationSources)
            {
                using var deleteCmd = conn.CreateCommand();
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = "DELETE FROM annotation WHERE scope_document_id = $1 AND source = $2";
                deleteCmd.Parameters.Add(new DuckDBParameter { Value = documentId });
                deleteCmd.Parameters.Add(new DuckDBParameter { Value = source });
                deleteCmd.ExecuteNonQuery();
            }
        }

        // Insert new annotations
        foreach (var a in annotations)
        {
            var remapped = a with { ScopeDocumentId = documentId };
            InsertAnnotation(conn, tx, remapped);
        }
    }

    private static void InsertAnnotation(DuckDBConnection conn, DuckDBTransaction tx, Annotation a)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO annotation (id, semantic_key, kind, severity, source, rule_id, message,
                                    data, scope_document_id, target_node_id, target_edge_id,
                                    target_span_id, target_uri, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)";
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Id });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.SemanticKey });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Kind });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Severity });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Source });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.RuleId });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Message });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.Data.Count > 0 ? a.Data.ToJsonString() : "{}" });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.ScopeDocumentId });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)a.TargetNodeId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)a.TargetEdgeId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)a.TargetSpanId ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.TargetUri?.AbsoluteUri });
        cmd.Parameters.Add(new DuckDBParameter { Value = a.CreatedAt.UtcDateTime });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)a.ExpiresAt?.UtcDateTime ?? DBNull.Value });
        cmd.ExecuteNonQuery();
    }

    private static void UpsertMetadata(DuckDBConnection conn, DuckDBTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO metadata (key, value, updated_at) VALUES ($1, $2, now())
            ON CONFLICT (key) DO UPDATE SET value = $2, updated_at = now()";
        cmd.Parameters.Add(new DuckDBParameter { Value = key });
        cmd.Parameters.Add(new DuckDBParameter { Value = value });
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(DuckDBConnection conn, DuckDBTransaction tx, string sql, object param)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.Add(new DuckDBParameter { Value = param });
        cmd.ExecuteNonQuery();
    }
}
