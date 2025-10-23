CREATE TABLE IF NOT EXISTS artifact (
                                        id           UUID PRIMARY KEY,
                                        digest       VARCHAR NOT NULL UNIQUE,
                                        byte_size    BIGINT NOT NULL,
                                        media_type   VARCHAR,
                                        text_content VARCHAR,
                                        storage_uri  VARCHAR,
                                        headline     VARCHAR,
                                        summary      VARCHAR,
                                        structure    VARCHAR
);
COMMENT ON TABLE artifact IS 'Content-addressed artifact bytes and optional decoded text.';
COMMENT ON COLUMN artifact.id IS 'Artifact identifier (GUID).';
COMMENT ON COLUMN artifact.digest IS 'Content digest (e.g., sha256:...).';
COMMENT ON COLUMN artifact.byte_size IS 'Uncompressed size in bytes.';
COMMENT ON COLUMN artifact.media_type IS 'Semantic media type string with parameters.';
COMMENT ON COLUMN artifact.text_content IS 'Optional decoded text for search and span mapping.';
COMMENT ON COLUMN artifact.storage_uri IS 'External storage location for raw bytes (file/object store).';
COMMENT ON COLUMN artifact.headline IS 'X-ray Level 0 (headline): essential identity (single line), always present for documents.';
COMMENT ON COLUMN artifact.summary IS 'X-ray Level 1 (summary): key information (~5 lines, max 10) for understanding without reading full content.';
COMMENT ON COLUMN artifact.structure IS 'X-ray Level 2 (structure): detailed outline (~15 lines, max 25) for navigation and exploration.';