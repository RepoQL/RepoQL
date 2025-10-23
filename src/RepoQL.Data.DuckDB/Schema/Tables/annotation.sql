CREATE TABLE IF NOT EXISTS annotation (
                                          id                 UUID PRIMARY KEY,
                                          semantic_key       TEXT,
                                          kind               TEXT NOT NULL,
                                          severity           TEXT NOT NULL,
                                          source             TEXT NOT NULL,
                                          rule_id            TEXT,
                                          message            TEXT NOT NULL,
                                          data               JSON NOT NULL,
                                          scope_document_id  UUID NOT NULL,
                                          target_node_id     UUID,
                                          target_edge_id     UUID,
                                          target_span_id     UUID,
                                          target_uri         TEXT,
                                          created_at         TIMESTAMP NOT NULL,
                                          expires_at         TIMESTAMP,
                                          UNIQUE(semantic_key)
);

CREATE INDEX IF NOT EXISTS annotation_kind_index           ON annotation(kind);
CREATE INDEX IF NOT EXISTS annotation_severity_index       ON annotation(severity);
CREATE INDEX IF NOT EXISTS annotation_scope_document_id_index ON annotation(scope_document_id);
CREATE INDEX IF NOT EXISTS annotation_target_node_id_index ON annotation(target_node_id);
CREATE INDEX IF NOT EXISTS annotation_target_edge_id_index ON annotation(target_edge_id);
CREATE INDEX IF NOT EXISTS annotation_target_span_id_index ON annotation(target_span_id);

COMMENT ON TABLE annotation IS 'Out-of-band facts (lint, outline, metrics, hints)..';