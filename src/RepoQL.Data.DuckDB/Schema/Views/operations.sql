-- Operations view: shows all tracked operations with progress and filesystem enrichment.
--
-- Purpose: Give agents a single queryable surface for import/startup/reindex progress.
-- Joins with Filesystems for import operations to show file counts and languages.
--
-- Examples:
--   SELECT * FROM Operations;
--   SELECT * FROM Operations WHERE state = 'Running';
--   SELECT id, kind, scope, ready_percent, elapsed_s FROM Operations;

CREATE OR REPLACE VIEW Operations AS
SELECT
    o.id,
    split_part(o.description, ': ', 1) AS kind,
    CASE
        WHEN position(': ' IN o.description) > 0
        THEN substring(o.description FROM position(': ' IN o.description) + 2)
        ELSE NULL
    END AS scope,
    o.state,
    o.total_files,
    o.indexed_count,
    o.embedded_count,
    o.failed_count,
    o.ready_percent,
    CASE
        WHEN o.completed_at IS NOT NULL
        THEN ROUND(EXTRACT(EPOCH FROM (o.completed_at::TIMESTAMPTZ - o.created_at::TIMESTAMPTZ)), 1)
        ELSE ROUND(EXTRACT(EPOCH FROM (now() - o.created_at::TIMESTAMPTZ)), 1)
    END AS elapsed_s,
    o.created_at,
    o.completed_at,
    fs.file_count AS fs_files,
    fs.languages AS fs_languages,
    fs.embed_pct AS fs_embed_pct
FROM _operations() o
LEFT JOIN Filesystems fs
    ON split_part(o.description, ': ', 1) = 'import'
    AND rtrim(fs.source_uri, '/') = rtrim(
        CASE
            WHEN position(': ' IN o.description) > 0
            THEN substring(o.description FROM position(': ' IN o.description) + 2)
            ELSE ''
        END, '/')
ORDER BY o.created_at DESC;
