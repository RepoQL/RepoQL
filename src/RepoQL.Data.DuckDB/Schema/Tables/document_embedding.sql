CREATE TABLE IF NOT EXISTS document_embedding (
    doc_id         UUID NOT NULL,
    node_id        UUID NOT NULL,
    chunk_index    INTEGER NOT NULL DEFAULT 0,  -- 0 = whole content or first chunk; 1+ = subsequent chunks
    embedding_type VARCHAR NOT NULL DEFAULT 'full' CHECK (embedding_type IN ('structure', 'full')),
    uri            VARCHAR NOT NULL,
    scope          VARCHAR NOT NULL CHECK (scope IN ('document', 'object')),
    model          VARCHAR NOT NULL,
    dim            INTEGER NOT NULL,
    embedding      FLOAT[] NOT NULL, -- Native list (50% smaller than JSON, uses DuckDB's list_cosine_similarity)
    start_byte     BIGINT,           -- NULL = whole content; otherwise byte range of chunk
    end_byte       BIGINT,
    updated_at     TIMESTAMP NOT NULL,
    PRIMARY KEY (doc_id, node_id, chunk_index, embedding_type)
);

CREATE INDEX IF NOT EXISTS document_embedding_scope_idx ON document_embedding(scope);
CREATE INDEX IF NOT EXISTS document_embedding_uri_idx ON document_embedding(uri);
CREATE INDEX IF NOT EXISTS document_embedding_model_idx ON document_embedding(model);
CREATE INDEX IF NOT EXISTS document_embedding_doc_chunk_idx ON document_embedding(doc_id, chunk_index);
CREATE INDEX IF NOT EXISTS document_embedding_type_idx ON document_embedding(embedding_type);
