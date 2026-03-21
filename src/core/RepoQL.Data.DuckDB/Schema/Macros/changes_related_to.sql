-- changes_related_to: Find git commits related to a semantic concept
--
-- Purpose: Supports two key use cases:
--   1. "What changes might have caused this problem I'm seeing"
--   2. "Find me an example change like the one I'm planning to make"
--
-- Uses semantic search to find conceptually related files, then joins with
-- git history to find commits that touched those files.
--
-- Examples:
--   SELECT * FROM changes_related_to('indexing pipeline', since := '7 days');
--   SELECT * FROM changes_related_to('UDF implementation', since_commit := 'abc123');

CREATE OR REPLACE MACRO changes_related_to(
    keywords,
    since := NULL,
    until := NULL,
    since_commit := NULL,
    until_commit := NULL,
    k := 30
) AS TABLE
WITH
-- 1. Find semantically related files (using keywords directly)
related_files AS (
    SELECT uri
    FROM search(TRIM(COALESCE(CAST(keywords AS VARCHAR), '')), k := k)
    WHERE TRIM(COALESCE(CAST(keywords AS VARCHAR), '')) != ''
),

-- 2. Normalize parameters inline
params AS (
    SELECT
        CASE
            WHEN since IS NULL THEN NULL
            WHEN TRY_CAST(since AS TIMESTAMPTZ) IS NOT NULL THEN TRY_CAST(since AS TIMESTAMPTZ)
            ELSE NOW() - TRY_CAST(since AS INTERVAL)
        END AS since_ts,
        CASE
            WHEN until IS NULL THEN NULL
            WHEN TRY_CAST(until AS TIMESTAMPTZ) IS NOT NULL THEN TRY_CAST(until AS TIMESTAMPTZ)
            ELSE NOW() - TRY_CAST(until AS INTERVAL)
        END AS until_ts,
        NULLIF(TRIM(COALESCE(CAST(since_commit AS VARCHAR), '')), '') AS since_hash,
        NULLIF(TRIM(COALESCE(CAST(until_commit AS VARCHAR), '')), '') AS until_hash
),

-- 3. Join to find commits touching related files
commit_matches AS (
    SELECT
        c.hash,
        c.author_name,
        c.committer_date,
        c.message,
        c.files_changed,
        c.insertions,
        c.deletions,
        COUNT(DISTINCT fc.uri) AS related_file_count,
        STRING_AGG(DISTINCT fc.uri, ';' ORDER BY fc.uri) AS related_file_uris
    FROM git_commit c
    JOIN git_file_change fc ON c.hash = fc.commit_hash
    JOIN related_files rf ON fc.uri = rf.uri
    CROSS JOIN params p
    WHERE
      -- Time-based filtering
      (p.since_ts IS NULL OR c.committer_date >= p.since_ts)
      AND (p.until_ts IS NULL OR c.committer_date <= p.until_ts)
      -- Commit hash filtering (prefix match)
      AND (p.since_hash IS NULL OR c.hash > p.since_hash)
      AND (p.until_hash IS NULL OR c.hash <= p.until_hash OR c.hash LIKE p.until_hash || '%')
    GROUP BY c.hash, c.author_name, c.committer_date, c.message, c.files_changed, c.insertions, c.deletions
)

-- 4. Format output
SELECT
    hash[1:8] AS commit,
    hash,
    committer_date::DATE AS date,
    author_name AS author,
    message[1:80] AS message,
    files_changed,
    related_file_count AS related_files,
    related_file_uris AS files,
    insertions,
    deletions
FROM commit_matches
ORDER BY committer_date DESC;
