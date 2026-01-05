-- Ephemeral VSS (Vector Similarity Search) index tables.
-- These are populated during idle processing with HNSW indexes for fast approximate nearest neighbor search.
-- The tables always exist (empty on startup) so the search macro can reference them unconditionally.
-- HNSW indexes are created during idle processing via VssIndexManager.

-- VSS index for 384-dimensional embeddings (all-MiniLM-L6-v2, BGE-small)
CREATE TABLE IF NOT EXISTS _vss_index_384 (
    node_id        UUID NOT NULL,
    doc_id         UUID NOT NULL,
    embedding_type VARCHAR NOT NULL,
    vec            FLOAT[384] NOT NULL
);

-- VSS index for 768-dimensional embeddings (e.g., BGE-base, MPNet)
CREATE TABLE IF NOT EXISTS _vss_index_768 (
    node_id        UUID NOT NULL,
    doc_id         UUID NOT NULL,
    embedding_type VARCHAR NOT NULL,
    vec            FLOAT[768] NOT NULL
);

-- VSS index for 1024-dimensional embeddings (e.g., E5-large, BGE-large)
CREATE TABLE IF NOT EXISTS _vss_index_1024 (
    node_id        UUID NOT NULL,
    doc_id         UUID NOT NULL,
    embedding_type VARCHAR NOT NULL,
    vec            FLOAT[1024] NOT NULL
);

-- Note: HNSW indexes are created dynamically by VssIndexManager during idle processing.
-- They are ephemeral (in-memory only) to avoid DuckDB VSS persistence bugs.
-- If the database restarts, VssIndexManager will rebuild them on the next idle cycle.
