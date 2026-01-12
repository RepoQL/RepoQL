-- parse(text) - Parse structured data (JSON/JSONL/CSV/TSV/YAML) into table rows
-- Each row contains a JSON object in the 'value' column
-- Use json_extract_string(value, '$.field') to access fields
--
-- Examples:
--   SELECT * FROM parse('id,name\n1,Alice\n2,Bob')
--   SELECT json_extract_string(value, '$.name') AS name FROM parse('{"name":"test"}')

CREATE OR REPLACE MACRO parse(text) AS TABLE (
    WITH parsed AS (
        SELECT parse_structured(text) AS json_data
    ),
    normalized AS (
        SELECT
            CASE
                WHEN json_type(json_data::JSON) = 'ARRAY' THEN json_data
                WHEN json_data IS NULL OR json_data = 'null' THEN '[]'
                ELSE '[' || json_data || ']'
            END AS json_array
        FROM parsed
    )
    SELECT unnest(from_json(json_array, '["json"]')) AS value
    FROM normalized
    WHERE json_array != '[]'
);
