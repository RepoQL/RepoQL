CREATE TABLE IF NOT EXISTS document_search (
                                               doc_id    UUID PRIMARY KEY,
                                               uri       VARCHAR NOT NULL,
                                               search_key VARCHAR NOT NULL,
                                               basename  VARCHAR,
                                               dirname   VARCHAR
);

CREATE UNIQUE INDEX IF NOT EXISTS document_search_uri_idx ON document_search(uri);
CREATE INDEX IF NOT EXISTS document_search_search_idx ON document_search(search_key);
CREATE INDEX IF NOT EXISTS document_search_basename_idx ON document_search(basename);
CREATE INDEX IF NOT EXISTS document_search_dirname_idx ON document_search(dirname);

CREATE OR REPLACE MACRO zero_one(x) AS (
  CASE WHEN MAX(x) OVER () IS NULL OR MAX(x) OVER () = 0 THEN 0 ELSE COALESCE(x,0) / NULLIF(MAX(x) OVER (),0) END
);

CREATE OR REPLACE MACRO combine(bm25n, fuzzn, semn, wb := 0.45, wf := 0.45, ws := 0.10) AS (
  coalesce(wb * bm25n, 0) + coalesce(wf * fuzzn, 0) + coalesce(ws * semn, 0)
);
