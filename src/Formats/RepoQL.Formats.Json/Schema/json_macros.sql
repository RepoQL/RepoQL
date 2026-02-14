-- Inventory: list all indexed JSON files with metadata
CREATE OR REPLACE MACRO json_files(pattern := NULL) AS TABLE (
    SELECT
        n.uri,
        a.headline,
        a.media_type,
        json_extract_string(n.properties, '$.shape') AS shape,
        json_extract(n.properties, '$.key_count')::INTEGER AS key_count,
        json_extract(n.properties, '$.max_depth')::INTEGER AS max_depth,
        a.byte_size,
        a.token_count
    FROM node n
    JOIN artifact a ON a.id = n.artifact_id
    WHERE n.kind = 'document'
      AND a.media_type LIKE 'application/json%'
      AND (pattern IS NULL OR matches_glob(n.uri, pattern))
    ORDER BY n.uri
);

-- Key structure: query keys across all JSON files
CREATE OR REPLACE MACRO json_keys(file_pattern := NULL, key_pattern := NULL) AS TABLE (
    SELECT
        doc.uri AS file_uri,
        key_node.uri AS key_uri,
        json_extract_string(key_node.properties, '$.path') AS path,
        json_extract_string(key_node.properties, '$.name') AS name,
        json_extract(key_node.properties, '$.depth')::INTEGER AS depth,
        json_extract_string(key_node.properties, '$.value_kind') AS value_kind,
        json_extract_string(key_node.properties, '$.scalar_value') AS value,
        json_extract(key_node.properties, '$.estimated_tokens')::INTEGER AS estimated_tokens,
        s.start_line,
        s.end_line
    FROM node doc
    JOIN edge e ON e.source_node_id = doc.id AND e.type = 'HAS_PART'
    JOIN node key_node ON key_node.id = e.destination_node_id AND key_node.kind = 'json_key'
    LEFT JOIN span s ON s.id = key_node.span_id
    WHERE doc.kind = 'document'
      AND (file_pattern IS NULL OR matches_glob(doc.uri, file_pattern))
      AND (key_pattern IS NULL OR json_extract_string(key_node.properties, '$.path') LIKE key_pattern)
    ORDER BY doc.uri, e.ordinal
);

-- Query-time data access for JSON data files
CREATE OR REPLACE MACRO json_data(uri) AS TABLE (
    SELECT * FROM read_json_auto(resolve_path(uri), maximum_object_size := 67108864)
);

-- Preview first N items from a JSON data file
CREATE OR REPLACE MACRO json_preview(uri, rows := 10) AS TABLE (
    SELECT * FROM read_json_auto(resolve_path(uri), maximum_object_size := 67108864) LIMIT rows
);
