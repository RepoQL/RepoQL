CREATE TABLE IF NOT EXISTS edge (
                                    id                     UUID PRIMARY KEY,
                                    source_node_id         UUID NOT NULL,
                                    destination_node_id    UUID NOT NULL,
                                    type                   VARCHAR NOT NULL,
                                    is_composition         BOOLEAN NOT NULL,
                                    ordinal                INTEGER,
                                    scope_document_id      UUID,
                                    semantic_key           VARCHAR,
                                    source_span_id         UUID,
                                    destination_span_id    UUID,
                                    composition_child_id   UUID,
                                    properties             JSON NOT NULL,
                                    created_at             TIMESTAMP NOT NULL
    -- FK constraints removed: DuckDB checks constraints immediately, even in transactions
    -- This prevents deletion of composition trees. Referential integrity is maintained
    -- at the application level instead.
);

CREATE UNIQUE INDEX IF NOT EXISTS edge_semantic_key_unique ON edge(semantic_key);
CREATE UNIQUE INDEX IF NOT EXISTS edge_composition_single_parent ON edge(composition_child_id);
CREATE INDEX IF NOT EXISTS edge_source_idx      ON edge(source_node_id);
CREATE INDEX IF NOT EXISTS edge_destination_idx ON edge(destination_node_id);
CREATE INDEX IF NOT EXISTS edge_type_idx         ON edge(type);
CREATE INDEX IF NOT EXISTS edge_scope_idx        ON edge(scope_document_id);

COMMENT ON TABLE edge IS 'Directed relationship between nodes with optional spans and attributes.';
COMMENT ON COLUMN edge.id IS 'Edge identifier (GUID).';
COMMENT ON COLUMN edge.source_node_id IS 'Source node id.';
COMMENT ON COLUMN edge.destination_node_id IS 'Destination node id.';
COMMENT ON COLUMN edge.type IS 'Relation type (e.g., HAS_PART, REFERS_TO, CALLS).';
COMMENT ON COLUMN edge.is_composition IS 'True when expressing containment/ownership.';
COMMENT ON COLUMN edge.ordinal IS 'Stable order among composition siblings.';
COMMENT ON COLUMN edge.scope_document_id IS 'Document that scoped or produced this relation.';
COMMENT ON COLUMN edge.semantic_key IS 'Optional business key for idempotent upserts.';
COMMENT ON COLUMN edge.source_span_id IS 'Span at origin site (e.g., link text or call site).';
COMMENT ON COLUMN edge.destination_span_id IS 'Span that the relation points to.';
COMMENT ON COLUMN edge.composition_child_id IS 'Destination when is_composition=true; enforces single parent.';
COMMENT ON COLUMN edge.properties IS 'Relation attributes as JSON.';
COMMENT ON COLUMN edge.created_at IS 'Creation timestamp (UTC).';