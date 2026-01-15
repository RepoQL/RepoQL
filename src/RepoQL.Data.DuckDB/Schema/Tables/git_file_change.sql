-- Files modified in each commit.
-- Links commits to file URIs for hotspot analysis and file history queries.
CREATE TABLE IF NOT EXISTS git_file_change (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    commit_hash TEXT NOT NULL,                -- References git_commit.hash
    uri TEXT NOT NULL,                        -- file:/// URI (e.g., file:///src/Foo.cs)
    change_type TEXT NOT NULL,                -- 'A' (add), 'M' (modify), 'D' (delete), 'R' (rename), 'C' (copy)
    old_uri TEXT,                             -- Previous URI for renames/copies
    insertions INTEGER DEFAULT 0,
    deletions INTEGER DEFAULT 0,
    is_binary BOOLEAN DEFAULT FALSE
);

-- Index for commit lookups
CREATE INDEX IF NOT EXISTS idx_git_file_change_commit ON git_file_change(commit_hash);

-- Index for file history queries
CREATE INDEX IF NOT EXISTS idx_git_file_change_uri ON git_file_change(uri);
