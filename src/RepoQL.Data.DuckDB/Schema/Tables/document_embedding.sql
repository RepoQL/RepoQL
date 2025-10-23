CREATE TABLE IF NOT EXISTS document_embedding (
                                                  doc_id    UUID PRIMARY KEY,
                                                  model     VARCHAR NOT NULL,
                                                  dim       INTEGER NOT NULL,
                                                  embedding VARCHAR NOT NULL, -- JSON float array
                                                  updated_at TIMESTAMP NOT NULL
);

CREATE INDEX IF NOT EXISTS document_embedding_model_idx ON document_embedding(model);