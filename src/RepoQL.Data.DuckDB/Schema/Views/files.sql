CREATE OR REPLACE VIEW files AS
WITH git AS (
    SELECT uri, index_status, work_tree_status, category
    FROM git_status()
)
SELECT
    -- Identity
    doc.uri,
    CASE
        WHEN doc.uri LIKE 'github://%'
        THEN regexp_extract(doc.uri, '^(github://[^/]+/[^/]+)', 1)
        ELSE split_part(doc.uri, '://', 1) || '://'
    END AS source,
    regexp_replace(repository_uri_container(doc.uri), '^[a-z]+://', '') AS path,
    regexp_extract(
        regexp_replace(repository_uri_container(doc.uri), '^[a-z]+://', ''),
        '^(.*)/[^/]*$', 1
    ) AS dirname,
    repository_uri_file_name(doc.uri) AS name,
    NULLIF(regexp_extract(repository_uri_file_name(doc.uri), '(\.[^.]+)$', 1), '') AS extension,

    -- Type info
    media_type_kind(art.media_type) AS lang,
    media_type_base(art.media_type) AS mime,

    -- Size
    art.byte_size,
    CASE
        WHEN art.text_content IS NOT NULL
        THEN len(string_split(art.text_content, chr(10)))
        ELSE NULL
    END AS lines,

    -- X-ray summaries
    COALESCE(doc.headline, art.headline) AS headline,
    art.summary,
    COALESCE(doc.structure, art.structure) AS structure,

    -- Timestamps
    doc.updated_at AS mtime,

    -- Diagnostics
    COALESCE(ann.error_count, 0) AS error_count,
    COALESCE(ann.warning_count, 0) AS warning_count,

    -- Git status (NULL for non-file:// URIs or unchanged files)
    git.category AS git_status,

    -- Join keys
    doc.id AS node_id,
    doc.artifact_id
FROM node doc
LEFT JOIN artifact art ON art.id = doc.artifact_id
LEFT JOIN (
    SELECT
        scope_document_id,
        COUNT(*) FILTER (WHERE severity = 'error') AS error_count,
        COUNT(*) FILTER (WHERE severity = 'warning') AS warning_count
    FROM annotation
    GROUP BY scope_document_id
) ann ON ann.scope_document_id = doc.id
LEFT JOIN git ON git.uri = doc.uri
WHERE doc.kind = 'document';
