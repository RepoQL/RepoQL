-- Symbol search: find functions, classes, methods, and other objects by name.
-- Optimized for the "looking for a specific symbol" use case.
--
-- Parameters:
--   q           - Symbol name or pattern to search for
--   k           - Max results (default 20)
--   scope       - Glob pattern to filter files (e.g., 'src/**/*.cs', 'src/api/**;!**/tests/**')
--   uris        - Array of URIs to search within (alternative to scope, e.g., from search() results)
--   kind_filter - Filter by kind substring (e.g., 'type', 'member', 'function')
--
-- Examples:
--   SELECT * FROM search_symbol('ValidateToken');
--   SELECT * FROM search_symbol('Service', kind_filter := 'type');
--   SELECT * FROM search_symbol('Process', scope := 'src/**/*.cs');
--   SELECT * FROM search_symbol('Handler', scope := 'src/api/**;!**/tests/**');
--   SELECT * FROM search_symbol('Validate', uris := (SELECT list(uri) FROM search('auth', k := 5)));

CREATE OR REPLACE MACRO search_symbol(
    q,
    k := 20,
    scope := NULL,
    uris := NULL,
    kind_filter := NULL
) AS TABLE
WITH
params AS (
    SELECT
        NULLIF(TRIM(COALESCE(scope, '')), '') AS scope_glob,
        uris AS uri_list,
        NULLIF(TRIM(COALESCE(kind_filter, '')), '') AS kind_pat
),

pushdown_params AS (
    SELECT
        p.*,
        CASE
            -- Preserve existing uri-list semantics (glob_files gives uri_list precedence).
            WHEN p.uri_list IS NOT NULL THEN NULL
            -- Keep complex glob behavior in the existing glob_files + list filter path.
            WHEN p.scope_glob IS NULL THEN NULL
            WHEN position(';' IN p.scope_glob) > 0 THEN NULL
            WHEN position('#' IN p.scope_glob) > 0 THEN NULL
            WHEN left(p.scope_glob, 1) = '!' THEN NULL
            ELSE p.scope_glob
        END AS scope_pushdown_glob
    FROM params p
),

-- Resolve scope/URI inputs to document IDs once for typed filtering.
-- Preserve existing precedence: explicit uri_list overrides scope_glob.
scope_inputs AS (
    SELECT uri
    FROM UNNEST(CAST((SELECT uri_list FROM params) AS VARCHAR[])) AS u(uri)
    WHERE (SELECT uri_list FROM params) IS NOT NULL
    UNION ALL
    SELECT uri
    FROM glob_files(
        pattern_spec := (SELECT scope_glob FROM params)
    )
    WHERE (SELECT uri_list FROM params) IS NULL
      AND (SELECT scope_glob FROM params) IS NOT NULL
),

scope_doc_ids AS (
    SELECT list(DISTINCT d.id) AS doc_ids
    FROM scope_inputs si
    JOIN node d ON d.kind = 'document'
        AND d.uri = split_part(si.uri, '#', 1)
),

-- Get candidates from internal search with symbol mode optimization
candidates AS (
    SELECT
        sc.doc_id,
        sc.uri,
        sc.node_scope AS result_scope,
        sc.symbol,
        sc.kind,
        sc.headline,
        sc.line_start,
        sc.line_end,
        sc.score,
        sc.confidence
    FROM _search_candidates(
        q,
        mode := 'symbol',
        k := k * 2,  -- Fetch extra since we filter to objects
        uri_glob := (SELECT scope_pushdown_glob FROM pushdown_params)
    ) sc
    WHERE sc.node_scope = 'object'
),

-- Apply scope filter if provided
filtered AS (
    SELECT c.*
    FROM candidates c
    CROSS JOIN params p
    LEFT JOIN scope_doc_ids sd ON TRUE
    WHERE (p.scope_glob IS NULL AND p.uri_list IS NULL)
       OR list_contains(CAST(sd.doc_ids AS UUID[]), c.doc_id)
),

-- Apply kind filter if provided
kind_filtered AS (
    SELECT f.*
    FROM filtered f
    CROSS JOIN params p
    WHERE p.kind_pat IS NULL
       OR f.kind LIKE '%' || p.kind_pat || '%'
)

SELECT
    uri,
    symbol,
    kind,
    headline,
    line_start,
    line_end,
    score,
    confidence
FROM kind_filtered
ORDER BY score DESC
LIMIT k;
