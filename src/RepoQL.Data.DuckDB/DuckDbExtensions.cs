using System.Data;
using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Extension methods for DuckDB operations.
/// </summary>
internal static class DuckDbExtensions
{
    #region Command Extensions

    /// <summary>
    /// Add positional parameters to a DuckDB command.
    /// </summary>
    public static void AddParameters(this DuckDBCommand cmd, params object?[] values)
    {
        foreach (var value in values)
            cmd.Parameters.Add(new DuckDBParameter(value));
    }

    extension(DuckDBConnection conn)
    {
        /// <summary>
        /// Execute a SQL statement with no return value.
        /// </summary>
        public void Execute(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Execute a SQL statement with parameters in a transaction.
        /// </summary>
        public void Execute(DuckDBTransaction tx, string sql, params object?[] values)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.AddParameters(values);
            cmd.ExecuteNonQuery();
        }
    }

    #endregion

    #region Data Record Mapping

    /// <summary>
    /// Map a data record to a Node.
    /// Column order: id, kind, uri, artifact_id, span_id, properties, headline, structure, created_at, updated_at
    /// </summary>
    public static Node MapToNode(this IDataRecord r)
    {
        return new Node
        {
            Id = r.GetGuid(0),
            Kind = r.GetString(1),
            Uri = r.IsDBNull(2) ? null : RepoUri.Parse(r.GetString(2)),
            ArtifactId = r.IsDBNull(3) ? null : r.GetGuid(3),
            SpanId = r.IsDBNull(4) ? null : r.GetGuid(4),
            Props = r.IsDBNull(5) ? null : JsonNode.Parse(r.GetString(5))?.AsObject(),
            Headline = r.IsDBNull(6) ? null : r.GetString(6),
            Structure = r.IsDBNull(7) ? null : r.GetString(7),
            CreatedAt = r.IsDBNull(8) ? default : new DateTimeOffset(r.GetDateTime(8), TimeSpan.Zero),
            UpdatedAt = r.IsDBNull(9) ? default : new DateTimeOffset(r.GetDateTime(9), TimeSpan.Zero)
        };
    }

    /// <summary>
    /// Map a data record to an Artifact.
    /// Column order: id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure, token_count
    /// </summary>
    public static Artifact MapToArtifact(this IDataRecord r)
    {
        return new Artifact
        {
            Id = r.GetGuid(0),
            Digest = r.GetString(1),
            Size = r.GetInt64(2),
            MediaType = r.IsDBNull(3) ? null : SemanticMediaType.Parse(r.GetString(3)),
            Text = r.IsDBNull(4) ? null : r.GetString(4),
            StoreUri = r.IsDBNull(5) ? null : RepoUri.Parse(r.GetString(5)),
            Headline = r.IsDBNull(6) ? null : r.GetString(6),
            Summary = r.IsDBNull(7) ? null : r.GetString(7),
            Structure = r.IsDBNull(8) ? null : r.GetString(8),
            TokenCount = r.IsDBNull(9) ? null : (int?)r.GetInt64(9)
        };
    }

    /// <summary>
    /// Map a data record to an Annotation.
    /// Column order: id, semantic_key, kind, severity, source, rule_id, message, data,
    ///               scope_document_id, target_node_id, target_edge_id, target_span_id, target_uri,
    ///               created_at, expires_at
    /// </summary>
    public static Annotation MapToAnnotation(this IDataRecord r)
    {
        return new Annotation
        {
            Id = r.GetGuid(0),
            SemanticKey = r.IsDBNull(1) ? null : r.GetString(1),
            Kind = r.IsDBNull(2) ? "info" : r.GetString(2),
            Severity = r.IsDBNull(3) ? "info" : r.GetString(3),
            Source = r.IsDBNull(4) ? "unknown" : r.GetString(4),
            RuleId = r.IsDBNull(5) ? null : r.GetString(5),
            Message = r.IsDBNull(6) ? "" : r.GetString(6),
            Data = r.IsDBNull(7) ? new JsonObject() : (JsonNode.Parse(r.GetString(7))?.AsObject() ?? new JsonObject()),
            ScopeDocumentId = r.IsDBNull(8) ? Guid.Empty : r.GetGuid(8),
            TargetNodeId = r.IsDBNull(9) ? null : r.GetGuid(9),
            TargetEdgeId = r.IsDBNull(10) ? null : r.GetGuid(10),
            TargetSpanId = r.IsDBNull(11) ? null : r.GetGuid(11),
            TargetUri = r.IsDBNull(12) ? null : RepoUri.Parse(r.GetString(12)),
            CreatedAt = r.IsDBNull(13) ? default : new DateTimeOffset(r.GetDateTime(13), TimeSpan.Zero),
            ExpiresAt = r.IsDBNull(14) ? null : new DateTimeOffset(r.GetDateTime(14), TimeSpan.Zero)
        };
    }

    #endregion

    #region Serialization

    /// <summary>
    /// Serialize a JsonObject to a JSON string for DuckDB storage.
    /// Returns "{}" for null or empty objects (schema requires NOT NULL).
    /// </summary>
    public static string ToJsonString(this JsonObject? props)
    {
        if (props is null || props.Count == 0)
            return "{}";
        using var ms = new MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(ms);
        props.WriteTo(writer);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Convert a float array to DuckDB's FLOAT[] format.
    /// </summary>
    public static string ToDuckDbArray(this float[] vec)
    {
        return "[" + string.Join(",", vec.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
    }

    #endregion

    #region Metrics Helpers

    /// <summary>
    /// Bucket row counts to limit cardinality: 0, 1, 2-10, 11-100, 101-1000, 1000+
    /// </summary>
    public static string ToRowCountBucket(this int count) => count switch
    {
        0 => "0",
        1 => "1",
        <= 10 => "2-10",
        <= 100 => "11-100",
        <= 1000 => "101-1000",
        _ => "1000+"
    };

    #endregion
}
