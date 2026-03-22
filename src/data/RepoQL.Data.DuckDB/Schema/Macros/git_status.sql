-- Returns working copy status (modified, staged, untracked files).
-- On-demand UDF - queries git status at runtime using LibGit2Sharp.
-- Equivalent to `git status --porcelain`.
-- Parameters:
--   scope: Optional glob pattern to filter results (src/**/*.cs)
--   include_untracked: Include untracked files (default true)
--   include_ignored: Include ignored files (default false)
-- Returns: uri, index_status, work_tree_status, category, is_conflicted
-- Categories: staged, modified, staged+modified, untracked, conflict, ignored
-- Examples:
--   SELECT * FROM git_status();
--   SELECT * FROM git_status('src/**/*.cs');
--   SELECT * FROM git_status('**/*.cs;!**/tests/**');
--   SELECT * FROM git_status(include_untracked := false);
CREATE OR REPLACE MACRO git_status(scope := NULL, include_untracked := TRUE, include_ignored := FALSE) AS TABLE (
    WITH raw_result AS (
        SELECT _git_status_internal(
            COALESCE(include_untracked::VARCHAR, 'true'),
            COALESCE(include_ignored::VARCHAR, 'false')
        ) AS json_result
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
        obj->>'index_status' AS index_status,
        obj->>'work_tree_status' AS work_tree_status,
        obj->>'category' AS category,
        CAST(obj->>'is_conflicted' AS BOOLEAN) AS is_conflicted
    FROM parsed
    WHERE obj->>'__udf_error__' IS NULL
      AND (scope IS NULL OR matches_glob(obj->>'uri', scope))
);
