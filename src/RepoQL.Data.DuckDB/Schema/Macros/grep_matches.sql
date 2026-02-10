-- Returns line-level case-insensitive literal text matches across indexed files.
-- Parameters:
--   pattern: Required literal text to search for
--   scope: Optional URI glob to filter documents (file:///src/**/*.cs)
-- Returns: uri, line_number, line_content
-- Examples:
--   SELECT * FROM grep_matches('validateToken');
--   SELECT * FROM grep_matches('DuckDb', 'file:///src/**/*.cs');
CREATE OR REPLACE MACRO grep_matches(pattern, scope := NULL) AS TABLE (
    WITH documents AS (
        SELECT n.uri, a.text_content
        FROM node n
        JOIN artifact a ON n.artifact_id = a.id
        WHERE n.kind = 'document'
          AND a.text_content IS NOT NULL
          AND (scope IS NULL OR matches_glob(n.uri, scope))
    ),
    lines AS (
        SELECT
            d.uri,
            ord::INTEGER AS line_number,
            TRIM(TRAILING CHR(13) FROM value) AS line_content
        FROM documents d
        CROSS JOIN UNNEST(string_split(d.text_content, CHR(10))) WITH ORDINALITY AS t(value, ord)
    )
    SELECT uri, line_number, line_content
    FROM lines
    WHERE pattern IS NOT NULL
      AND line_content ILIKE '%' || pattern || '%'
    ORDER BY uri, line_number
);
