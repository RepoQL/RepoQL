CREATE OR REPLACE MACRO node_primary_fragment(kind, properties_json, start_line, end_line, start_byte, end_byte) AS (
  CASE
    WHEN start_line IS NOT NULL OR end_line IS NOT NULL THEN
      CASE
        WHEN end_line IS NULL THEN 'line=' || CAST(start_line AS VARCHAR)
        WHEN start_line IS NULL THEN 'line=,' || CAST(end_line AS VARCHAR)
        ELSE 'line=' || CAST(start_line AS VARCHAR) || ',' || CAST(end_line AS VARCHAR)
      END
    WHEN start_byte IS NOT NULL OR end_byte IS NOT NULL THEN
      CASE
        WHEN end_byte IS NULL THEN 'char=' || CAST(start_byte AS VARCHAR)
        WHEN start_byte IS NULL THEN 'char=,' || CAST(end_byte AS VARCHAR)
        ELSE 'char=' || CAST(start_byte AS VARCHAR) || ',' || CAST(end_byte AS VARCHAR)
      END
    ELSE NULL
  END
);