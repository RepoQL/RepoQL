CREATE TABLE IF NOT EXISTS node (
                                    id                          UUID PRIMARY KEY,
                                    kind                        VARCHAR NOT NULL,
                                    uri                         VARCHAR,
                                    container_uri_lowercase     VARCHAR,
                                    artifact_id                 UUID,
                                    span_id                     UUID,
                                    properties                  JSON NOT NULL,
                                    headline                    VARCHAR,
                                    structure                   VARCHAR,
                                    created_at                  TIMESTAMP NOT NULL,
                                    updated_at                  TIMESTAMP NOT NULL,
                                    CHECK (kind <> 'document' OR uri IS NOT NULL),
                                    FOREIGN KEY (artifact_id) REFERENCES artifact(id)
);

CREATE UNIQUE INDEX IF NOT EXISTS node_container_uri_lowercase_unique ON node(container_uri_lowercase);
CREATE INDEX IF NOT EXISTS node_kind_idx ON node(kind);

COMMENT ON TABLE node IS 'Property-graph vertex: documents, sections, symbols, etc.';
COMMENT ON COLUMN node.id IS 'Node identifier (GUID).';
COMMENT ON COLUMN node.kind IS 'Open taxonomy label (e.g., document, md_section, cs_class).';
COMMENT ON COLUMN node.uri IS 'Repository-aware container URI for documents (no fragment).';
COMMENT ON COLUMN node.container_uri_lowercase IS 'Lowercase container URI for uniqueness.';
COMMENT ON COLUMN node.artifact_id IS 'Back-reference to artifact providing bytes.';
COMMENT ON COLUMN node.span_id IS 'Span that locates this node within a document.';
COMMENT ON COLUMN node.properties IS 'Arbitrary attributes as JSON.';
COMMENT ON COLUMN node.headline IS 'X-ray summary (Level 0) for this node, if available.';
COMMENT ON COLUMN node.structure IS 'X-ray outline (Level 2) for this node, if available.';
COMMENT ON COLUMN node.created_at IS 'Creation timestamp (UTC).';
COMMENT ON COLUMN node.updated_at IS 'Update timestamp (UTC).';
