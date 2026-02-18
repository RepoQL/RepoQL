-- parse(text) - Parse structured text into table rows with dynamic columns
-- Supports JSON, JSONL, TSV, CSV, YAML, embedded data, and plain text fallback.
--
-- Examples:
--   SELECT * FROM parse('id,name
--   1,Alice
--   2,Bob')
--
--   SELECT * FROM parse('{"id":1,"name":"Alice"}
--   {"id":2,"name":"Bob"}')
--
-- Note: convert_to_json() performs format detection and normalization,
-- then read_json_auto infers the output table schema dynamically.

CREATE OR REPLACE MACRO parse(text) AS TABLE (
    SELECT * FROM read_json_auto(
        _write_temp_json(convert_to_json(text, 'true')),
        maximum_object_size := 67108864
    )
);
