-- ============================================================================
-- XLSX Data Access Macros
-- ============================================================================
-- These macros leverage DuckDB's native excel extension to query actual
-- spreadsheet data from indexed XLSX files.
--
-- Prerequisites: DuckDB excel extension (auto-loaded on first use)
-- ============================================================================


-- ----------------------------------------------------------------------------
-- xlsx: Read XLSX data using RepoQL URIs (recommended)
-- ----------------------------------------------------------------------------
-- Simple wrapper around read_xlsx() that accepts RepoQL URIs directly.
-- Automatically resolves file:/// URIs to physical paths.
--
-- Parameters:
--   uri         - RepoQL URI (e.g., 'file:///Examples/data.xlsx')
--   sheet       - Sheet name (default: first sheet)
--   header      - Use first row as header (default TRUE)
--   all_varchar - Read all columns as VARCHAR (default FALSE, set TRUE for messy data)
--
-- Examples:
--   SELECT * FROM xlsx('file:///Examples/data.xlsx');
--   SELECT * FROM xlsx('file:///Examples/data.xlsx', sheet := 'Sheet1');
--   SELECT * FROM xlsx('file:///Examples/data.xlsx', all_varchar := TRUE);
--
CREATE OR REPLACE MACRO xlsx(
    uri,
    sheet := NULL,
    header := TRUE,
    all_varchar := FALSE
) AS TABLE (
    SELECT * FROM read_xlsx(
        resolve_path(uri),
        sheet := sheet,
        header := header,
        all_varchar := all_varchar
    )
);


-- ----------------------------------------------------------------------------
-- xlsx_data: Read data from a single XLSX file
-- ----------------------------------------------------------------------------
-- Reads actual cell data from an indexed XLSX file.
--
-- Parameters:
--   file_uri    - RepoQL URI of the XLSX file (e.g., 'file:///path/to/file.xlsx')
--   sheet       - Sheet name or pattern (NULL = all sheets unioned)
--   header      - Use first row as header (default TRUE)
--   range       - Cell range to read (e.g., 'A1:D100', NULL = all data)
--
-- Returns: Table with spreadsheet data plus _source_file and _source_sheet columns
--
-- Examples:
--   SELECT * FROM xlsx_data('file:///expenses.xlsx');
--   SELECT * FROM xlsx_data('file:///expenses.xlsx', sheet := 'January');
--   SELECT * FROM xlsx_data('file:///expenses.xlsx', range := 'A1:D100');
--
CREATE OR REPLACE MACRO xlsx_data(
    file_uri,
    sheet := NULL,
    header := TRUE,
    range := NULL
) AS TABLE (
    WITH file_path AS (
        SELECT a.storage_uri AS path
        FROM node n
        JOIN artifact a ON a.id = n.artifact_id
        WHERE n.kind = 'document'
          AND (n.uri = file_uri OR n.container_uri_lowercase = LOWER(file_uri))
        LIMIT 1
    ),
    sheet_list AS (
        SELECT ws.name AS sheet_name
        FROM node n
        JOIN node ws ON ws.kind = 'xlsx_worksheet'
        JOIN edge e ON e.source_node_id = n.id AND e.destination_node_id = ws.id AND e.type = 'HAS_PART'
        WHERE n.kind = 'document'
          AND (n.uri = file_uri OR n.container_uri_lowercase = LOWER(file_uri))
          AND (sheet IS NULL OR ws.name = sheet OR ws.name LIKE REPLACE(REPLACE(sheet, '*', '%'), '?', '_'))
        ORDER BY e.ordinal
    )
    SELECT
        file_uri AS _source_file,
        (SELECT sheet_name FROM sheet_list LIMIT 1) AS _source_sheet,
        *
    FROM read_xlsx(
        (SELECT path FROM file_path),
        sheet := COALESCE(sheet, (SELECT sheet_name FROM sheet_list LIMIT 1)),
        header := header,
        range := range
    )
);


-- ----------------------------------------------------------------------------
-- xlsx_sheets: List all sheets in an XLSX file with metadata
-- ----------------------------------------------------------------------------
-- Returns worksheet information from an indexed XLSX file.
--
-- Parameters:
--   file_uri    - RepoQL URI of the XLSX file
--
-- Returns: sheet_name, row_count, column_count, has_header, headline
--
-- Example:
--   SELECT * FROM xlsx_sheets('file:///expenses.xlsx');
--
CREATE OR REPLACE MACRO xlsx_sheets(file_uri) AS TABLE (
    SELECT
        json_extract_string(ws.properties, '$.name') AS sheet_name,
        json_extract(ws.properties, '$.index')::INTEGER AS sheet_index,
        json_extract(ws.properties, '$.row_count')::INTEGER AS row_count,
        json_extract(ws.properties, '$.column_count')::INTEGER AS column_count,
        json_extract(ws.properties, '$.has_header_row')::BOOLEAN AS has_header,
        json_extract(ws.properties, '$.has_totals')::BOOLEAN AS has_totals,
        ws.headline
    FROM node n
    JOIN node ws ON ws.kind = 'xlsx_worksheet'
    JOIN edge e ON e.source_node_id = n.id AND e.destination_node_id = ws.id AND e.type = 'HAS_PART'
    WHERE n.kind = 'document'
      AND (n.uri = file_uri OR n.container_uri_lowercase = LOWER(file_uri))
    ORDER BY e.ordinal
);


-- ----------------------------------------------------------------------------
-- xlsx_preview: Quick preview of XLSX data (first N rows)
-- ----------------------------------------------------------------------------
-- Fast preview without loading entire file.
--
-- Parameters:
--   file_uri    - RepoQL URI of the XLSX file
--   rows        - Number of rows to preview (default 10)
--   sheet       - Sheet name (NULL = first sheet)
--
-- Example:
--   SELECT * FROM xlsx_preview('file:///expenses.xlsx', 20);
--
CREATE OR REPLACE MACRO xlsx_preview(
    file_uri,
    rows := 10,
    sheet := NULL
) AS TABLE (
    WITH file_info AS (
        SELECT
            a.storage_uri AS path,
            (
                SELECT json_extract_string(ws.properties, '$.name')
                FROM node ws
                JOIN edge e ON e.source_node_id = n.id AND e.destination_node_id = ws.id AND e.type = 'HAS_PART'
                WHERE ws.kind = 'xlsx_worksheet'
                  AND (sheet IS NULL OR json_extract_string(ws.properties, '$.name') = sheet)
                ORDER BY e.ordinal
                LIMIT 1
            ) AS first_sheet
        FROM node n
        JOIN artifact a ON a.id = n.artifact_id
        WHERE n.kind = 'document'
          AND (n.uri = file_uri OR n.container_uri_lowercase = LOWER(file_uri))
        LIMIT 1
    )
    SELECT
        file_uri AS _source_file,
        (SELECT first_sheet FROM file_info) AS _source_sheet,
        *
    FROM read_xlsx(
        (SELECT path FROM file_info),
        sheet := COALESCE(sheet, (SELECT first_sheet FROM file_info)),
        header := TRUE
    )
    LIMIT rows
);


-- ----------------------------------------------------------------------------
-- xlsx_schema: Show detected schema for a worksheet
-- ----------------------------------------------------------------------------
-- Returns column information including detected types from indexing.
--
-- Parameters:
--   file_uri    - RepoQL URI of the XLSX file
--   sheet       - Sheet name (NULL = first sheet)
--
-- Example:
--   SELECT * FROM xlsx_schema('file:///expenses.xlsx');
--
CREATE OR REPLACE MACRO xlsx_schema(
    file_uri,
    sheet := NULL
) AS TABLE (
    WITH target_sheet AS (
        SELECT ws.id AS ws_id, ws.properties
        FROM node n
        JOIN node ws ON ws.kind = 'xlsx_worksheet'
        JOIN edge e ON e.source_node_id = n.id AND e.destination_node_id = ws.id AND e.type = 'HAS_PART'
        WHERE n.kind = 'document'
          AND (n.uri = file_uri OR n.container_uri_lowercase = LOWER(file_uri))
          AND (sheet IS NULL OR json_extract_string(ws.properties, '$.name') = sheet)
        ORDER BY e.ordinal
        LIMIT 1
    ),
    column_types AS (
        SELECT
            json_extract_string(ts.properties, '$.name') AS sheet_name,
            json_extract(ts.properties, '$.column_types') AS col_types
        FROM target_sheet ts
    )
    SELECT
        ct.sheet_name,
        key AS column_letter,
        value AS detected_type
    FROM column_types ct,
         LATERAL (SELECT * FROM json_each(ct.col_types)) AS cols(key, value)
    ORDER BY column_letter
);


-- ----------------------------------------------------------------------------
-- xlsx_files: List all indexed XLSX files
-- ----------------------------------------------------------------------------
-- Returns all XLSX files with summary information.
--
-- Parameters:
--   pattern     - Optional glob pattern to filter files (default: all xlsx files)
--
-- Example:
--   SELECT * FROM xlsx_files();
--   SELECT * FROM xlsx_files('**/*expense*');
--   SELECT * FROM xlsx_files() WHERE headline LIKE '%expense%';
--
CREATE OR REPLACE MACRO xlsx_files(pattern := NULL) AS TABLE (
    SELECT
        n.uri,
        a.storage_uri AS file_path,
        json_extract(n.properties, '$.sheet_count')::INTEGER AS sheet_count,
        json_extract(n.properties, '$.total_rows')::INTEGER AS total_rows,
        json_extract(n.properties, '$.table_count')::INTEGER AS table_count,
        json_extract(n.properties, '$.has_formulas')::BOOLEAN AS has_formulas,
        json_extract(n.properties, '$.has_totals')::BOOLEAN AS has_totals,
        a.headline,
        a.byte_size
    FROM node n
    JOIN artifact a ON a.id = n.artifact_id
    WHERE n.kind = 'document'
      AND a.media_type LIKE '%xlsx%'
      AND (pattern IS NULL OR matches_glob(n.uri, pattern))
    ORDER BY n.uri
);


-- ----------------------------------------------------------------------------
-- xlsx_union: Union data from multiple XLSX files
-- ----------------------------------------------------------------------------
-- Combines data from multiple XLSX files matching a pattern into a single table.
-- Critical for tax synthesis: "give me all expenses from all spreadsheets"
--
-- Parameters:
--   pattern     - Glob pattern to match files (e.g., '**/*expense*.xlsx')
--   sheet       - Sheet name filter (NULL = first sheet from each file)
--   header      - Use first row as header (default TRUE)
--
-- Returns: Combined data with _source_file and _source_sheet columns
--
-- Examples:
--   SELECT * FROM xlsx_union('**/*expense*.xlsx');
--   SELECT * FROM xlsx_union('**/*.xlsx', sheet := 'Summary');
--   SELECT SUM(Amount) FROM xlsx_union('**/2024*.xlsx') WHERE Category = 'Office';
--
-- Note: All files should have compatible schemas. Columns are matched by position.
-- Use xlsx_schema() first to verify column compatibility.
--
CREATE OR REPLACE MACRO xlsx_union(
    pattern,
    sheet := NULL,
    header := TRUE
) AS TABLE (
    WITH matched_files AS (
        SELECT
            n.uri,
            a.storage_uri AS file_path,
            (
                SELECT json_extract_string(ws.properties, '$.name')
                FROM node ws
                JOIN edge e ON e.source_node_id = n.id AND e.destination_node_id = ws.id AND e.type = 'HAS_PART'
                WHERE ws.kind = 'xlsx_worksheet'
                  AND (sheet IS NULL OR json_extract_string(ws.properties, '$.name') = sheet)
                ORDER BY e.ordinal
                LIMIT 1
            ) AS sheet_name
        FROM node n
        JOIN artifact a ON a.id = n.artifact_id
        WHERE n.kind = 'document'
          AND a.media_type LIKE '%xlsx%'
          AND matches_glob(n.uri, pattern, TRUE, 'file:///')
    )
    SELECT
        mf.uri AS _source_file,
        mf.sheet_name AS _source_sheet,
        data.*
    FROM matched_files mf,
         LATERAL (
             SELECT * FROM read_xlsx(mf.file_path, sheet := mf.sheet_name, header := header)
         ) AS data
);


-- ----------------------------------------------------------------------------
-- xlsx_find_amounts: Find columns that look like financial amounts
-- ----------------------------------------------------------------------------
-- Searches across all XLSX files for columns that appear to contain amounts.
-- Useful for discovering financial data in messy spreadsheets.
--
-- Parameters:
--   pattern     - Glob pattern to match files (default: all xlsx)
--   column_hint - Regex pattern for column names (default: amount|total|sum|price|cost)
--
-- Returns: file_uri, sheet_name, column_name, row_count, detected_type
--
-- Example:
--   SELECT * FROM xlsx_find_amounts();
--   SELECT * FROM xlsx_find_amounts('**/2024*.xlsx');
--
CREATE OR REPLACE MACRO xlsx_find_amounts(
    pattern := NULL,
    column_hint := '(?i)(amount|total|sum|price|cost|value|revenue|expense)'
) AS TABLE (
    SELECT
        n.uri AS file_uri,
        json_extract_string(ws.properties, '$.name') AS sheet_name,
        cols.key AS column_letter,
        cols.value AS detected_type,
        json_extract(ws.properties, '$.row_count')::INTEGER AS row_count
    FROM node n
    JOIN artifact a ON a.id = n.artifact_id
    JOIN node ws ON ws.kind = 'xlsx_worksheet'
    JOIN edge e ON e.source_node_id = n.id AND e.destination_node_id = ws.id AND e.type = 'HAS_PART'
    CROSS JOIN LATERAL (
        SELECT * FROM json_each(json_extract(ws.properties, '$.column_types'))
    ) AS cols(key, value)
    WHERE n.kind = 'document'
      AND a.media_type LIKE '%xlsx%'
      AND (pattern IS NULL OR matches_glob(n.uri, pattern))
      AND (cols.value = 'numeric' OR cols.value = 'currency')
    ORDER BY n.uri, json_extract(ws.properties, '$.name'), cols.key
);


-- ----------------------------------------------------------------------------
-- xlsx_summary: Financial summary across multiple files
-- ----------------------------------------------------------------------------
-- Aggregates key metrics from XLSX files - useful for tax prep overview.
--
-- Parameters:
--   pattern     - Glob pattern to match files (default: all xlsx)
--
-- Returns: Overview of all matched files with row counts and totals presence
--
-- Example:
--   SELECT * FROM xlsx_summary('**/2024*.xlsx');
--
CREATE OR REPLACE MACRO xlsx_summary(pattern := NULL) AS TABLE (
    SELECT
        n.uri AS file_uri,
        json_extract(n.properties, '$.sheet_count')::INTEGER AS sheets,
        json_extract(n.properties, '$.total_rows')::INTEGER AS total_rows,
        json_extract(n.properties, '$.table_count')::INTEGER AS tables,
        json_extract(n.properties, '$.has_formulas')::BOOLEAN AS has_formulas,
        json_extract(n.properties, '$.has_totals')::BOOLEAN AS has_totals,
        a.headline,
        (
            SELECT string_agg(json_extract_string(ws.properties, '$.name'), ', ' ORDER BY e.ordinal)
            FROM node ws
            JOIN edge e ON e.source_node_id = n.id AND e.destination_node_id = ws.id AND e.type = 'HAS_PART'
            WHERE ws.kind = 'xlsx_worksheet'
        ) AS sheet_names
    FROM node n
    JOIN artifact a ON a.id = n.artifact_id
    WHERE n.kind = 'document'
      AND a.media_type LIKE '%xlsx%'
      AND (pattern IS NULL OR matches_glob(n.uri, pattern))
    ORDER BY n.uri
);
