-- parse(text) - Parse CSV/TSV text into table rows with dynamic columns
-- Columns are auto-detected from the header row
--
-- Examples:
--   SELECT * FROM parse('id,name
--   1,Alice
--   2,Bob')
--
--   SELECT * FROM parse('project,team,priority
--   RepoQL.Data.DuckDB,Platform,1
--   RepoQL.Indexing,Platform,2')
--
-- Note: Uses temp file + read_csv_auto for dynamic column detection.
-- For JSON data, use from_json() with json_structure() directly.

CREATE OR REPLACE MACRO parse(text) AS TABLE (
    SELECT * FROM read_csv_auto(_write_temp_csv(text), header := true)
);
