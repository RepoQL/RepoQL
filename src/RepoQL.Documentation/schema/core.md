---
description: "artifact(id,digest,byte_size,media_type,text_content,headline,summary,structure) + node(id,kind,uri,artifact_id,span_id,properties) + edge(source_node_id,destination_node_id,type,is_composition) + span(document_id,start_line,end_line) + annotation(kind,severity,source,message,target_*)"
tags: ["Schema", "Tables", "PropertyGraph", "Core", "Architecture"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Schema[100%]"]
---

# Core Tables

Five frozen tables. Extend via views/macros/UDFs only.

```
artifact ──artifact_id──► node ◄──document_id── span
                           │                      ▲
                           │ source_node_id       │ span_id
                           ▼                      │
                         edge                   node (child)
                           │                      ▲
                           │ target_*             │ scope_document_id
                           ▼                      │
                      annotation ─────────────────┘
```

---

## Capsule: ModelOverview

**Invariant**
Files → artifact + document node. Symbols → child nodes with spans. Containment → composition edges. References → non-composition edges. Diagnostics → annotations.

**Example**
```sql
-- Document with content
SELECT n.uri, a.headline, a.text_content
FROM node n JOIN artifact a ON a.id = n.artifact_id
WHERE n.kind = 'document';

-- Child symbols
SELECT n.uri, n.properties->>'name', s.start_line
FROM node n JOIN span s ON s.id = n.span_id
WHERE n.kind LIKE 'csharp.%';
```
//BOUNDARY: Documents have artifact_id; children have span_id. Never both.

---

## Capsule: Artifact

**Invariant**
`artifact(id, digest, byte_size, media_type, text_content, storage_uri, headline, summary, structure, token_count)`

**Example**
```sql
SELECT digest, byte_size, headline FROM artifact WHERE media_type LIKE '%csharp%';
SELECT text_content FROM artifact WHERE id = (SELECT artifact_id FROM node WHERE uri = 'file:///src/Foo.cs');
```
//BOUNDARY: Deduplicated by digest. Multiple files with same content share one artifact.

**Depth**
- `digest`: sha256 hash, unique constraint
- `headline/summary/structure`: X-ray levels 0/1/2
- `token_count`: Estimated LLM tokens, NULL for binary
- `storage_uri`: External blob storage (optional)

---

## Capsule: Node

**Invariant**
`node(id, kind, uri, container_uri_lowercase, artifact_id, span_id, properties, headline, structure, created_at, updated_at)`

**Example**
```sql
-- Documents
SELECT uri, headline FROM node WHERE kind = 'document';

-- Types
SELECT uri, properties->>'name' FROM node WHERE kind = 'csharp.type';

-- By property
SELECT * FROM node WHERE properties->>'qualified_name' = 'MyNamespace.MyClass';
```
//BOUNDARY: kind='document' requires uri. Others use span_id for location.

**Depth**
- `kind`: Open taxonomy (`document`, `csharp.type`, `csharp.member`, `markdown.heading`, etc.)
- `uri`: Full URI for documents; fragment URI or NULL for children
- `properties`: JSON bag (name, qualified_name, visibility, etc.)
- `artifact_id`: Only for documents
- `span_id`: Only for sub-document nodes

---

## Capsule: Edge

**Invariant**
`edge(id, source_node_id, destination_node_id, destination_uri, type, is_composition, ordinal, scope_document_id, semantic_key, source_span_id, destination_span_id, properties, created_at)`

**Example**
```sql
-- Composition tree (parent → children)
SELECT e.ordinal, child.properties->>'name'
FROM edge e JOIN node child ON child.id = e.destination_node_id
WHERE e.source_node_id = @parent_id AND e.is_composition = true
ORDER BY e.ordinal;

-- References (cross-file and within-file)
SELECT e.type, dst.uri FROM edge e
JOIN node dst ON dst.id = e.destination_node_id
WHERE e.is_composition = false AND e.type = 'REFERS_TO';
```
//BOUNDARY: is_composition=true → tree (HAS_PART). is_composition=false → graph (REFERS_TO, USES_SYMBOL, EXTENDS, etc.).

**Depth**
- `type`: `HAS_PART` (stable), `REFERS_TO` (stable), plus format-specific types (USES_SYMBOL, EXTENDS, IMPLEMENTS)
- `ordinal`: Source order for composition children
- `destination_uri`: Deferred resolution for cross-file refs
- `semantic_key`: Idempotent upsert key

---

## Capsule: Span

**Invariant**
`span(id, document_id, start_byte, end_byte, start_line, start_column, end_line, end_column)`

**Example**
```sql
-- Node location
SELECT n.properties->>'name', s.start_line, s.end_line
FROM node n JOIN span s ON s.id = n.span_id;

-- Extract text
SELECT substr(a.text_content, s.start_byte + 1, s.end_byte - s.start_byte)
FROM span s
JOIN node doc ON doc.id = s.document_id
JOIN artifact a ON a.id = doc.artifact_id
WHERE s.id = @span_id;
```
//BOUNDARY: Lines are 1-based inclusive. Bytes are 0-based, end exclusive.

**Depth**
- `document_id`: FK to document node (not artifact)
- Use `start_line/end_line` for display
- Use `start_byte/end_byte` for text extraction

---

## Capsule: Annotation

**Invariant**
`annotation(id, semantic_key, kind, severity, source, rule_id, message, data, scope_document_id, target_node_id, target_edge_id, target_span_id, target_uri, created_at, expires_at)`

**Example**
```sql
-- Errors
SELECT message, source FROM annotation WHERE severity = 'error';

-- For a file
SELECT severity, message FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id
WHERE doc.uri = 'file:///src/Foo.cs';
```
//BOUNDARY: Use `Annotations` view for resolved_target_uri. Raw table has separate target_* columns.

**Depth**
- `kind`: `lint`, `metric`, `outline`, `hint`
- `severity`: `error`, `warning`, `info`, `hint`
- `target_*`: At most one set; resolution order: uri → span → node → edge → scope_document
- `data`: JSON with rule-specific metadata

---

## Quick Patterns

| Task | Query |
|------|-------|
| Document content | `node n JOIN artifact a ON a.id = n.artifact_id WHERE n.kind = 'document'` |
| Child nodes | `node n JOIN span s ON s.id = n.span_id WHERE s.document_id = @doc_id` |
| Composition tree | `edge WHERE is_composition = true ORDER BY ordinal` |
| References out | `edge WHERE is_composition = false AND source_node_id = @id` |
| References in | `edge WHERE is_composition = false AND destination_node_id = @id` |
| Node location | `node n JOIN span s ON s.id = n.span_id` |
| File errors | `annotation WHERE scope_document_id = @doc_id AND severity = 'error'` |

---

## Prefer Views

| Instead of | Use |
|------------|-----|
| `node WHERE kind='document' JOIN artifact` | `Files` |
| `node WHERE kind LIKE '%.type'` | `Types` |
| `node WHERE kind LIKE '%.member'` | `Functions` |
| `annotation JOIN span/node` | `Annotations` |
