-- Git commit metadata indexed from repository history.
-- Stores last 12 months of commits for code archaeology and change analysis.
CREATE TABLE IF NOT EXISTS git_commit (
    hash TEXT PRIMARY KEY,                    -- Full commit SHA
    author_name TEXT,
    author_email TEXT,
    author_date TIMESTAMPTZ,
    committer_name TEXT,
    committer_email TEXT,
    committer_date TIMESTAMPTZ,
    message TEXT,                             -- Full commit message
    parent_hashes TEXT[],                     -- Array of parent SHAs (empty for initial commit)
    files_changed INTEGER DEFAULT 0,
    insertions INTEGER DEFAULT 0,
    deletions INTEGER DEFAULT 0,
    indexed_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Index for time-based queries
CREATE INDEX IF NOT EXISTS idx_git_commit_author_date ON git_commit(author_date DESC);

-- Index for author lookups
CREATE INDEX IF NOT EXISTS idx_git_commit_author_email ON git_commit(author_email);
