-- Returns line-by-line git blame information for files matching a pattern.
-- On-demand UDF - computes blame at query time using LibGit2Sharp.
-- Parameters:
--   scope: File URI or glob pattern (file:///src/Foo.cs, src/**/*.cs)
--   start_line: Optional start line filter (1-based)
--   end_line: Optional end line filter (1-based)
-- Examples:
--   SELECT * FROM git_blame('file:///src/Foo.cs');
--   SELECT * FROM git_blame('src/**/*.cs', 1, 50);
--   SELECT * FROM git_blame('src/**;!**/tests/**');
CREATE OR REPLACE MACRO git_blame(scope, start_line := NULL, end_line := NULL) AS TABLE (
    WITH files AS (
        SELECT g.uri AS file_uri FROM glob_files(scope) g
    ),
    blame_results AS (
        SELECT
            f.file_uri,
            _git_blame_internal(f.file_uri::VARCHAR, COALESCE(start_line::VARCHAR, ''), COALESCE(end_line::VARCHAR, '')) AS json_result
        FROM files f
    ),
    parsed AS (
        SELECT
            br.file_uri,
            j.value AS obj
        FROM blame_results br, json_each(br.json_result::JSON) AS j
        WHERE j.type = 'OBJECT'
    ),
    -- Extract error message if present (scalar subquery forces evaluation)
    error_msg AS (
        SELECT obj->>'__udf_error__' AS msg FROM parsed WHERE obj->>'__udf_error__' IS NOT NULL LIMIT 1
    )
    -- COALESCE with scalar subquery forces error evaluation before WHERE filters rows
    SELECT
        COALESCE((SELECT CASE WHEN msg IS NOT NULL THEN error(msg) END FROM error_msg), 0) AS _error_guard,
        file_uri AS uri,
        CAST(obj->>'line_number' AS INTEGER) AS line_number,
        obj->>'commit_hash' AS commit_hash,
        obj->>'author_name' AS author_name,
        obj->>'author_email' AS author_email,
        CAST(obj->>'author_date' AS TIMESTAMPTZ) AS author_date,
        obj->>'message' AS message
    FROM parsed
    WHERE obj->>'__udf_error__' IS NULL
);
