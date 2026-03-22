-- Recent commits from the last 7 days.
-- Useful for "what changed recently?" queries.
CREATE OR REPLACE VIEW git_recent AS
SELECT
    hash,
    author_name,
    author_email,
    author_date,
    message,
    files_changed,
    insertions,
    deletions
FROM git_commit
WHERE author_date > now() - INTERVAL '7 days'
ORDER BY author_date DESC;
