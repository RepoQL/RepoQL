-- Returns URIs matching the pattern specification OR from a provided URI list.
-- Supports semicolon-delimited patterns, negative patterns with ! prefix,
-- and fragment patterns (#symbol=..., #line=...).
--
-- Examples:
--   SELECT * FROM glob_files('src/**/*.cs');                        -- All .cs files in src
--   SELECT * FROM glob_files('src/**;lib/**');                      -- Files in src OR lib
--   SELECT * FROM glob_files('src/**;!src/tests/**');               -- src files excluding tests
--   SELECT * FROM glob_files('src/Foo.cs#symbol=MyClass.*');        -- Direct children of MyClass
--   SELECT * FROM glob_files('src/Foo.cs#symbol=MyClass.**');       -- All descendants of MyClass
--   SELECT * FROM glob_files('src/**/*.cs#symbol=*Handler');        -- Handlers in all .cs files
--   SELECT * FROM glob_files(uris := (SELECT list(uri) FROM search('auth', k := 5)));  -- From search results
--
-- Parameters:
--   pattern_spec    - Pattern specification (semicolon-delimited, ! for negatives)
--   uris            - Array of URIs to return directly (alternative to pattern_spec)
--   ignore_case     - Case insensitive matching (default TRUE)
--   default_scheme  - Default scheme for patterns without one (default 'file:///')
--
-- Note: If both pattern_spec and uris are provided, uris takes precedence.

CREATE OR REPLACE MACRO glob_files(
    pattern_spec := NULL,
    uris := NULL,
    ignore_case := TRUE,
    default_scheme := 'file:///'
) AS TABLE
WITH params AS (
    SELECT
        NULLIF(TRIM(COALESCE(CAST(pattern_spec AS VARCHAR), '')), '') AS pattern,
        uris AS uri_list
)
SELECT n.uri
FROM node n
CROSS JOIN params p
WHERE
    CASE
        -- Branch 1: URI list provided - filter to those URIs
        WHEN p.uri_list IS NOT NULL THEN
            list_contains(CAST(p.uri_list AS VARCHAR[]), n.uri)
        -- Branch 2: Pattern with fragment - match nodes
        WHEN p.pattern IS NOT NULL AND position('#' IN p.pattern) > 0 THEN
            matches_glob(n.uri, p.pattern, ignore_case, default_scheme) IS TRUE
        -- Branch 3: Pattern without fragment - match documents only
        WHEN p.pattern IS NOT NULL THEN
            n.kind = 'document'
            AND matches_glob(n.uri, p.pattern, ignore_case, default_scheme) IS TRUE
        -- Branch 4: Neither provided - return all documents
        ELSE n.kind = 'document'
    END
ORDER BY n.uri;
