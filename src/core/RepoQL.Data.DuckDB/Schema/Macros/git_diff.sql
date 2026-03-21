-- Returns file changes between two git refs (branches, commits, tags).
-- On-demand UDF - computes diff at query time using LibGit2Sharp.
-- Parameters:
--   from_ref: Starting ref (branch name, tag, or commit SHA)
--   to_ref: Ending ref (defaults to 'HEAD')
--   scope: Optional glob pattern to filter results (src/**/*.cs, src/**;!**/tests/**)
-- Returns: uri (file:/// URI), change_type, old_uri, insertions, deletions, is_binary
-- Examples:
--   SELECT * FROM git_diff('HEAD~1');
--   SELECT * FROM git_diff('main', 'feature-branch');
--   SELECT * FROM git_diff('HEAD~5', 'HEAD', 'src/**/*.cs');
--   SELECT * FROM git_diff('HEAD~1', 'HEAD', 'src/**;!**/tests/**');
CREATE OR REPLACE MACRO git_diff(from_ref, to_ref := 'HEAD', scope := NULL) AS TABLE (
    WITH raw_result AS (
        SELECT _git_diff_internal(from_ref::VARCHAR, to_ref::VARCHAR) AS json_result
    ),
    parsed AS (
        SELECT j.value AS obj
        FROM raw_result, json_each(raw_result.json_result::JSON) AS j
        WHERE j.type = 'OBJECT'
    ),
    -- Extract error message if present (scalar subquery forces evaluation)
    error_msg AS (
        SELECT obj->>'__udf_error__' AS msg FROM parsed WHERE obj->>'__udf_error__' IS NOT NULL LIMIT 1
    )
    -- COALESCE with scalar subquery forces error evaluation before WHERE filters rows
    SELECT
        COALESCE((SELECT CASE WHEN msg IS NOT NULL THEN error(msg) END FROM error_msg), 0) AS _error_guard,
        obj->>'uri' AS uri,
        obj->>'change_type' AS change_type,
        obj->>'old_uri' AS old_uri,
        CAST(obj->>'insertions' AS INTEGER) AS insertions,
        CAST(obj->>'deletions' AS INTEGER) AS deletions,
        CAST(obj->>'is_binary' AS BOOLEAN) AS is_binary
    FROM parsed
    WHERE obj->>'__udf_error__' IS NULL
      AND (scope IS NULL OR matches_glob(obj->>'uri', scope))
);
