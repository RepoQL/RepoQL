using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using System.Diagnostics.Metrics;

namespace RepoQL.Data.DuckDB;

/// <summary>
///     DuckDB-backed implementation of <see cref="IGraphStore" />. Provides a self-describing schema,
///     enables helpful extensions, registers UDFs, and installs the “anything by URI” macro.
/// </summary>
    public sealed class DuckDbGraphStore : IGraphStore
    {
        private readonly DuckDBConnection _connection;
        private readonly bool _ownsConnection;
        private readonly bool _udfsRegistered;
        private readonly ILogger<DuckDbGraphStore> _logger;
        // Embedding metrics instruments (use the shared indexing meter name)
        private static readonly Meter Meter = new("RepoQL.Indexing");
        private static readonly Counter<long> EmbedRequests = Meter.CreateCounter<long>(
            "repoql.embed.requests", unit: "calls", description: "Embedding requests (query-time or refresh)");
        private static readonly Counter<long> EmbedErrors = Meter.CreateCounter<long>(
            "repoql.embed.errors", unit: "errors", description: "Embedding failures");
        private static readonly Histogram<double> EmbedDuration = Meter.CreateHistogram<double>(
            "repoql.embed.duration", unit: "ms", description: "Embedding duration");

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
    public void RefreshDocumentEmbeddings(RepoQL.Contracts.Embeddings.IEmbeddingProvider provider, CancellationToken ct = default)
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
        if (int.TryParse(Environment.GetEnvironmentVariable("REPOQL_EMBED_BATCH_SIZE"), out var bs) && bs > 0) batchSize = bs;
        for (int ofs = 0; ofs < rows.Count; ofs += batchSize)
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
                    EmbedErrors.Add(1, new System.Diagnostics.TagList { { "source", "refresh" }, { "model", provider.Model }, { "dim", provider.Dimension } });
                    EmbedRequests.Add(1, new System.Diagnostics.TagList { { "source", "refresh" }, { "model", provider.Model }, { "dim", provider.Dimension }, { "status", "error" } });
                    EmbedDuration.Record(t0.Elapsed.TotalMilliseconds, new System.Diagnostics.TagList { { "source", "refresh" }, { "model", provider.Model }, { "dim", provider.Dimension }, { "status", "error" } });
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
                EmbedRequests.Add(1, new System.Diagnostics.TagList { { "source", "refresh" }, { "model", provider.Model }, { "dim", provider.Dimension }, { "status", "ok" } });
                EmbedDuration.Record(t0.Elapsed.TotalMilliseconds, new System.Diagnostics.TagList { { "source", "refresh" }, { "model", provider.Model }, { "dim", provider.Dimension }, { "status", "ok" } });
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
            for (int i = 0; i < vec.Length; i++) w.WriteNumberValue(vec[i]);
            w.WriteEndArray();
            w.Flush();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    ///     Creates a DuckDbGraphStore with an existing connection.
    /// </summary>
    /// <param name="connection">An open DuckDB connection.</param>
    /// <param name="enableExtensions">Install/Load recommended extensions when true.</param>
    /// <param name="registerUdfs">Register repository URI and media type scalar UDFs when true.</param>
    /// <param name="logger">Optional logger for macro/view creation warnings.</param>
    public DuckDbGraphStore(DuckDBConnection connection, bool enableExtensions = true, bool registerUdfs = true, ILogger<DuckDbGraphStore>? logger = null, RepoQL.Contracts.Embeddings.IEmbeddingProvider? embeddingProvider = null)
    {
        this._connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this._ownsConnection = false;
        this._udfsRegistered = registerUdfs;
        this._logger = logger ?? NullLogger<DuckDbGraphStore>.Instance;
        this._databaseLabel = TryExtractDbNameSafe(_connection.ConnectionString);

        if (enableExtensions) EnableRecommendedExtensions();
        if (registerUdfs) RepositoryUserDefinedFunctions.RegisterAll(connection, embeddingProvider);
    }

    /// <summary>
    ///     Opens a DuckDB database from a file path. Optionally enables extensions and registers UDFs.
    /// </summary>
    /// <param name="filePath">Path to a DuckDB file.</param>
    /// <param name="enableExtensions">Install/Load recommended extensions when true.</param>
    /// <param name="registerUdfs">Register repository URI and media type scalar UDFs when true.</param>
    /// <param name="logger">Optional logger for macro/view creation warnings.</param>
    public DuckDbGraphStore(string filePath, bool enableExtensions = true, bool registerUdfs = true, ILogger<DuckDbGraphStore>? logger = null, RepoQL.Contracts.Embeddings.IEmbeddingProvider? embeddingProvider = null)
    {
        _connection = new DuckDBConnection($"Data Source={filePath}");
        _connection.Open();
        _ownsConnection = true;
        _udfsRegistered = registerUdfs;
        _logger = logger ?? NullLogger<DuckDbGraphStore>.Instance;
        _databaseLabel = TryExtractDbNameSafe(_connection.ConnectionString);

        if (enableExtensions) EnableRecommendedExtensions();
        if (registerUdfs) RepositoryUserDefinedFunctions.RegisterAll(_connection, embeddingProvider);
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
        using var tx = _connection.BeginTransaction();

        Execute(@"
CREATE TABLE IF NOT EXISTS artifact (
  id           UUID PRIMARY KEY,
  digest       VARCHAR NOT NULL UNIQUE,
  byte_size    BIGINT NOT NULL,
  media_type   VARCHAR,
  text_content VARCHAR,
  storage_uri  VARCHAR,
  headline     VARCHAR,
  summary      VARCHAR,
  structure    VARCHAR
);");

        Execute(@"
CREATE TABLE IF NOT EXISTS node (
  id                          UUID PRIMARY KEY,
  kind                        VARCHAR NOT NULL,
  uri                         VARCHAR,
  container_uri_lowercase     VARCHAR,
  artifact_id                 UUID,
  span_id                     UUID,
  properties                  JSON NOT NULL,
  created_at                  TIMESTAMP NOT NULL,
  updated_at                  TIMESTAMP NOT NULL,
  CHECK (kind <> 'document' OR uri IS NOT NULL),
  FOREIGN KEY (artifact_id) REFERENCES artifact(id)
);");

        Execute(@"
CREATE TABLE IF NOT EXISTS span (
  id            UUID PRIMARY KEY,
  document_id   UUID NOT NULL,
  start_byte    BIGINT,
  end_byte      BIGINT,
  start_line    INTEGER,
  start_column  INTEGER,
  end_line      INTEGER,
  end_column    INTEGER
  -- FK constraint removed: See edge table comment
);");

        Execute(@"
CREATE TABLE IF NOT EXISTS edge (
  id                     UUID PRIMARY KEY,
  source_node_id         UUID NOT NULL,
  destination_node_id    UUID NOT NULL,
  type                   VARCHAR NOT NULL,
  is_composition         BOOLEAN NOT NULL,
  ordinal                INTEGER,
  scope_document_id      UUID,
  semantic_key           VARCHAR,
  source_span_id         UUID,
  destination_span_id    UUID,
  composition_child_id   UUID,
  properties             JSON NOT NULL,
  created_at             TIMESTAMP NOT NULL
  -- FK constraints removed: DuckDB checks constraints immediately, even in transactions
  -- This prevents deletion of composition trees. Referential integrity is maintained
  -- at the application level instead.
);");

        Execute(
            @"CREATE UNIQUE INDEX IF NOT EXISTS node_container_uri_lowercase_unique ON node(container_uri_lowercase);");
        Execute(@"CREATE INDEX IF NOT EXISTS node_kind_idx ON node(kind);");
        Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS edge_semantic_key_unique ON edge(semantic_key);");
        Execute(@"CREATE UNIQUE INDEX IF NOT EXISTS edge_composition_single_parent ON edge(composition_child_id);");
        Execute(@"CREATE INDEX IF NOT EXISTS edge_source_idx      ON edge(source_node_id);");
        Execute(@"CREATE INDEX IF NOT EXISTS edge_destination_idx ON edge(destination_node_id);");
        Execute(@"CREATE INDEX IF NOT EXISTS edge_type_idx         ON edge(type);");
        Execute(@"CREATE INDEX IF NOT EXISTS edge_scope_idx        ON edge(scope_document_id);");

        Execute(@"
COMMENT ON TABLE artifact IS 'Content-addressed artifact bytes and optional decoded text.';
COMMENT ON COLUMN artifact.id IS 'Artifact identifier (GUID).';
COMMENT ON COLUMN artifact.digest IS 'Content digest (e.g., sha256:...).';
COMMENT ON COLUMN artifact.byte_size IS 'Uncompressed size in bytes.';
COMMENT ON COLUMN artifact.media_type IS 'Semantic media type string with parameters.';
COMMENT ON COLUMN artifact.text_content IS 'Optional decoded text for search and span mapping.';
COMMENT ON COLUMN artifact.storage_uri IS 'External storage location for raw bytes (file/object store).';
COMMENT ON COLUMN artifact.headline IS 'X-ray Level 0 (headline): essential identity (single line), always present for documents.';
COMMENT ON COLUMN artifact.summary IS 'X-ray Level 1 (summary): key information (~5 lines, max 10) for understanding without reading full content.';
COMMENT ON COLUMN artifact.structure IS 'X-ray Level 2 (structure): detailed outline (~15 lines, max 25) for navigation and exploration.';

COMMENT ON TABLE node IS 'Property-graph vertex: documents, sections, symbols, etc.';
COMMENT ON COLUMN node.id IS 'Node identifier (GUID).';
COMMENT ON COLUMN node.kind IS 'Open taxonomy label (e.g., document, md_section, cs_class).';
COMMENT ON COLUMN node.uri IS 'Repository-aware container URI for documents (no fragment).';
COMMENT ON COLUMN node.container_uri_lowercase IS 'Lowercase container URI for uniqueness.';
COMMENT ON COLUMN node.artifact_id IS 'Back-reference to artifact providing bytes.';
COMMENT ON COLUMN node.span_id IS 'Span that locates this node within a document.';
COMMENT ON COLUMN node.properties IS 'Arbitrary attributes as JSON.';
COMMENT ON COLUMN node.created_at IS 'Creation timestamp (UTC).';
COMMENT ON COLUMN node.updated_at IS 'Update timestamp (UTC).';

COMMENT ON TABLE span IS 'Text/byte extent within a single document node.';
COMMENT ON COLUMN span.id IS 'Span identifier (GUID).';
COMMENT ON COLUMN span.document_id IS 'Owning document node id.';
COMMENT ON COLUMN span.start_byte IS '0-based start byte offset (inclusive).';
COMMENT ON COLUMN span.end_byte IS '0-based end byte offset (exclusive).';
COMMENT ON COLUMN span.start_line IS '1-based start line.';
COMMENT ON COLUMN span.start_column IS '1-based start column.';
COMMENT ON COLUMN span.end_line IS '1-based end line.';
COMMENT ON COLUMN span.end_column IS '1-based end column.';

COMMENT ON TABLE edge IS 'Directed relationship between nodes with optional spans and attributes.';
COMMENT ON COLUMN edge.id IS 'Edge identifier (GUID).';
COMMENT ON COLUMN edge.source_node_id IS 'Source node id.';
COMMENT ON COLUMN edge.destination_node_id IS 'Destination node id.';
COMMENT ON COLUMN edge.type IS 'Relation type (e.g., HAS_PART, REFERS_TO, CALLS).';
COMMENT ON COLUMN edge.is_composition IS 'True when expressing containment/ownership.';
COMMENT ON COLUMN edge.ordinal IS 'Stable order among composition siblings.';
COMMENT ON COLUMN edge.scope_document_id IS 'Document that scoped or produced this relation.';
COMMENT ON COLUMN edge.semantic_key IS 'Optional business key for idempotent upserts.';
COMMENT ON COLUMN edge.source_span_id IS 'Span at origin site (e.g., link text or call site).';
COMMENT ON COLUMN edge.destination_span_id IS 'Span that the relation points to.';
COMMENT ON COLUMN edge.composition_child_id IS 'Destination when is_composition=true; enforces single parent.';
COMMENT ON COLUMN edge.properties IS 'Relation attributes as JSON.';
COMMENT ON COLUMN edge.created_at IS 'Creation timestamp (UTC).';
");

        Execute(@"
CREATE OR REPLACE MACRO entities_by_uri(u) AS TABLE (
  WITH base AS (
    SELECT
      repository_uri_container(u)         AS base,
      repository_uri_fragment(u)          AS frag,
      repository_uri_fragment_kind(u)     AS kind,
      repository_uri_line_start(u)        AS l1,
      repository_uri_line_end(u)          AS l2
  ),
  char_rng AS (
    SELECT
      CASE WHEN kind='char' THEN try_cast(split_part(substr(frag, 6), ',', 1) AS BIGINT) END AS c1,
      CASE WHEN kind='char' THEN try_cast(NULLIF(split_part(substr(frag, 6), ',', 2), '') AS BIGINT) END AS c2
    FROM base
  )
  SELECT
    'Document' AS entity, n.id AS id, n.kind AS aux,
    n.uri AS uri, n.uri AS container_uri, NULL AS fragment
  FROM base b
  JOIN node n ON lower(n.uri) = lower(b.base)
  WHERE b.frag IS NULL

  UNION ALL
  SELECT
    'Edge', e.id, e.type,
    repository_uri_join(n.uri, 'edge=' || CAST(e.id AS VARCHAR)),
    n.uri, 'edge=' || CAST(e.id AS VARCHAR)
  FROM base b
  JOIN node n ON lower(n.uri) = lower(b.base)
  JOIN edge e ON e.scope_document_id = n.id
  WHERE b.frag LIKE 'edge=%' AND substr(b.frag, 6) = CAST(e.id AS VARCHAR)

  UNION ALL
  SELECT
    'Span', s.id, NULL,
    repository_uri_join(n.uri, fragment_from_line_range(s.start_line, s.end_line)),
    n.uri, fragment_from_line_range(s.start_line, s.end_line)
  FROM base b
  JOIN node n ON lower(n.uri) = lower(b.base)
  JOIN span s ON s.document_id = n.id
  WHERE b.kind = 'line'
    AND s.start_line <= COALESCE(b.l1, s.start_line)
    AND s.end_line   >= COALESCE(b.l2, s.end_line)

  UNION ALL
  SELECT
    'Span', s.id, NULL,
    repository_uri_join(n.uri, fragment_from_char_range(s.start_byte, s.end_byte)),
    n.uri, fragment_from_char_range(s.start_byte, s.end_byte)
  FROM base b, char_rng r
  JOIN node n ON lower(n.uri) = lower(b.base)
  JOIN span s ON s.document_id = n.id
  WHERE b.kind = 'char'
    AND (r.c1 IS NOT NULL AND s.start_byte <= r.c1)
    AND (r.c2 IS NULL    OR  s.end_byte   >= r.c2)
);");

        // Helper macro: extract a JSON string array at $.tags into a DuckDB LIST<VARCHAR>
        // This avoids requiring the DuckDB JSON extension in read-only/test contexts.
        // Usage: LATERAL UNNEST(json_extract_string_array(n.properties, '$.tags')) AS t(tag)
        Execute(@"
CREATE OR REPLACE MACRO json_extract_string_array(j, path) AS (
  string_split(
    REPLACE(
      REGEXP_REPLACE(
        REGEXP_REPLACE(CAST(j AS VARCHAR), '^.*""tags""\s*:\s*\[\s*', ''),
        '\s*\].*$', ''
      ),
      '""',
      ''
    ),
    ','
  )
);
");

        // Annotation table and indexes
        Execute(@"
CREATE TABLE IF NOT EXISTS annotation (
  id                 UUID PRIMARY KEY,
  semantic_key       TEXT,
  kind               TEXT NOT NULL,
  severity           TEXT NOT NULL,
  source             TEXT NOT NULL,
  rule_id            TEXT,
  message            TEXT NOT NULL,
  data               JSON NOT NULL,
  scope_document_id  UUID NOT NULL,
  target_node_id     UUID,
  target_edge_id     UUID,
  target_span_id     UUID,
  target_uri         TEXT,
  created_at         TIMESTAMP NOT NULL,
  expires_at         TIMESTAMP,
  UNIQUE(semantic_key)
);

CREATE INDEX IF NOT EXISTS annotation_kind_index           ON annotation(kind);
CREATE INDEX IF NOT EXISTS annotation_severity_index       ON annotation(severity);
CREATE INDEX IF NOT EXISTS annotation_scope_document_id_index ON annotation(scope_document_id);
CREATE INDEX IF NOT EXISTS annotation_target_node_id_index ON annotation(target_node_id);
CREATE INDEX IF NOT EXISTS annotation_target_edge_id_index ON annotation(target_edge_id);
CREATE INDEX IF NOT EXISTS annotation_target_span_id_index ON annotation(target_span_id);

COMMENT ON TABLE annotation IS 'Out-of-band facts (lint, outline, metrics, hints)..';
", tx);

        // Annotation helper macros and views
        Execute(@"CREATE OR REPLACE MACRO _severity_rank(s) AS (
  CASE lower(s)
    WHEN 'error'   THEN 4
    WHEN 'warning' THEN 3
    WHEN 'info'    THEN 2
    WHEN 'hint'    THEN 1
    ELSE 0
  END
);", tx);

        // Create the annotations VIEW only if UDFs are registered
        // because it depends on repository_uri_join and fragment_from_line_range functions
        if (_udfsRegistered)
        {
            Execute(@"CREATE OR REPLACE VIEW annotations AS
WITH base AS (
  SELECT a.*, sd.uri AS scope_document_uri
  FROM annotation a
  JOIN node sd ON sd.id = a.scope_document_id
),
span_uri AS (
  SELECT a.id,
         repository_uri_join(b.scope_document_uri,
           fragment_from_line_range(s.start_line, s.end_line)) AS uri_from_span
  FROM base b
  JOIN annotation a ON a.id = b.id
  LEFT JOIN span s  ON s.id = a.target_span_id
),
node_frag AS (
  SELECT a.id,
         -- Simplified fragment for nodes: just use line range if available
         CASE 
           WHEN s.start_line IS NOT NULL AND s.end_line IS NOT NULL 
           THEN fragment_from_line_range(s.start_line, s.end_line)
           ELSE NULL
         END AS frag
  FROM base b
  JOIN annotation a ON a.id = b.id
  LEFT JOIN node n  ON n.id = a.target_node_id
  LEFT JOIN span s  ON s.id = n.span_id
),
edge_uri AS (
  SELECT a.id,
         repository_uri_join(b.scope_document_uri, 'edge=' || CAST(e.id AS TEXT)) AS uri_from_edge
  FROM base b
  JOIN annotation a ON a.id = b.id
  LEFT JOIN edge e  ON e.id = a.target_edge_id
)
SELECT
  a.*,
  COALESCE(
    a.target_uri,
    su.uri_from_span,
    CASE WHEN nf.frag IS NOT NULL THEN repository_uri_join(b.scope_document_uri, nf.frag) END,
    eu.uri_from_edge,
    b.scope_document_uri
  ) AS resolved_target_uri,
  _severity_rank(a.severity) AS severity_rank
FROM annotation a
JOIN base b   ON b.id = a.id
LEFT JOIN span_uri su ON su.id = a.id
LEFT JOIN node_frag nf ON nf.id = a.id
LEFT JOIN edge_uri eu  ON eu.id = a.id;", tx);

            Execute(@"CREATE OR REPLACE MACRO annotations_for(u, kinds, min_severity) AS TABLE (
  WITH doc AS (
    SELECT id AS doc_id FROM node
    WHERE lower(uri) = lower(repository_uri_container(u))
  )
  SELECT *
  FROM annotations a, doc
  WHERE a.scope_document_id = doc.doc_id
    AND (kinds IS NULL OR EXISTS (
          SELECT 1 FROM UNNEST(string_split(kinds, ',')) k(value)
          WHERE lower(trim(k.value)) = lower(a.kind)))
    AND (_severity_rank(a.severity) >= _severity_rank(COALESCE(min_severity,'hint')))
  ORDER BY severity_rank DESC, created_at DESC
);", tx);

        Execute(@"CREATE OR REPLACE MACRO annotations_all(kinds, min_severity) AS TABLE (
  SELECT *
  FROM annotations
  WHERE (kinds IS NULL OR EXISTS (
          SELECT 1 FROM UNNEST(string_split(kinds, ',')) k(value)
          WHERE lower(trim(k.value)) = lower(annotations.kind)))
    AND (_severity_rank(severity) >= _severity_rank(COALESCE(min_severity,'hint')))
  ORDER BY severity_rank DESC, created_at DESC
);", tx);

        // Embeddings table (optional, used when semantic search is enabled)
        Execute(@"CREATE TABLE IF NOT EXISTS document_embedding (
  doc_id    UUID PRIMARY KEY,
  model     VARCHAR NOT NULL,
  dim       INTEGER NOT NULL,
  embedding VARCHAR NOT NULL, -- JSON float array
  updated_at TIMESTAMP NOT NULL
);", tx);
        Execute(@"CREATE INDEX IF NOT EXISTS document_embedding_model_idx ON document_embedding(model);", tx);
        }

        tx.Commit();

        if (_udfsRegistered)
        {
            try { CreateSnippetMacro(); } catch (Exception ex) { _logger.LogWarning(ex, "CreateSnippetMacro failed; continuing without snippet macro"); }
            try { CreateMarkdownViews(); } catch (Exception ex) { _logger.LogWarning(ex, "CreateMarkdownViews failed; continuing without markdown views"); }
            try { CreateXrayDocumentsMacro(); } catch (Exception ex) { _logger.LogWarning(ex, "CreateXrayDocumentsMacro failed; continuing without xray_documents macro"); }
            try { CreateXrayItemsMacro(); } catch (Exception ex) { _logger.LogWarning(ex, "CreateXrayItemsMacro failed; continuing without xray_items macro"); }
            try { CreateXrayLinesMacro(); } catch (Exception ex) { _logger.LogWarning(ex, "CreateXrayLinesMacro failed; continuing without xray_lines macro"); }
            try { CreateSearchMacros(); } catch (Exception ex) { _logger.LogWarning(ex, "CreateSearchMacros failed; continuing without file_search macro"); }
        }
    }

    public void CreateMarkdownViews()
    {
        Execute(@"CREATE OR REPLACE VIEW markdown_headings AS
SELECT
  d.uri AS document_uri,
  h.uri AS heading_uri,
  CAST(json_extract(h.properties, '$.level') AS INTEGER) AS level,
  json_extract(h.properties, '$.text') AS text,
  json_extract(h.properties, '$.slug') AS slug,
  s.start_line,
  s.end_line,
  s.start_column,
  s.end_column
FROM node h
JOIN edge e ON e.destination_node_id = h.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON h.span_id = s.id;");

        Execute(@"CREATE OR REPLACE VIEW markdown_links AS
SELECT
  d.uri AS document_uri,
  l.uri AS link_uri,
  json_extract(l.properties, '$.href') AS href,
  json_extract(l.properties, '$.text') AS link_text,
  json_extract(l.properties, '$.title') AS link_title,
  s.start_line,
  s.end_line,
  s.start_column,
  s.end_column
FROM node l
JOIN edge e ON e.destination_node_id = l.id AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node d ON e.source_node_id = d.id AND d.kind = 'document'
LEFT JOIN span s ON l.span_id = s.id;");
    }

    public void CreateSnippetMacro()
    {
        // Add the snippet table macro for extracting code snippets with context
        // Note: This requires the UDFs to be registered first
        Execute(@"
CREATE OR REPLACE MACRO snippet(u, context_lines) AS TABLE (
  WITH base AS (
    SELECT
      repository_uri_container(u)     AS base,
      repository_uri_fragment(u)      AS frag,
      repository_uri_fragment_kind(u) AS kind,
      repository_uri_line_start(u)    AS l1,
      repository_uri_line_end(u)      AS l2
  ),
  doc AS (
    SELECT n.id AS doc_id, n.uri AS uri, a.text_content, a.media_type, a.storage_uri
    FROM base b
    JOIN node n ON n.container_uri_lowercase = lower(b.base)
    LEFT JOIN artifact a ON a.id = n.artifact_id
  ),
  edge_focus AS (
    SELECT e.id AS edge_id,
           ss.start_line   AS el1, ss.end_line   AS el2,
           ss.start_column AS ec1, ss.end_column AS ec2
    FROM base b
    JOIN edge e ON b.frag LIKE 'edge=%' AND substr(b.frag, 6) = CAST(e.id AS VARCHAR)
    LEFT JOIN span ss ON ss.id = e.source_span_id
  ),
  char_rng AS (
    SELECT
      CASE WHEN kind='char' THEN try_cast(split_part(substr(frag, 6), ',', 1) AS BIGINT) END AS c1,
      CASE WHEN kind='char' THEN try_cast(NULLIF(split_part(substr(frag, 6), ',', 2), '') AS BIGINT) END AS c2
    FROM base
  ),
  focus AS (
    SELECT
      COALESCE(
        (SELECT el1 FROM edge_focus),
        (SELECT l1  FROM base),
        (SELECT line_for_byte_offset(text_content, c1) FROM doc, char_rng),
        1
      ) AS fl1,
      COALESCE(
        (SELECT el2 FROM edge_focus),
        (SELECT l2  FROM base),
        (SELECT NULLIF(line_for_byte_offset(text_content, c2), 0) FROM doc, char_rng)
      ) AS fl2,
      COALESCE(
        (SELECT ec1 FROM edge_focus),
        (SELECT column_for_byte_offset(text_content, c1) FROM doc, char_rng)
      ) AS fc1,
      COALESCE(
        (SELECT ec2 FROM edge_focus),
        (SELECT column_for_byte_offset(text_content, c2) FROM doc, char_rng)
      ) AS fc2
  ),
  raw_text AS (
    SELECT
      CASE WHEN text_content IS NOT NULL THEN text_content
           ELSE COALESCE(binary_preview(storage_uri, 4096), '')
      END AS content
    FROM doc
  ),
  lines AS (
    SELECT
      ROW_NUMBER() OVER () AS ln,
      value AS line
    FROM raw_text,
         UNNEST(string_split(content, CHR(10))) AS t(value)
  ),
  win AS (
    SELECT
      GREATEST(1, COALESCE(fl1,1) - COALESCE(context_lines,3)) AS w1,
      COALESCE(COALESCE(fl2,fl1) + COALESCE(context_lines,3), 1 + COALESCE(context_lines,3)*2) AS w2
    FROM focus
  )
  SELECT
    ln AS line_number,
    line AS text,
    (ln BETWEEN fl1 AND COALESCE(fl2, fl1)) AS is_focus,
    CASE WHEN ln BETWEEN fl1 AND COALESCE(fl2, fl1) THEN fc1 ELSE NULL END AS focus_start_column,
    CASE WHEN ln BETWEEN fl1 AND COALESCE(fl2, fl1) THEN fc2 ELSE NULL END AS focus_end_column,
    language_from_media_type_or_uri((SELECT media_type FROM doc), (SELECT uri FROM doc)) AS language,
    (SELECT uri FROM doc) AS document_uri,
    repository_uri_join(
      (SELECT uri FROM doc),
      'line=' || CAST(fl1 AS VARCHAR) || COALESCE(',' || CAST(fl2 AS VARCHAR), '')
    ) AS resolved_uri
  FROM lines, win, focus
  WHERE ln BETWEEN w1 AND w2
  ORDER BY ln
);");
    }

    public void CreateXrayDocumentsMacro()
    {
        // Add the document inventory macro used by RepoQL tooling
        Execute(@"
CREATE OR REPLACE MACRO xray_documents() AS TABLE (
  WITH docs AS (
    SELECT id, uri, artifact_id FROM node WHERE kind = 'document'
  ),
  media AS (
    SELECT d.id AS doc_id, a.media_type, a.byte_size
    FROM docs d LEFT JOIN artifact a ON a.id = d.artifact_id
  ),
  parts AS (
    SELECT e.source_node_id AS doc_id, c.kind, COUNT(*) AS item_count
    FROM edge e
    JOIN node c ON c.id = e.destination_node_id
    WHERE e.is_composition = TRUE
    GROUP BY 1,2
  ),
  kinds AS (
    SELECT doc_id, string_agg(kind || ':' || CAST(item_count AS TEXT), ' ') AS kinds_summary
    FROM parts GROUP BY doc_id
  )
  SELECT
    d.uri                                        AS document_uri,
    repository_uri_file_name(d.uri)              AS file_name,
    media_type_base(m.media_type)                AS media_base,
    media_type_kind(m.media_type)                AS media_kind,
    m.byte_size                                  AS byte_size,
    COALESCE(k.kinds_summary, '')                AS kinds_summary
  FROM docs d
  LEFT JOIN media m ON m.doc_id = d.id
  LEFT JOIN kinds k ON k.doc_id = d.id
  ORDER BY lower(file_name)
);");
    }

    public void CreateXrayItemsMacro()
    {
        // First create the node_primary_fragment macro as a workaround for the 6-parameter limitation
        Execute(@"
CREATE OR REPLACE MACRO node_primary_fragment(kind, properties_json, start_line, end_line, start_byte, end_byte) AS (
  CASE
    WHEN start_line IS NOT NULL OR end_line IS NOT NULL THEN
      CASE
        WHEN end_line IS NULL THEN 'line=' || CAST(start_line AS VARCHAR)
        WHEN start_line IS NULL THEN 'line=,' || CAST(end_line AS VARCHAR)
        ELSE 'line=' || CAST(start_line AS VARCHAR) || ',' || CAST(end_line AS VARCHAR)
      END
    WHEN start_byte IS NOT NULL OR end_byte IS NOT NULL THEN
      CASE
        WHEN end_byte IS NULL THEN 'char=' || CAST(start_byte AS VARCHAR)
        WHEN start_byte IS NULL THEN 'char=,' || CAST(end_byte AS VARCHAR)
        ELSE 'char=' || CAST(start_byte AS VARCHAR) || ',' || CAST(end_byte AS VARCHAR)
      END
    ELSE NULL
  END
);");

        // Add the items-within-documents macro for exploring document structure
        Execute(@"
CREATE OR REPLACE MACRO xray_items(include_kinds, max_per_document) AS TABLE (
  WITH docs AS (SELECT id, uri FROM node WHERE kind='document'),
  cand AS (
    SELECT
      d.id AS doc_id, d.uri AS document_uri,
      c.id AS item_id, c.kind AS item_kind,
      node_display_label(c.kind, c.properties) AS item_label,
      s.start_line, s.end_line, s.start_byte, s.end_byte, e.ordinal,
      node_primary_fragment(c.kind, c.properties, s.start_line, s.end_line, s.start_byte, s.end_byte) AS frag
    FROM docs d
    JOIN edge e ON e.source_node_id=d.id AND e.is_composition=TRUE
    JOIN node c ON c.id=e.destination_node_id
    LEFT JOIN span s ON s.id=c.span_id
    WHERE include_kinds IS NULL
       OR EXISTS (
         SELECT 1 FROM UNNEST(string_split(include_kinds, ',')) k(value)
         WHERE lower(trim(k.value)) = lower(c.kind)
       )
  ),
  ranked AS (
    SELECT *,
           ROW_NUMBER() OVER (
             PARTITION BY doc_id
             ORDER BY COALESCE(start_line, 2147483647), COALESCE(ordinal, 2147483647), item_id
           ) AS rn
    FROM cand
  )
  SELECT
    document_uri,
    repository_uri_file_name(document_uri) AS file_name,
    item_kind,
    COALESCE(item_label, '?') AS item_label,
    COALESCE(repository_uri_join(document_uri, frag), document_uri) AS item_uri
  FROM ranked
  WHERE rn <= COALESCE(CAST(max_per_document AS INTEGER), 8)
  ORDER BY lower(file_name), rn
);");
    }

    public void CreateXrayLinesMacro()
    {
        // Add the combined text output macro that uses xray_documents and xray_items
        Execute(@"
CREATE OR REPLACE MACRO xray_lines(lod, include_kinds, max_per_document) AS TABLE (
  WITH d AS (SELECT * FROM xray_documents()),
       i AS (SELECT * FROM xray_items(include_kinds, max_per_document))
  SELECT file_name, 0 AS ord,
         (file_name || ' · ' || COALESCE(media_kind, media_base) ||
          CASE WHEN kinds_summary <> '' THEN '  ' || kinds_summary ELSE '' END) AS line
  FROM d
  UNION ALL
  SELECT repository_uri_file_name(document_uri) AS file_name, 1 AS ord,
         ('  - ' || item_kind || ': ' || item_label || '  (' || item_uri || ')') AS line
  FROM i
  WHERE CAST(lod AS INTEGER) >= 1
);");
    }

    public void CreateSearchMacros()
    {
        EnsureDocumentSearchSchema();

        Execute(@"
CREATE OR REPLACE MACRO zero_one(x) AS (
  CASE WHEN MAX(x) OVER () IS NULL OR MAX(x) OVER () = 0 THEN 0 ELSE COALESCE(x,0) / NULLIF(MAX(x) OVER (),0) END
);");

        Execute(@"
CREATE OR REPLACE MACRO combine(bm25n, fuzzn, semn, wb := 0.45, wf := 0.45, ws := 0.10) AS (
  coalesce(wb * bm25n, 0) + coalesce(wf * fuzzn, 0) + coalesce(ws * semn, 0)
);");

        // capability wrapper: default to JSON cosine over document_embedding
        Execute(@"
CREATE OR REPLACE MACRO vss_candidates(qvec_json, top_k) AS TABLE (
  SELECT doc_id, cosine_similarity_json(qvec_json, embedding) AS sem
  FROM document_embedding
  ORDER BY sem DESC
  LIMIT CAST(top_k AS BIGINT)
);");

        Execute(@"
CREATE OR REPLACE MACRO file_search(q, k := 50, max_cand := 5000) AS TABLE (
WITH score_source AS (
  SELECT
    ds.doc_id,
    ds.uri,
    CASE WHEN position(lower(q) in ds.search_key) > 0 THEN 1.0 ELSE 0.0 END AS bm25,
    match_score(q, ds.search_key) AS fuzz
  FROM document_search ds
),
ranked_lex AS (
  SELECT *
  FROM score_source
  ORDER BY coalesce(bm25, 0) DESC, fuzz DESC, length(uri)
  LIMIT CAST(max_cand AS BIGINT)
),
normalized_lex AS (
  SELECT
    doc_id,
    uri,
    zero_one(bm25) AS bm25n,
    zero_one(fuzz) AS fuzzn
  FROM ranked_lex
),
qv AS (
  SELECT embed_text_json('Represent this sentence for searching relevant passages: ' || q) AS qjson
),
sem_candidates AS (
  SELECT * FROM vss_candidates((SELECT qjson FROM qv), max_cand)
),
sem_norm AS (
  SELECT doc_id, (sem / NULLIF(MAX(sem) OVER (), 0)) AS semn FROM sem_candidates
),
union_ids AS (
  SELECT doc_id FROM normalized_lex
  UNION
  SELECT doc_id FROM sem_candidates
)
SELECT
  u.doc_id,
  ds.uri,
  COALESCE(lx.bm25n, 0) AS bm25n,
  COALESCE(lx.fuzzn, 0) AS fuzzn,
  COALESCE(sn.semn, NULL) AS semn,
  combine(COALESCE(lx.bm25n, 0), COALESCE(lx.fuzzn, 0), sn.semn) AS score
FROM union_ids u
LEFT JOIN normalized_lex lx USING(doc_id)
LEFT JOIN sem_norm sn USING(doc_id)
JOIN document_search ds USING(doc_id)
ORDER BY score DESC, length(ds.uri)
LIMIT CAST(k AS BIGINT)
);");
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
            return new Artifact
            {
                Id = r.GetGuid(0),
                Digest = r.GetString(1),
                Size = r.GetInt64(2),
                MediaType = ParseMediaType(r.IsDBNull(3) ? null : r.GetString(3)),
                Text = r.IsDBNull(4) ? null : r.GetString(4),
                StoreUri = r.IsDBNull(5) ? null : r.GetString(5),
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
            return new Artifact
            {
                Id = r.GetGuid(0),
                Digest = r.GetString(1),
                Size = r.GetInt64(2),
                MediaType = ParseMediaType(r.IsDBNull(3) ? null : r.GetString(3)),
                Text = r.IsDBNull(4) ? null : r.GetString(4),
                StoreUri = r.IsDBNull(5) ? null : r.GetString(5),
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
        EnsureDocumentSearchSchema();

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
                Execute("DELETE FROM document_search;", tx);
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

        EnsureDocumentSearchIndexes();

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
                artifact.StoreUri,
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
        catch
        {
            tx.Rollback();
            throw;
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
        var dataJson = r.IsDBNull(7) ? "{}" : r.GetString(7);
        var data = JsonNode.Parse(dataJson)?.AsObject() ?? new JsonObject();
        return new Annotation
        {
            Id = r.GetGuid(0),
            SemanticKey = r.IsDBNull(1) ? null : r.GetString(1),
            Kind = r.GetString(2),
            Severity = r.GetString(3),
            Source = r.GetString(4),
            RuleId = r.IsDBNull(5) ? null : r.GetString(5),
            Message = r.GetString(6),
            Data = data,
            ScopeDocumentId = r.GetGuid(8),
            TargetNodeId = r.IsDBNull(9) ? null : r.GetGuid(9),
            TargetEdgeId = r.IsDBNull(10) ? null : r.GetGuid(10),
            TargetSpanId = r.IsDBNull(11) ? null : r.GetGuid(11),
            TargetUri = r.IsDBNull(12) ? null : r.GetString(12),
            CreatedAt = DateTime.SpecifyKind(r.GetDateTime(13), DateTimeKind.Utc),
            ExpiresAt = r.IsDBNull(14) ? null : DateTime.SpecifyKind(r.GetDateTime(14), DateTimeKind.Utc)
        };
    }

    // ---------- helpers ----------

    private void EnsureDocumentSearchSchema()
    {
        Execute(@"CREATE TABLE IF NOT EXISTS document_search (
  doc_id    UUID PRIMARY KEY,
  uri       VARCHAR NOT NULL,
  search_key VARCHAR NOT NULL,
  basename  VARCHAR,
  dirname   VARCHAR
);");

        EnsureDocumentSearchIndexes();
    }

    private void EnsureDocumentSearchIndexes()
    {
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS document_search_uri_idx ON document_search(uri);");
        Execute("CREATE INDEX IF NOT EXISTS document_search_search_idx ON document_search(search_key);");
        Execute("CREATE INDEX IF NOT EXISTS document_search_basename_idx ON document_search(basename);");
        Execute("CREATE INDEX IF NOT EXISTS document_search_dirname_idx ON document_search(dirname);");
    }

    private void EnableRecommendedExtensions()
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
        try
        {
            Execute(sql);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AddParameters(DuckDBCommand cmd, params object?[] values)
    {
        foreach (var v in values)
            cmd.Parameters.Add(new DuckDBParameter { Value = v ?? DBNull.Value });
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
                    try { return System.IO.Path.GetFileName(value); } catch { return value; }
                }
            }
        }
        catch { }
        return null;
    }
}
