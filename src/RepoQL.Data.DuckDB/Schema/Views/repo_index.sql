CREATE OR REPLACE VIEW repo_index AS
WITH document_rows AS (
    SELECT
        doc.id AS doc_id,
        doc.id AS node_id,
        COALESCE(doc.uri, 'repoql://document/' || CAST(doc.id AS VARCHAR)) AS uri,
        REPLACE(repository_uri_container(COALESCE(doc.uri, 'repoql://document/' || CAST(doc.id AS VARCHAR))), '\\', '/') AS path,
        LOWER(REPLACE(repository_uri_container(COALESCE(doc.uri, 'repoql://document/' || CAST(doc.id AS VARCHAR))), '\\', '/')) AS search_key,
        repository_uri_file_name(doc.uri) AS basename,
        regexp_extract(REPLACE(repository_uri_container(COALESCE(doc.uri, 'repoql://document/' || CAST(doc.id AS VARCHAR))), '\\', '/'), '^(.*)/[^/]*$', 1) AS dirname,
        media_type_kind(art.media_type) AS lang,
        media_type_base(art.media_type) AS mime,
        doc.kind,
        repository_uri_symbol(doc.uri) AS symbol,
        LOWER(COALESCE(repository_uri_symbol(doc.uri), '')) AS symbol_key,
        CAST(NULL AS INTEGER) AS line_start,
        CAST(NULL AS INTEGER) AS line_end,
        COALESCE(NULLIF(doc.headline, ''), NULLIF(art.headline, '')) AS headline,
        COALESCE(NULLIF(doc.structure, ''), NULLIF(art.structure, '')) AS structure,
        NULLIF(
            trim(
                concat_ws(
                    '\n\n',
                    NULLIF(doc.headline, ''),
                    NULLIF(doc.structure, ''),
                    NULLIF(art.summary, ''),
                    NULLIF(substr(art.text_content, 1, 4000), '')
                )
            ),
            ''
        ) AS body,
        'document' AS scope,
        de.embedding,
        doc.updated_at AS mtime,
        art.digest
    FROM node doc
             LEFT JOIN artifact art ON art.id = doc.artifact_id
             LEFT JOIN document_embedding de ON de.node_id = doc.id
    WHERE doc.kind = 'document'
),
object_rows AS (
    SELECT
        doc.id AS doc_id,
        child.id AS node_id,
        COALESCE(
            child.uri,
            repository_uri_join(
                COALESCE(doc.uri, 'repoql://document/' || CAST(doc.id AS VARCHAR)),
                COALESCE(
                    fragment_from_line_range(span.start_line, span.end_line),
                    concat('node/', child.kind, '/', REPLACE(CAST(child.id AS VARCHAR), '-', ''))
                )
            )
        ) AS uri,
        REPLACE(repository_uri_container(COALESCE(doc.uri, 'repoql://document/' || CAST(doc.id AS VARCHAR))), '\\', '/') AS path,
        LOWER(REPLACE(repository_uri_container(COALESCE(doc.uri, 'repoql://document/' || CAST(doc.id AS VARCHAR))), '\\', '/')) AS search_key,
        repository_uri_file_name(doc.uri) AS basename,
        regexp_extract(REPLACE(repository_uri_container(COALESCE(doc.uri, 'repoql://document/' || CAST(doc.id AS VARCHAR))), '\\', '/'), '^(.*)/[^/]*$', 1) AS dirname,
        media_type_kind(art.media_type) AS lang,
        media_type_base(art.media_type) AS mime,
        child.kind,
        COALESCE(
            repository_uri_symbol(child.uri),
            json_extract_string(child.properties, '$.symbol'),
            json_extract_string(child.properties, '$.name')
        ) AS symbol,
        LOWER(
            COALESCE(
                repository_uri_symbol(child.uri),
                json_extract_string(child.properties, '$.symbol'),
                json_extract_string(child.properties, '$.name'),
                ''
            )
        ) AS symbol_key,
        COALESCE(span.start_line, repository_uri_line_start(child.uri)) AS line_start,
        COALESCE(span.end_line, repository_uri_line_end(child.uri)) AS line_end,
        COALESCE(
            NULLIF(child.headline, ''),
            json_extract_string(child.properties, '$.name'),
            repository_uri_file_name(doc.uri)
        ) AS headline,
        NULLIF(child.structure, '') AS structure,
        NULLIF(
            trim(
                concat_ws(
                    '\n\n',
                    NULLIF(child.headline, ''),
                    NULLIF(child.structure, ''),
                    json_extract_string(child.properties, '$.summary'),
                    json_extract_string(child.properties, '$.docstring')
                )
            ),
            ''
        ) AS body,
        'object' AS scope,
        de.embedding,
        child.updated_at AS mtime,
        art.digest
    FROM node child
             JOIN span ON span.id = child.span_id
             JOIN node doc ON doc.id = span.document_id
             LEFT JOIN artifact art ON art.id = doc.artifact_id
             LEFT JOIN document_embedding de ON de.node_id = child.id
    WHERE child.kind <> 'document'
)
SELECT
    doc_id,
    node_id,
    uri,
    path,
    search_key,
    basename,
    dirname,
    lang,
    mime,
    kind,
    symbol,
    symbol_key,
    line_start,
    line_end,
    headline,
    structure,
    body,
    scope,
    embedding,
    mtime,
    digest
FROM document_rows
UNION ALL
SELECT
    doc_id,
    node_id,
    uri,
    path,
    search_key,
    basename,
    dirname,
    lang,
    mime,
    kind,
    symbol,
    symbol_key,
    line_start,
    line_end,
    headline,
    structure,
    body,
    scope,
    embedding,
    mtime,
    digest
FROM object_rows;
