-- Returns URIs matching the pattern specification.
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
--
-- Parameters:
--   pattern_spec    - Pattern specification (semicolon-delimited, ! for negatives)
--   ignore_case     - Case insensitive matching (default TRUE)
--   default_scheme  - Default scheme for patterns without one (default 'file:///')

CREATE OR REPLACE MACRO glob_files(
    pattern_spec,
    ignore_case := TRUE,
    default_scheme := 'file:///'
) AS TABLE (
    SELECT uri
    FROM node
    WHERE
      CASE
        WHEN position('#' IN pattern_spec) > 0 THEN
          -- Pattern has fragment: match nodes (symbols, line ranges, etc.)
          matches_glob(uri, pattern_spec, ignore_case, default_scheme) IS TRUE
        ELSE
          -- No fragment: match documents only
          kind = 'document'
          AND matches_glob(uri, pattern_spec, ignore_case, default_scheme) IS TRUE
      END
    ORDER BY uri
);
