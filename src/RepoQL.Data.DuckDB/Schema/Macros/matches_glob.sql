-- Wrapper macro for repoql_matches_glob UDF with default parameters.
-- Extends glob_match with:
--   - Semicolon-delimited compound patterns: file:///src/**;file:///lib/**
--   - Exclusion patterns with ! prefix: !file:///tests/**
--   - Fragment patterns: #symbol=MyClass.*, #line=10,*
CREATE OR REPLACE MACRO matches_glob(
    uri,
    pattern,
    ignore_case := TRUE,
    default_scheme := 'file:///'
) AS (
    repoql_matches_glob(uri, pattern, ignore_case, default_scheme)
);
