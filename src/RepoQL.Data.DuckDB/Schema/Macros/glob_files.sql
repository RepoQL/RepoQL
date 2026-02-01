-- Returns URIs matching the pattern specification OR from a provided URI list.
-- Uses registry-based line-range globbing for pattern matching.
-- Supports semicolon-delimited patterns, negative patterns with ! prefix,
-- symbol patterns (#symbol=...), line range patterns (#line=...), and exclusions.
--
-- Examples:
--   SELECT * FROM glob_files('src/**/*.cs');                              -- All .cs files in src
--   SELECT * FROM glob_files('src/**;lib/**');                            -- Files in src OR lib
--   SELECT * FROM glob_files('src/**;!src/tests/**');                     -- src files excluding tests
--   SELECT * FROM glob_files('src/**/*.cs#symbol=*');                     -- All symbols in .cs files
--   SELECT * FROM glob_files('src/**/*.cs#symbol=*Handler');              -- Handlers in all .cs files
--   SELECT * FROM glob_files('src/**/*.cs#symbol=*;!#line=1,30');         -- Symbols minus header region
--   SELECT * FROM glob_files('src/Foo.cs#symbol=MyClass;!#line=35,40');   -- Partial symbol (split)
--   SELECT * FROM glob_files(uris := (SELECT list(uri) FROM search('auth', k := 5)));  -- From search results
--
-- Parameters:
--   pattern_spec    - Pattern specification (semicolon-delimited, ! for negatives)
--   uris            - Array of URIs to return directly (alternative to pattern_spec)
--   ignore_case     - Case insensitive matching (default TRUE) - used for uri list matching only
--   default_scheme  - Default scheme for patterns without one (default 'file:///') - reserved for future use
--
-- Note: If both pattern_spec and uris are provided, uris takes precedence.
-- Pattern matching uses the in-memory URI registry with line-range-based operations.

CREATE OR REPLACE MACRO glob_files(
    pattern_spec := NULL,
    uris := NULL,
    ignore_case := 'true',
    default_scheme := 'file:///'
) AS TABLE
WITH params AS (
    SELECT
        NULLIF(TRIM(COALESCE(CAST(pattern_spec AS VARCHAR), '')), '') AS pattern,
        uris AS uri_list
)
SELECT * FROM (
    -- Branch 1: URI list provided - filter nodes to those URIs
    SELECT n.uri
    FROM node n
    CROSS JOIN params p
    WHERE p.uri_list IS NOT NULL
        AND list_contains(CAST(p.uri_list AS VARCHAR[]), n.uri)

    UNION ALL

    -- Branch 2: Pattern provided - use registry-based glob
    -- The structured UDF returns JSON array, use json_each to convert to rows
    SELECT j.value->>'uri' AS uri
    FROM json_each(_glob_files_internal((SELECT pattern FROM params))) AS j
    WHERE (SELECT pattern FROM params) IS NOT NULL
        AND (SELECT uri_list FROM params) IS NULL
        AND j.type = 'OBJECT'

    UNION ALL

    -- Branch 3: Neither provided - return all documents
    SELECT n.uri
    FROM node n
    WHERE n.kind = 'document'
        AND (SELECT pattern FROM params) IS NULL
        AND (SELECT uri_list FROM params) IS NULL
)
ORDER BY uri;
