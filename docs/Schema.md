---
description: Authoritative documentation of RepoQL's core schema, UDFs, macros, and query patterns.
documentationCategory: comprehensive
tags: [repoql, schema, duckdb, sql, nodes, edges, spans, annotations, macros, udf]
---

# RepoGraph Core Schema

## Goals

* Model any repository file as a **document**, with contained **items** and **relations**.
* Resolve **URI → entity** fast and build **snippets** cheaply.
* Support **X‑ray** summaries, lint, outlines, and tool hints via **annotations**.
* Keep SQL and names obvious for LLMs. No unnecessary abbreviations.

---

## Entities

### `artifact`

Content bytes, optional decoded text, and x‑ray summaries.

```sql
CREATE TABLE IF NOT EXISTS artifact (
  id           UUID PRIMARY KEY,
  digest       TEXT NOT NULL UNIQUE,                  -- e.g., 'sha256:…'
  byte_size    BIGINT NOT NULL,
  media_type   TEXT,                                  -- 'type/subtype;key=value…'
  text_content TEXT,                                  -- optional decoded text
  storage_uri  TEXT,                                  -- file/object storage location
  headline     TEXT,                                  -- Level 0 (headline): single-line essence
  summary      TEXT,                                  -- Level 1 (summary): ~5 lines, max 10
  structure    TEXT                                   -- Level 2 (structure): ~15 lines, max 25
);

COMMENT ON TABLE artifact IS 'Content-addressed artifact bytes, optional decoded text, and x-ray summaries (headline, summary, structure).';
COMMENT ON COLUMN artifact.media_type IS 'Semantic media type with parameters (kind, version, etc.).';
COMMENT ON COLUMN artifact.headline IS 'X-ray Level 0 (headline): essential identity (single line), always present for documents.';
COMMENT ON COLUMN artifact.summary IS 'X-ray Level 1 (summary): key information (~5 lines, max 10) for understanding without reading full content.';
COMMENT ON COLUMN artifact.structure IS 'X-ray Level 2 (structure): detailed outline (~15 lines, max 25) for navigation and exploration.';
```

Producers SHOULD populate all three x‑ray fields when creating artifacts for documents. Each field is independent; do not duplicate lower levels inside higher ones (consumers may combine them in UI as needed):

- **headline** (single line): File type, primary entity name, and essential counts. Always present.
- **summary** (~5 lines, max 10): First paragraph or key classes/functions, notable metadata. Enables understanding without opening.
- **structure** (~15 lines, max 25): Full structural outline (heading tree, class hierarchy, endpoint list). Enables navigation.

### `node`

Graph vertex: documents and contained entities.

```sql
CREATE TABLE IF NOT EXISTS node (
  id                      UUID PRIMARY KEY,
  kind                    TEXT NOT NULL,              -- open taxonomy label
  uri                     TEXT,                       -- container URI for documents only
  container_uri_lowercase TEXT,                       -- lower(uri) for uniqueness
  artifact_id             UUID,                       -- optional
  span_id                 UUID,                       -- optional localizer
  properties              JSON NOT NULL,              -- arbitrary attributes
  created_at              TIMESTAMP NOT NULL,
  updated_at              TIMESTAMP NOT NULL,
  CHECK (kind <> 'document' OR uri IS NOT NULL),
  FOREIGN KEY (artifact_id) REFERENCES artifact(id)
);

CREATE UNIQUE INDEX IF NOT EXISTS node_container_uri_lowercase_unique_index
  ON node(container_uri_lowercase);
CREATE INDEX IF NOT EXISTS node_kind_index ON node(kind);

COMMENT ON TABLE node IS 'Property-graph vertex: documents, sections, symbols, endpoints, etc.';
COMMENT ON COLUMN node.uri IS 'Repository-aware container URI (no fragment). Persisted only for documents.';
```

### `span`

Extent inside a document (lines and/or bytes). Either or both dimensions may be present.

```sql
CREATE TABLE IF NOT EXISTS span (
  id            UUID PRIMARY KEY,
  document_id   UUID NOT NULL,                        -- must point to a document node
  start_byte    BIGINT,
  end_byte      BIGINT,
  start_line    INTEGER,                              -- 1-based
  start_column  INTEGER,                              -- 1-based
  end_line      INTEGER,                              -- 1-based
  end_column    INTEGER,                              -- 1-based
  FOREIGN KEY (document_id) REFERENCES node(id)
);

COMMENT ON TABLE span IS 'Text/byte extent within a single document.';
```

### `edge`

Directed relation between nodes (composition and references).

```sql
CREATE TABLE IF NOT EXISTS edge (
  id                     UUID PRIMARY KEY,
  source_node_id         UUID NOT NULL,
  destination_node_id    UUID NOT NULL,
  type                   TEXT NOT NULL,               -- e.g., HAS_PART, REFERS_TO, CALLS
  is_composition         BOOLEAN NOT NULL,            -- true => containment/ownership
  ordinal                INTEGER,                     -- sibling order for composition
  scope_document_id      UUID,                        -- document where relation is expressed
  semantic_key           TEXT,                        -- idempotent upsert key (optional)
  source_span_id         UUID,                        -- call site / link text
  destination_span_id    UUID,                        -- target definition extent
  composition_child_id   UUID,                        -- = destination when is_composition=true
  properties             JSON NOT NULL,
  created_at             TIMESTAMP NOT NULL,
  FOREIGN KEY (source_node_id)       REFERENCES node(id),
  FOREIGN KEY (destination_node_id)  REFERENCES node(id),
  FOREIGN KEY (scope_document_id)    REFERENCES node(id),
  FOREIGN KEY (source_span_id)       REFERENCES span(id),
  FOREIGN KEY (destination_span_id)  REFERENCES span(id),
  CHECK (composition_child_id IS NULL OR composition_child_id = destination_node_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS edge_semantic_key_unique_index
  ON edge(semantic_key);

CREATE UNIQUE INDEX IF NOT EXISTS edge_composition_single_parent_index
  ON edge(composition_child_id);

CREATE INDEX IF NOT EXISTS edge_source_node_id_index       ON edge(source_node_id);
CREATE INDEX IF NOT EXISTS edge_destination_node_id_index  ON edge(destination_node_id);
CREATE INDEX IF NOT EXISTS edge_type_index                 ON edge(type);
CREATE INDEX IF NOT EXISTS edge_scope_document_id_index    ON edge(scope_document_id);

COMMENT ON TABLE edge IS 'Directed relationship between nodes with optional spans and attributes.';
COMMENT ON COLUMN edge.composition_child_id IS 'Enforces single parent for composition trees.';
```

### `annotation`

Out-of-band facts attached to a document, node, span, edge, or explicit URI.

```sql
CREATE TABLE IF NOT EXISTS annotation (
  id                 UUID PRIMARY KEY,
  semantic_key       TEXT,                -- business key for idempotent upsert
  kind               TEXT NOT NULL,       -- e.g., 'lint', 'outline', 'metric', 'hint', 'broken-link'
  severity           TEXT NOT NULL,       -- 'hint'|'info'|'warning'|'error' (free text allowed)
  source             TEXT NOT NULL,       -- producer name, e.g., 'markdown-parser', 'eslint'
  rule_id            TEXT,                -- producer rule id, e.g., 'MD001', 'E302'
  message            TEXT NOT NULL,       -- human-readable text
  data               JSON NOT NULL,       -- payload (outline array, metrics, etc.)
  scope_document_id  UUID NOT NULL,       -- owning document
  target_node_id     UUID,
  target_edge_id     UUID,
  target_span_id     UUID,
  target_uri         TEXT,                -- external or prebuilt URI (optional)
  created_at         TIMESTAMP NOT NULL,
  expires_at         TIMESTAMP,
  UNIQUE(semantic_key),
  FOREIGN KEY (scope_document_id) REFERENCES node(id),
  FOREIGN KEY (target_node_id)    REFERENCES node(id),
  FOREIGN KEY (target_edge_id)    REFERENCES edge(id),
  FOREIGN KEY (target_span_id)    REFERENCES span(id)
);

CREATE INDEX IF NOT EXISTS annotation_kind_index           ON annotation(kind);
CREATE INDEX IF NOT EXISTS annotation_severity_index       ON annotation(severity);
CREATE INDEX IF NOT EXISTS annotation_scope_document_id_index ON annotation(scope_document_id);
CREATE INDEX IF NOT EXISTS annotation_target_node_id_index ON annotation(target_node_id);
CREATE INDEX IF NOT EXISTS annotation_target_edge_id_index ON annotation(target_edge_id);
CREATE INDEX IF NOT EXISTS annotation_target_span_id_index ON annotation(target_span_id);

COMMENT ON TABLE annotation IS 'Out-of-band facts (lint, outline, metrics, hints) scoped to a document and optionally targeting a node, edge, span, or explicit URI.';
```

### `document_embedding`

Vector store for semantic search (both document-level and object-level embeddings). Rows reference the owning document (`doc_id`) and the specific node (`node_id`) whose text produced the embedding.

```sql
CREATE TABLE IF NOT EXISTS document_embedding (
  doc_id     UUID NOT NULL,
  node_id    UUID NOT NULL,
  uri        TEXT NOT NULL,
  scope      TEXT NOT NULL CHECK (scope IN ('document', 'object')),
  model      TEXT NOT NULL,
  dim        INTEGER NOT NULL,
  embedding  TEXT NOT NULL, -- JSON float array
  updated_at TIMESTAMP NOT NULL,
  PRIMARY KEY (doc_id, node_id),
  FOREIGN KEY (doc_id)  REFERENCES node(id),
  FOREIGN KEY (node_id) REFERENCES node(id)
);

CREATE INDEX IF NOT EXISTS document_embedding_scope_idx ON document_embedding(scope);
CREATE INDEX IF NOT EXISTS document_embedding_uri_idx   ON document_embedding(uri);
CREATE INDEX IF NOT EXISTS document_embedding_model_idx ON document_embedding(model);
```

> **Lifecycle:** the DuckDB writer updates these rows opportunistically (document scope) and during multi-file analysis (object scope). `DeleteSubtreeInternal` removes entries for both `doc_id` and `node_id` before deleting the corresponding nodes, preventing FK violations.

---

## High-Level Views

Abstraction views over the canonical schema for common query patterns. **Prefer these over raw table queries.**

### `files`

File-level metadata for all indexed documents.

| Column | Description |
|--------|-------------|
| `uri`, `file_uri` | Document URI |
| `source` | Origin scheme (`file://`, `github://org/repo`, `docs://`) |
| `path`, `name`, `extension` | Path components |
| `lang` | Language from media type |
| `lines` | Line count |
| `error_count`, `warning_count` | Annotation counts |
| `headline`, `summary`, `structure` | X-ray summaries |
| `node_id`, `artifact_id` | Join keys |

```sql
-- Find all TypeScript files with errors
SELECT name, error_count FROM files WHERE lang = 'typescript' AND error_count > 0;
```

### `types`

Type declarations (classes, interfaces, structs, enums) across languages.

| Column | Description |
|--------|-------------|
| `uri`, `file_uri`, `file_name` | Location |
| `name`, `qualified_name` | Type identity |
| `type_kind` | class, interface, struct, enum, etc. |
| `namespace`, `visibility` | Scope and access |
| `extends`, `implements` | Inheritance |
| `signature`, `headline`, `structure` | Summaries |
| `lang` | Language (csharp, typescript, etc.) |
| `node_id`, `span_id` | Join keys |

```sql
-- Find all classes that implement IDisposable
SELECT qualified_name, file_name FROM types WHERE implements LIKE '%IDisposable%';
```

### `functions`

Callable entities (methods, functions, constructors) across languages.

| Column | Description |
|--------|-------------|
| `uri`, `file_uri`, `file_name` | Location |
| `name`, `qualified_name` | Function identity |
| `function_kind` | method, function, constructor |
| `declaring_type` | Parent type (null for standalone) |
| `visibility`, `signature` | Access and full signature |
| `return_type`, `parameters` | Type information |
| `is_static`, `is_async` | Modifiers |
| `lang` | Language |
| `node_id`, `span_id` | Join keys |

```sql
-- Find all async methods in a class
SELECT name, signature FROM functions WHERE declaring_type = 'MyService' AND is_async;

-- Find all public static methods
SELECT qualified_name, return_type FROM functions WHERE visibility = 'public' AND is_static;
```

---

## Invariants

* **Document uniqueness**: exactly one `node` per container URI (case-insensitive).
* **Composition single parent**: one composition parent per child (`edge_composition_single_parent_index`).
* **Span ownership**: each span belongs to one document.
* **Timestamps**: UTC.

---

## Repository URIs

* **Container**: absolute URI (no query or fragment). Stored only on **document** nodes.
* **Fragments**:

    * `line=a[,b]` (1‑based)
    * `char=a[,b]` (0‑based bytes)
    * JSON Pointer `#/…`
    * key–value (`symbol=…`, etc.) or bare anchors
* **Derived URIs**: compose at query time with helpers.

---

## Media Types

* Base `type/subtype` plus parameters. Examples:

    * `application/json;kind=openapi;version=3.1`
    * `text/markdown;kind=document`
    * `application/zip;kind=playwright-trace;version=1`

---

## Helper UDFs (scalar)

*Registered at startup. Names are descriptive and stable.*

**URI**

* `repository_uri_container(text) -> text?`
* `repository_uri_fragment(text) -> text?`
* `repository_uri_join(text container, text? fragment) -> text?`
* `repository_uri_fragment_kind(text) -> text`  (`json_pointer|line|char|parameters|anchor|empty`)
* `repository_uri_line_start(text) -> int?`
* `repository_uri_line_end(text) -> int?`
* `fragment_from_line_range(int? start, int? end) -> text?`
* `fragment_from_char_range(bigint? start, bigint? end) -> text?`
* `repository_uri_file_name(text) -> text?`
* `glob_match(text uri, text pattern, bool ignore_case := TRUE, text default_scheme := 'file:///') -> boolean?`
  * Git-style glob matching over RepoURIs (supports `**`, `?`, character classes, and `[!negated]`). When a pattern omits the scheme, `default_scheme` is prepended—callers can pass `'embed:///'` to target embedded docs.

**Media type**

* `media_type_base(text) -> text?`
* `media_type_kind(text) -> text?`
* `media_type_version(text) -> text?`
* `media_type_with_parameter(text, text, text?) -> text?`

**Language/snippet helpers**

* `language_from_media_type_or_uri(text media_type, text uri) -> text?`
* `line_for_byte_offset(text content, long? byte_offset) -> int?`
* `column_for_byte_offset(text content, long? byte_offset) -> int?`
* `binary_preview(text storage_uri, int max_bytes) -> text?`

**X‑ray helpers**

* `node_display_label(text kind, text properties_json) -> text?`
* `node_primary_fragment(text kind, text properties_json, int? start_line, int? end_line, long? start_byte, long? end_byte) -> text?`

---

## Core macros and views

### 1) Universal resolver

Single entry point for **URI → entities**.

```sql
CREATE OR REPLACE MACRO entities_by_uri(u) AS TABLE (
  WITH base AS (
    SELECT
      repository_uri_container(u)         AS base,
      repository_uri_fragment(u)          AS frag,
      repository_uri_fragment_kind(u)     AS kind,
      repository_uri_line_start(u)        AS l1,
      repository_uri_line_end(u)          AS l2
  ),
  char_rng AS (
    SELECT
      CASE WHEN kind='char' THEN try_cast(split_part(substr(frag, 6), ',', 1) AS BIGINT) END AS c1,
      CASE WHEN kind='char' THEN try_cast(NULLIF(split_part(substr(frag, 6), ',', 2), '') AS BIGINT) END AS c2
    FROM base
  )
  SELECT
    'document' AS entity, n.id AS id, n.kind AS aux,
    n.uri AS uri, n.uri AS container_uri, NULL AS fragment
  FROM base b
  JOIN node n ON n.container_uri_lowercase = lower(b.base)
  WHERE b.frag IS NULL

  UNION ALL
  SELECT
    'edge', e.id, e.type,
    repository_uri_join(n.uri, 'edge=' || CAST(e.id AS TEXT)),
    n.uri, 'edge=' || CAST(e.id AS TEXT)
  FROM base b
  JOIN node n ON n.container_uri_lowercase = lower(b.base)
  JOIN edge e ON e.scope_document_id = n.id
  WHERE b.frag LIKE 'edge=%' AND substr(b.frag, 6) = CAST(e.id AS TEXT)

  UNION ALL
  SELECT
    'span', s.id, NULL,
    repository_uri_join(n.uri, fragment_from_line_range(s.start_line, s.end_line)),
    n.uri, fragment_from_line_range(s.start_line, s.end_line)
  FROM base b
  JOIN node n ON n.container_uri_lowercase = lower(b.base)
  JOIN span s ON s.document_id = n.id
  WHERE b.kind = 'line'
    AND s.start_line <= COALESCE(b.l1, s.start_line)
    AND s.end_line   >= COALESCE(b.l2, s.end_line)

  UNION ALL
  SELECT
    'span', s.id, NULL,
    repository_uri_join(n.uri, fragment_from_char_range(s.start_byte, s.end_byte)),
    n.uri, fragment_from_char_range(s.start_byte, s.end_byte)
  FROM base b, char_rng r
  JOIN node n ON n.container_uri_lowercase = lower(b.base)
  JOIN span s ON s.document_id = n.id
  WHERE b.kind = 'char'
    AND (r.c1 IS NOT NULL AND s.start_byte <= r.c1)
    AND (r.c2 IS NULL    OR  s.end_byte   >= r.c2)
);
```

### 2) Snippet extractor

Returns focus window with language hint.

```sql
CREATE OR REPLACE MACRO snippet(u, context_lines) AS TABLE (
  WITH base AS (
    SELECT
      repository_uri_container(u)     AS base,
      repository_uri_fragment(u)      AS frag,
      repository_uri_fragment_kind(u) AS kind,
      repository_uri_line_start(u)    AS l1,
      repository_uri_line_end(u)      AS l2
  ),
  doc AS (
    SELECT n.id AS doc_id, n.uri AS uri, a.text_content, a.media_type, a.storage_uri
    FROM base b
    JOIN node n ON n.container_uri_lowercase = lower(b.base)
    LEFT JOIN artifact a ON a.id = n.artifact_id
  ),
  edge_focus AS (
    SELECT e.id AS edge_id,
           ss.start_line   AS el1, ss.end_line   AS el2,
           ss.start_column AS ec1, ss.end_column AS ec2
    FROM base b
    JOIN edge e ON b.frag LIKE 'edge=%' AND substr(b.frag, 6) = CAST(e.id AS VARCHAR)
    LEFT JOIN span ss ON ss.id = e.source_span_id
  ),
  char_rng AS (
    SELECT
      CASE WHEN kind='char' THEN try_cast(split_part(substr(frag, 6), ',', 1) AS BIGINT) END AS c1,
      CASE WHEN kind='char' THEN try_cast(NULLIF(split_part(substr(frag, 6), ',', 2), '') AS BIGINT) END AS c2
    FROM base
  ),
  focus AS (
    SELECT
      COALESCE(
        (SELECT el1 FROM edge_focus),
        (SELECT l1  FROM base),
        (SELECT line_for_byte_offset(text_content, c1) FROM doc, char_rng),
        1
      ) AS fl1,
      COALESCE(
        (SELECT el2 FROM edge_focus),
        (SELECT l2  FROM base),
        (SELECT NULLIF(line_for_byte_offset(text_content, c2), 0) FROM doc, char_rng)
      ) AS fl2,
      COALESCE(
        (SELECT ec1 FROM edge_focus),
        (SELECT column_for_byte_offset(text_content, c1) FROM doc, char_rng)
      ) AS fc1,
      COALESCE(
        (SELECT ec2 FROM edge_focus),
        (SELECT column_for_byte_offset(text_content, c2) FROM doc, char_rng)
      ) AS fc2
  ),
  raw_text AS (
    SELECT CASE
             WHEN text_content IS NOT NULL THEN text_content
             ELSE COALESCE(binary_preview(storage_uri, 4096), '')
           END AS content
    FROM doc
  ),
  lines AS (
    SELECT ROW_NUMBER() OVER () AS ln, value AS line
    FROM raw_text, UNNEST(string_split(content, CHR(10))) AS t(value)
  ),
  win AS (
    SELECT
      GREATEST(1, COALESCE(fl1,1) - COALESCE(context_lines,3)) AS w1,
      COALESCE(COALESCE(fl2,fl1) + COALESCE(context_lines,3), 1 + COALESCE(context_lines,3)*2) AS w2
    FROM focus
  )
  SELECT
    ln AS line_number,
    line AS text,
    (ln BETWEEN fl1 AND COALESCE(fl2, fl1)) AS is_focus,
    fc1 AS focus_start_column,
    fc2 AS focus_end_column,
    language_from_media_type_or_uri((SELECT media_type FROM doc), (SELECT uri FROM doc)) AS language,
    (SELECT uri FROM doc) AS document_uri,
    repository_uri_join(
      (SELECT uri FROM doc),
      'line=' || CAST(fl1 AS VARCHAR) || COALESCE(',' || CAST(fl2 AS VARCHAR), '')
    ) AS resolved_uri
  FROM lines, win, focus
  WHERE ln BETWEEN w1 AND w2
  ORDER BY ln
);
```

### 3) Files view

The primary document inventory view with pre-computed summaries.

```sql
CREATE OR REPLACE VIEW Files AS
SELECT
    n.uri,
    repository_uri_file_name(n.uri) AS name,
    media_type_lang(a.media_type) AS lang,
    a.byte_size,
    a.headline,
    a.summary,
    a.structure
FROM node n
JOIN artifact a ON n.artifact_id = a.id
WHERE n.kind = 'document';
```

For per-document structure, query node/edge directly:

```sql
-- Get child items of a document
SELECT c.kind, c.properties, s.start_line, s.end_line
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.is_composition = TRUE
JOIN node c ON c.id = e.destination_node_id
LEFT JOIN span s ON s.id = c.span_id
WHERE doc.uri = 'file:///src/Example.cs'
ORDER BY s.start_line;
```

### 4) Annotations view and filters

```sql
CREATE OR REPLACE MACRO _severity_rank(s) AS (
  CASE lower(s)
    WHEN 'error'   THEN 4
    WHEN 'warning' THEN 3
    WHEN 'info'    THEN 2
    WHEN 'hint'    THEN 1
    ELSE 0
  END
);

CREATE VIEW IF NOT EXISTS annotations AS
WITH base AS (
  SELECT a.*, sd.uri AS scope_document_uri
  FROM annotation a
  JOIN node sd ON sd.id = a.scope_document_id
),
span_uri AS (
  SELECT a.id,
         repository_uri_join(b.scope_document_uri,
           fragment_from_line_range(s.start_line, s.end_line)) AS uri_from_span
  FROM base b
  JOIN annotation a ON a.id = b.id
  LEFT JOIN span s  ON s.id = a.target_span_id
),
node_frag AS (
  SELECT a.id,
         node_primary_fragment(n.kind, n.properties, s.start_line, s.end_line, s.start_byte, s.end_byte) AS frag
  FROM base b
  JOIN annotation a ON a.id = b.id
  LEFT JOIN node n  ON n.id = a.target_node_id
  LEFT JOIN span s  ON s.id = n.span_id
),
edge_uri AS (
  SELECT a.id,
         repository_uri_join(b.scope_document_uri, 'edge=' || CAST(e.id AS TEXT)) AS uri_from_edge
  FROM base b
  JOIN annotation a ON a.id = b.id
  LEFT JOIN edge e  ON e.id = a.target_edge_id
)
SELECT
  a.*,
  COALESCE(
    a.target_uri,
    su.uri_from_span,
    CASE WHEN nf.frag IS NOT NULL THEN repository_uri_join(b.scope_document_uri, nf.frag) END,
    eu.uri_from_edge,
    b.scope_document_uri
  ) AS resolved_target_uri,
  _severity_rank(a.severity) AS severity_rank
FROM annotation a
JOIN base b   ON b.id = a.id
LEFT JOIN span_uri su ON su.id = a.id
LEFT JOIN node_frag nf ON nf.id = a.id
LEFT JOIN edge_uri eu  ON eu.id = a.id;

CREATE OR REPLACE MACRO annotations_for(u, kinds, min_severity) AS TABLE (
  WITH doc AS (
    SELECT id AS doc_id FROM node
    WHERE lower(uri) = lower(repository_uri_container(u))
  )
  SELECT *
  FROM annotations a, doc
  WHERE a.scope_document_id = doc.doc_id
    AND (kinds IS NULL OR EXISTS (
          SELECT 1 FROM UNNEST(string_split(kinds, ',')) k(value)
          WHERE lower(trim(k.value)) = lower(a.kind)))
    AND (_severity_rank(a.severity) >= _severity_rank(COALESCE(min_severity,'hint')))
  ORDER BY severity_rank DESC, created_at DESC
);

CREATE OR REPLACE MACRO annotations_all(kinds, min_severity) AS TABLE (
  SELECT *
  FROM annotations
  WHERE (kinds IS NULL OR EXISTS (
          SELECT 1 FROM UNNEST(string_split(kinds, ',')) k(value)
          WHERE lower(trim(k.value)) = lower(annotations.kind)))
    AND (_severity_rank(severity) >= _severity_rank(COALESCE(min_severity,'hint')))
  ORDER BY severity_rank DESC, created_at DESC
);
```

---

## Query patterns

* **Resolve anything by URI**

  ```sql
  SELECT * FROM entities_by_uri('file:///repo/app.py#line=42');
  ```

* **Get a snippet**

  ```sql
  SELECT * FROM snippet('file:///repo/app.py#edge=8b4f…', 3);
  ```

* **Repo inventory (compact)**

  ```sql
  SELECT name, lang, headline FROM Files ORDER BY lower(name);
  ```

* **Lint and outline for one file**

  ```sql
  SELECT severity, source, rule_id, message, resolved_target_uri
  FROM annotations_for('file:///repo/README.md', 'lint,outline', 'info');
  ```

---

## Design choices and tradeoffs

* **URIs only on documents**: single source of truth; derived fragments via UDFs/macros.
* **Open vocabularies** (`node.kind`, `edge.type`, `annotation.kind`): easy to extend; conventions over enums.
* **Idempotent upserts** via `semantic_key` on `edge` and `annotation`.
* **Partial spans allowed**: line‑only, byte‑only, or both.
* **Annotations**: first-class table for lint, outlines, and tool results, including targets that are **edges**.

---

This is the complete, current core.
