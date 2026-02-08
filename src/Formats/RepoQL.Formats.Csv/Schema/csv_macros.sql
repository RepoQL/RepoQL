-- ============================================================================
-- CSV/TSV/PSV Data Access Macros
-- ============================================================================
-- These macros leverage DuckDB's native read_csv_auto() over indexed delimited
-- files and expose schema metadata captured during indexing.
--
-- NOTE: read_csv_auto() is a DuckDB built-in that requires literal-like
-- parameters. Column references via LATERAL joins are rejected at macro
-- creation time. Single-file macros pass resolve_path(file_uri) directly
-- (derived from macro parameter = treated as literal). Multi-file iteration
-- requires a UDF wrapper — see csv_union comment below.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- csv_schema: Show detected column schema from indexed metadata
-- ----------------------------------------------------------------------------
-- Parameters:
--   file_uri    - RepoQL URI of the file
--
-- Returns: column index/name/type with token estimates and examples
CREATE OR REPLACE MACRO csv_schema(file_uri) AS TABLE (
    SELECT
        json_extract(col.properties, '$.index')::INTEGER AS column_index,
        json_extract_string(col.properties, '$.name') AS column_name,
        json_extract_string(col.properties, '$.type') AS detected_type,
        json_extract(col.properties, '$.estimated_tokens')::BIGINT AS estimated_tokens,
        json_extract_string(col.properties, '$.min_value') AS min_value,
        json_extract_string(col.properties, '$.max_value') AS max_value,
        json_extract(col.properties, '$.sample_values') AS sample_values
    FROM node n
    JOIN node col ON col.kind = 'csv_column'
    JOIN edge e ON e.source_node_id = n.id AND e.destination_node_id = col.id AND e.type = 'HAS_PART'
    WHERE n.kind = 'document'
      AND (n.uri = file_uri OR n.container_uri_lowercase = LOWER(file_uri))
    ORDER BY e.ordinal, column_index
);


-- ----------------------------------------------------------------------------
-- csv_files: List all indexed CSV/TSV/PSV files
-- ----------------------------------------------------------------------------
-- Parameters:
--   pattern     - Optional glob pattern to filter file URIs
CREATE OR REPLACE MACRO csv_files(pattern := NULL) AS TABLE (
    SELECT
        n.uri,
        a.storage_uri AS file_path,
        json_extract_string(n.properties, '$.delimiter') AS delimiter,
        json_extract(n.properties, '$.row_count')::INTEGER AS row_count,
        json_extract(n.properties, '$.column_count')::INTEGER AS column_count,
        json_extract(n.properties, '$.has_header')::BOOLEAN AS has_header,
        a.media_type,
        a.headline,
        a.byte_size
    FROM node n
    JOIN artifact a ON a.id = n.artifact_id
    WHERE n.kind = 'document'
      AND (
          a.media_type LIKE '%csv%'
          OR a.media_type LIKE '%tab-separated%'
          OR a.media_type LIKE '%data.psv%'
      )
      AND (pattern IS NULL OR matches_glob(n.uri, pattern))
    ORDER BY n.uri
);


-- ----------------------------------------------------------------------------
-- csv: Read delimited data using RepoQL URIs (recommended)
-- ----------------------------------------------------------------------------
-- Parameters:
--   uri         - RepoQL URI (e.g., 'file:///Examples/data.csv')
--   delimiter   - Delimiter character (default ',')
--   header      - Use first row as header (default TRUE)
--   all_varchar - Read all columns as VARCHAR (default FALSE)
CREATE OR REPLACE MACRO csv(
    uri,
    delimiter := ',',
    header := TRUE,
    all_varchar := FALSE
) AS TABLE (
    SELECT * FROM read_csv_auto(
        resolve_path(uri),
        delim := delimiter,
        header := header,
        all_varchar := all_varchar,
        strict_mode := FALSE
    )
);


-- ----------------------------------------------------------------------------
-- csv_data: Read data from a single indexed delimited file
-- ----------------------------------------------------------------------------
-- Uses resolve_path(file_uri) directly — read_csv_auto auto-detects the
-- delimiter, so we don't need to look it up from the graph.
--
-- Parameters:
--   file_uri    - RepoQL URI of the file
--   header      - Override header behavior (default TRUE)
--
-- Returns: table rows with _source_file column
CREATE OR REPLACE MACRO csv_data(
    file_uri,
    header := TRUE
) AS TABLE (
    SELECT
        file_uri AS _source_file,
        data.*
    FROM read_csv_auto(
        resolve_path(file_uri),
        header := header,
        strict_mode := FALSE
    ) AS data
);


-- ----------------------------------------------------------------------------
-- csv_preview: Preview first N rows from one indexed file
-- ----------------------------------------------------------------------------
-- Uses resolve_path(file_uri) directly with auto-detection.
--
-- Parameters:
--   file_uri    - RepoQL URI of the file
--   rows        - Number of rows to return (default 10)
CREATE OR REPLACE MACRO csv_preview(
    file_uri,
    rows := 10
) AS TABLE (
    SELECT
        file_uri AS _source_file,
        data.*
    FROM read_csv_auto(
        resolve_path(file_uri),
        strict_mode := FALSE
    ) AS data
    LIMIT rows
);


-- ----------------------------------------------------------------------------
-- csv_union: Union rows from all matching indexed delimited files
-- ----------------------------------------------------------------------------
-- NOTE: read_csv_auto() rejects column references as parameters, so LATERAL
-- iteration over graph-resolved paths is not possible in a pure SQL macro.
-- Unlike read_xlsx (a RepoQL UDF), read_csv_auto is a DuckDB built-in with
-- this restriction. Use csv_data() on individual files, or read_csv_auto()
-- with file system glob patterns directly:
--
--   SELECT * FROM read_csv_auto('path/to/**/*.csv', union_by_name := true, filename := true)
--
-- A proper csv_union() requires a UDF wrapper around read_csv_auto that
-- accepts column references. This is tracked for future implementation.
