-- Filesystems view: summarizes mounted file systems (imports) with statistics.
--
-- Purpose: Provides an overview of all data sources (local repo, imports, docs)
-- with file counts, line totals, languages, tokens, and indexing progress.
--
-- Examples:
--   SELECT * FROM Filesystems;
--   SELECT source_uri, file_count, languages FROM Filesystems WHERE scheme = 'github';

CREATE OR REPLACE VIEW filesystems AS
WITH doc_source AS (
    -- Derive a source key from each document node's URI that identifies its mount.
    -- For schemes with authority (github, local): scheme://authority/first-path-segment
    -- For schemes without (file, help): scheme://
    SELECT
        n.id,
        n.artifact_id,
        CASE
            WHEN n.uri LIKE 'github://%' OR n.uri LIKE 'local://%'
            THEN split_part(n.uri, '://', 1) || '://' ||
                 split_part(split_part(n.uri, '://', 2), '/', 1) || '/' ||
                 split_part(split_part(n.uri, '://', 2), '/', 2)
            ELSE split_part(n.uri, '://', 1) || '://'
        END AS source
    FROM node n
    WHERE n.kind = 'document'
),
stats AS (
    SELECT
        d.source,
        COUNT(*) AS file_count,
        SUM(a.byte_size) AS total_bytes,
        SUM(a.token_count) AS total_tokens,
        string_agg(DISTINCT media_type_kind(a.media_type), ', ' ORDER BY media_type_kind(a.media_type)) AS languages
    FROM doc_source d
    JOIN artifact a ON d.artifact_id = a.id
    GROUP BY 1
),
embed_stats AS (
    SELECT
        d.source,
        COUNT(*) AS indexed_count,
        COUNT(e.node_id) AS embedded_count
    FROM doc_source d
    LEFT JOIN document_embedding e ON d.id = e.node_id
    GROUP BY 1
)
SELECT
    COALESCE(m.id, s.source) AS id,
    COALESCE(m.scheme, split_part(s.source, '://', 1)) AS scheme,
    m.authority,
    COALESCE(m.source_uri, s.source) AS source_uri,
    s.file_count,
    s.total_bytes,
    s.total_tokens,
    s.languages,
    es.indexed_count,
    es.embedded_count,
    CASE WHEN es.indexed_count > 0
         THEN ROUND(100.0 * es.embedded_count / es.indexed_count, 1)
         ELSE 0 END AS embed_pct,
    m.mounted_at,
    COALESCE(m.enable_watching, FALSE) AS watching,
    COALESCE(m.enable_analysis, FALSE) AS analysis
FROM stats s
LEFT JOIN embed_stats es ON s.source = es.source
LEFT JOIN file_system_mount m ON rtrim(m.source_uri, '/') = rtrim(s.source, '/')
ORDER BY s.file_count DESC;
