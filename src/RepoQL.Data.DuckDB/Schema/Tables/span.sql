CREATE TABLE IF NOT EXISTS span (
                                    id            UUID PRIMARY KEY,
                                    document_id   UUID NOT NULL,
                                    start_byte    BIGINT,
                                    end_byte      BIGINT,
                                    start_line    INTEGER,
                                    start_column  INTEGER,
                                    end_line      INTEGER,
                                    end_column    INTEGER
    -- FK constraint removed: See edge table comment
);

COMMENT ON TABLE span IS 'Text/byte extent within a single document node.';
COMMENT ON COLUMN span.id IS 'Span identifier (GUID).';
COMMENT ON COLUMN span.document_id IS 'Owning document node id.';
COMMENT ON COLUMN span.start_byte IS '0-based start byte offset (inclusive).';
COMMENT ON COLUMN span.end_byte IS '0-based end byte offset (exclusive).';
COMMENT ON COLUMN span.start_line IS '1-based start line.';
COMMENT ON COLUMN span.start_column IS '1-based start column.';
COMMENT ON COLUMN span.end_line IS '1-based end line.';
COMMENT ON COLUMN span.end_column IS '1-based end column.';