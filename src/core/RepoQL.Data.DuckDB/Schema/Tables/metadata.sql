-- Metadata table for storing RepoQL configuration and version tracking.
-- Used to detect version changes and trigger cache invalidation.
CREATE TABLE IF NOT EXISTS metadata (
    key VARCHAR PRIMARY KEY,
    value VARCHAR NOT NULL,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
