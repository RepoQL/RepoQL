CREATE OR REPLACE MACRO json_extract_string_array(j, path) AS (
  string_split(
    REPLACE(
      REGEXP_REPLACE(
        REGEXP_REPLACE(CAST(j AS VARCHAR), '^.*""tags""\s*:\s*\[\s*', ''),
        '\s*\].*$', ''
      ),
      '""',
      ''
    ),
    ','
  )
);