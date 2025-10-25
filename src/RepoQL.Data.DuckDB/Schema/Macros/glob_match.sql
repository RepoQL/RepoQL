CREATE OR REPLACE MACRO glob_match(
    uri,
    pattern,
    ignore_case := TRUE,
    default_scheme := 'file:///'
) AS (
    repoql_glob_match(uri, pattern, ignore_case, default_scheme)
);
