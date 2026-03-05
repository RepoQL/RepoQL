-- Centralized scope filter: narrows the node universe before search.
-- Returns slim (node_id, doc_id, node_scope) rows — callers join for columns.
--
-- Uses glob_files as the driving table for URI narrowing:
--   uri_glob provided → only matching documents and their children
--   uri_glob NULL     → all documents and their children
--
-- This is the single source of truth for scope filtering.
-- All search macros call this instead of duplicating the filter logic.
--
-- Parameters:
--   uri_glob     - URI glob pattern resolved via glob_files (NULL = all)
--   uri_like     - SQL LIKE pattern, case-insensitive via ILIKE (NULL = all)
--   exclude_uri  - Document URI to exclude with all its children
--   scope        - 'document', 'object', or NULL for both
--
-- Examples:
--   SELECT * FROM _scope_filter(uri_glob := 'src/**/*.cs');
--   SELECT * FROM _scope_filter(uri_like := 'file:///src/%');
--   SELECT * FROM _scope_filter(exclude_uri := 'file:///src/Foo.cs', scope := 'document');

CREATE OR REPLACE MACRO _scope_filter(
    uri_glob := NULL,
    uri_like := NULL,
    exclude_uri := NULL,
    scope := NULL
) AS TABLE (
WITH
params AS (
    SELECT
        NULLIF(TRIM(CAST(uri_glob AS VARCHAR)), '') AS uri_filter,
        NULLIF(TRIM(CAST(uri_like AS VARCHAR)), '') AS like_filter,
        NULLIF(TRIM(CAST(exclude_uri AS VARCHAR)), '') AS exclude_filter,
        NULLIF(TRIM(CAST(scope AS VARCHAR)), '') AS scope_filter
),

-- glob_files drives narrowing: pattern → matching docs, NULL → all docs.
-- Falls back to matches_glob on node table when glob_files returns nothing
-- (e.g. in test environments without URI registry).
-- LIKE and exclude filters applied at document level before expansion.
glob_docs AS (
    SELECT n.id AS doc_id
    FROM params p
    CROSS JOIN glob_files(pattern_spec := p.uri_filter) gf
    JOIN node n ON n.uri = gf.uri AND n.kind = 'document'
    WHERE (p.like_filter IS NULL OR gf.uri ILIKE p.like_filter)
      AND (p.exclude_filter IS NULL OR gf.uri <> p.exclude_filter)
),

-- Fallback: use matches_glob directly on node URIs when glob_files found nothing
-- but a uri_filter was specified. This handles environments where the URI registry
-- isn't populated (e.g., test fixtures that insert directly into DuckDB).
fallback_docs AS (
    SELECT n.id AS doc_id
    FROM node n
    CROSS JOIN params p
    WHERE n.kind = 'document'
      AND p.uri_filter IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM glob_docs)
      AND matches_glob(n.uri, p.uri_filter) IS TRUE
      AND (p.like_filter IS NULL OR n.uri ILIKE p.like_filter)
      AND (p.exclude_filter IS NULL OR n.uri <> p.exclude_filter)
),

docs AS (
    SELECT doc_id FROM glob_docs
    UNION ALL
    SELECT doc_id FROM fallback_docs
),

-- Expand to all in-scope nodes: documents + children via span
scoped AS (
    -- Documents
    SELECT d.doc_id AS node_id, d.doc_id, 'document' AS node_scope
    FROM docs d

    UNION ALL

    -- Children of scoped documents
    SELECT child.id AS node_id, d.doc_id, 'object' AS node_scope
    FROM docs d
    JOIN span s ON s.document_id = d.doc_id
    JOIN node child ON child.span_id = s.id
    WHERE child.kind <> 'document'
)

SELECT node_id, doc_id, node_scope
FROM scoped
WHERE (SELECT scope_filter FROM params) IS NULL
   OR node_scope = (SELECT scope_filter FROM params)
);
