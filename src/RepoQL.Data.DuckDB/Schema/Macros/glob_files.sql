-- Returns document URIs matching the pattern specification.
-- Supports semicolon-delimited patterns and negative patterns with ! prefix.
--
-- Examples:
--   SELECT * FROM glob_files('src/**/*.cs');                     -- All .cs files in src
--   SELECT * FROM glob_files('src/**;lib/**');                   -- Files in src OR lib
--   SELECT * FROM glob_files('src/**;!src/tests/**');            -- src files excluding tests
--   SELECT * FROM glob_files('!**/*.md;!**/*.txt');              -- Everything except .md and .txt
--   SELECT * FROM glob_files('');                                -- All documents (blank = everything)
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
    WHERE kind = 'document'
      AND matches_glob(uri, pattern_spec, ignore_case, default_scheme) IS TRUE
    ORDER BY uri
);
