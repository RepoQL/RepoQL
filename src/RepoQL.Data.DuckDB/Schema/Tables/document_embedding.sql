CREATE TABLE IF NOT EXISTS document_embedding (
    doc_id     UUID NOT NULL,
    node_id    UUID NOT NULL,
    uri        VARCHAR NOT NULL,
    scope      VARCHAR NOT NULL CHECK (scope IN ('document', 'object')),
    model      VARCHAR NOT NULL,
    dim        INTEGER NOT NULL,
    embedding  VARCHAR NOT NULL, -- JSON float array
    updated_at TIMESTAMP NOT NULL,
    PRIMARY KEY (doc_id, node_id),
    FOREIGN KEY (doc_id) REFERENCES node(id),
    FOREIGN KEY (node_id) REFERENCES node(id)
);

CREATE INDEX IF NOT EXISTS document_embedding_scope_idx ON document_embedding(scope);
CREATE INDEX IF NOT EXISTS document_embedding_uri_idx ON document_embedding(uri);
CREATE INDEX IF NOT EXISTS document_embedding_model_idx ON document_embedding(model);
