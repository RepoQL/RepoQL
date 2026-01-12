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

-- Get URIs from glob pattern or uri list
scope_uris AS (
    SELECT list(uri) AS uris
    FROM glob_files(
        pattern_spec := (SELECT scope_glob FROM params),
        uris := (SELECT uri_list FROM params)
    )
    WHERE (SELECT scope_glob FROM params) IS NOT NULL
       OR (SELECT uri_list FROM params) IS NOT NULL
),

-- Get candidates from internal search with symbol mode optimization
candidates AS (
    SELECT
        sc.uri,
        sc.scope AS result_scope,
        sc.symbol,
        sc.kind,
        sc.headline,
        sc.line_start,
        sc.line_end,
        sc.score,
        sc.confidence,
        split_part(sc.uri, '#', 1) AS document_uri
    FROM _search_candidates(
        q,
        mode := 'symbol',
        k := k * 2  -- Fetch extra since we filter to objects
    ) sc
    WHERE sc.scope = 'object'
),

-- Apply scope filter if provided
filtered AS (
    SELECT c.*
    FROM candidates c
    CROSS JOIN params p
    LEFT JOIN scope_uris su ON TRUE
    WHERE p.scope_glob IS NULL
       OR list_contains(CAST(su.uris AS VARCHAR[]), c.document_uri)
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
