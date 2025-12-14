-- Persists file system mounts so they survive server restarts.
-- When a repository is imported, a record is created here and reloaded on startup.
CREATE TABLE IF NOT EXISTS file_system_mount (
    id TEXT PRIMARY KEY,              -- Mount ID, e.g. 'github:owner/repo@ref'
    scheme TEXT NOT NULL,             -- URI scheme, e.g. 'github'
    authority TEXT,                   -- URI authority, e.g. 'owner' (nullable for local mounts)
    path_prefix TEXT NOT NULL,        -- Path prefix for URI matching, e.g. 'repo'
    source_uri TEXT NOT NULL,         -- Original import URI
    local_path TEXT NOT NULL,         -- Physical path on disk
    mounted_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    include_in_enumeration BOOLEAN DEFAULT TRUE,
    enable_watching BOOLEAN DEFAULT FALSE,
    enable_analysis BOOLEAN DEFAULT FALSE
);
