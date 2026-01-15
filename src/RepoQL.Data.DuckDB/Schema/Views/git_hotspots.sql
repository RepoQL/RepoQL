-- Ranks files by change frequency for hotspot analysis.
-- High-churn files often correlate with complexity and bugs.
-- Returns URIs that can be joined with Files view or passed to other tools.
CREATE OR REPLACE VIEW git_hotspots AS
SELECT
    fc.uri,
    COUNT(*) AS commits,
    COUNT(DISTINCT c.author_email) AS authors,
    SUM(fc.insertions + fc.deletions) AS churn,
    SUM(fc.insertions) AS total_insertions,
    SUM(fc.deletions) AS total_deletions,
    MIN(c.author_date) AS first_changed,
    MAX(c.author_date) AS last_changed
FROM git_file_change fc
JOIN git_commit c ON fc.commit_hash = c.hash
GROUP BY fc.uri;
