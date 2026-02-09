-- Returns staged and unstaged working copy patches.
-- On-demand UDF - computes working copy diffs at runtime using LibGit2Sharp.
-- Parameters:
--   scope: Optional glob pattern to filter results (src/**/*.cs, src/**;!**/tests/**)
-- Returns: uri, diff_target, patch, insertions, deletions, is_binary
-- Examples:
--   SELECT * FROM git_patches();
--   SELECT * FROM git_patches('src/**/*.cs');
--   SELECT * FROM git_patches('src/**;!**/tests/**');
CREATE OR REPLACE MACRO git_patches(scope := NULL, include_unstaged := TRUE) AS TABLE (
    WITH raw_result AS (
        SELECT _git_working_patches_internal(COALESCE(include_unstaged::VARCHAR, 'true')) AS json_result
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
        obj->>'diff_target' AS diff_target,
        obj->>'patch' AS patch,
        CAST(obj->>'insertions' AS INTEGER) AS insertions,
        CAST(obj->>'deletions' AS INTEGER) AS deletions,
        CAST(obj->>'is_binary' AS BOOLEAN) AS is_binary
    FROM parsed
    WHERE obj->>'__udf_error__' IS NULL
      AND (scope IS NULL OR matches_glob(obj->>'uri', scope))
);
