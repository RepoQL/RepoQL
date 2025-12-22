# RepoQL Essentials

## Foundation

Five tables. Everything addressable by URI. Pre-computed summaries save tokens.

| Table | Purpose |
|-------|---------|
| `artifact` | Content: text_content, digest, **headline/summary/structure** |
| `node` | Entities: documents, functions, classes, headings |
| `edge` | Relationships: CALLS, IMPORTS, REFERS_TO, composition |
| `span` | Locations: line/byte ranges within documents |
| `annotation` | Facts: lint errors, metrics, analysis results |

**URI fragments address precisely:**
`file:///src/auth.cs#line=42,50` | `file:///lib.cs#symbol=Foo.Bar` | `file:///api.yaml#/paths/users`

---

## Capsule: Composition

**Invariant**
LATERAL joins expand each result row with context or related data in one query.

**Example**
```sql
-- Search + code context
SELECT f.uri, s.line_number, s.text
FROM search('auth', k := 5) f,
     LATERAL snippet(f.uri, 2) s
WHERE s.is_focus;
```

**Depth**
- `LATERAL` = for each row, invoke function with that row's values
- Compose: search -> snippet, search -> annotation, node -> edges
- NotThis: fetch results then loop in application code

---

## Capsule: RegexExtraction

**Invariant**
One query extracts all pattern instances across the codebase once the pattern is known.

**Example**
```sql
-- Every TODO
SELECT n.uri, regexp_extract_all(a.text_content, 'TODO:\s*(.+)', 1) AS todos
FROM node n JOIN artifact a ON n.artifact_id = a.id
WHERE n.kind = 'document' AND regexp_matches(a.text_content, 'TODO:');

-- Frequency per file
SELECT n.uri, length(regexp_extract_all(a.text_content, 'console\.log', 0)) AS count
FROM node n JOIN artifact a ON n.artifact_id = a.id
WHERE n.kind = 'document' AND count > 0
ORDER BY count DESC;
```

**Depth**
- `regexp_extract_all(text, pattern, group)` -> list of matches
- `regexp_matches(text, pattern)` -> boolean filter
- Group 0 = full match; 1+ = captures
- NotThis: looping over files in application code

---

## Capsule: GraphTraversal

**Invariant**
Edges encode relationships; query them instead of parsing code.

**Example**
```sql
-- What calls this function?
SELECT src.uri, src.properties->>'$.symbol' AS caller
FROM edge e
JOIN node src ON e.source_node_id = src.id
JOIN node tgt ON e.destination_node_id = tgt.id
WHERE tgt.uri LIKE '%#symbol=ProcessRequest%' AND e.type = 'CALLS';

-- Document structure
SELECT child.kind, child.properties->>'$.name'
FROM node doc
JOIN edge e ON e.source_node_id = doc.id AND e.is_composition
JOIN node child ON e.destination_node_id = child.id
WHERE doc.uri = 'file:///src/api.cs';
```

**Depth**
- `edge.type`: CALLS, IMPORTS, REFERS_TO, INHERITS
- `edge.is_composition`: parent contains child
- NotThis: parsing source to find call sites

---

## Capsule: AnnotationQuery

**Invariant**
Annotations are pre-computed facts; query them instead of re-analyzing.

**Example**
```sql
-- All errors
SELECT resolved_target_uri, rule_id, message
FROM annotations WHERE severity = 'error';

-- Per-file counts using FILTER
SELECT n.uri,
       count(*) FILTER (WHERE severity = 'error') AS errors,
       count(*) FILTER (WHERE severity = 'warning') AS warnings
FROM node n JOIN annotation a ON a.scope_document_id = n.id
GROUP BY n.uri HAVING errors > 0;
```

**Depth**
- Kinds: lint, metric, diagnostic
- `annotations` view = joined with URIs; `annotation` table = raw
- NotThis: re-running analysis on content

---

## Capsule: ViewDiscovery

**Invariant**
Format-specific views project the graph into domain terms; discover at runtime.

**Example**
```sql
-- Available views
SELECT table_name FROM information_schema.tables WHERE table_type = 'VIEW';

-- Domain queries
SELECT document_uri, level, text FROM markdown_headings WHERE level <= 2;
SELECT namespace, name FROM csharp_types WHERE kind = 'class';
```

**Depth**
- Views from parsers: markdown_headings, csharp_types, csharp_members
- Inspect: `SELECT sql FROM duckdb_views() WHERE view_name = '...'`
- NotThis: manual node/edge/span joins when a view exists

---

## DuckDB Patterns

| Pattern | Effect |
|---------|--------|
| `regexp_extract_all(t, p, n)` | All matches as list |
| `regexp_matches(t, p)` | Boolean filter |
| `FROM x, LATERAL fn(x.col)` | Expand rows |
| `count(*) FILTER (WHERE c)` | Conditional count |
| `QUALIFY row_number() OVER(...) <= n` | Top-N per group |
| `list_transform(l, lambda x: ...)` | Map over list |
| `col->>'$.key'` | JSON extract as text |
| `GROUP BY ALL` | Auto-group non-aggregates |
| `SELECT * EXCLUDE (col)` | All except column |

---

## Kitchen Sink Query

One query demonstrating all patterns - starts with semantic search, then extracts:

```sql
WITH candidates AS (
  -- SEMANTIC SEARCH: find relevant files first
  SELECT uri, score AS search_score
  FROM search('error handling', k := 20)
),
extracted AS (
  -- REGEX EXTRACTION + JSON on candidates only
  SELECT
    c.uri,
    c.search_score,
    n.id AS node_id,
    n.properties->>'$.language' AS lang,
    regexp_extract_all(a.text_content, '(TODO|FIXME|HACK):\s*(.+)', 0) AS raw_tasks,
    length(regexp_extract_all(a.text_content, 'TODO|FIXME|HACK', 0)) AS task_count
  FROM candidates c
  JOIN node n ON n.uri = c.uri AND n.kind = 'document'
  JOIN artifact a ON n.artifact_id = a.id
  WHERE regexp_matches(a.text_content, 'TODO|FIXME|HACK')
),
with_annotations AS (
  -- FILTER + GROUP BY ALL + QUALIFY
  SELECT
    e.uri, e.search_score, e.lang, e.task_count, e.raw_tasks,
    count(ann.id) FILTER (WHERE ann.severity = 'error') AS errors,
    count(ann.id) FILTER (WHERE ann.severity = 'warning') AS warnings,
    count(ed.id) FILTER (WHERE ed.type = 'CALLS') AS outgoing_calls
  FROM extracted e
  LEFT JOIN annotation ann ON ann.scope_document_id = e.node_id
  LEFT JOIN edge ed ON ed.source_node_id = e.node_id
  GROUP BY ALL
  QUALIFY row_number() OVER (ORDER BY task_count DESC) <= 10
),
with_context AS (
  -- LATERAL composition with snippet
  SELECT wa.*, s.line_number, s.text AS context
  FROM with_annotations wa,
       LATERAL snippet(wa.uri, 1) s
  WHERE s.text ~ 'TODO|FIXME|HACK'
)
SELECT
  * EXCLUDE (raw_tasks, context),  -- EXCLUDE verbose columns
  list_transform(raw_tasks, lambda t: t[1] || ': ' || left(t[2], 40)) AS tasks,  -- lambda
  context AS sample_line
FROM with_context
ORDER BY search_score DESC, task_count DESC, line_number
LIMIT 20;
```

Uses: `search`, `regexp_extract_all`, `regexp_matches`, `LATERAL snippet`, `FILTER`, `QUALIFY`, `GROUP BY ALL`, `EXCLUDE`, `list_transform`, `->>'$.key'`, edges, annotations.

---

## Checklist

- [ ] Use xray tool first; query tool for composition/aggregation
- [ ] Compose with LATERAL; avoid app-side loops
- [ ] Query edges for relationships, not code parsing
- [ ] Query annotations for pre-computed facts
- [ ] Check for domain views before manual joins