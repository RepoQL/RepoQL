## Repo URIs

All elements are identified by special URIs defined as follows:

```ABNF
repo-uri  = container [ "#" fragment ]
  container = absolute-uri-without-fragment
  fragment  = json-pointer / params / range / anchor

  json-pointer = "/" *( pchar / "/" )
  params       = param *( "&" param )
  param        = key [ "=" value ]
  key          = 1*( ALPHA / DIGIT / "_" / "-" )
  value        = *pchar
  range        = "line=" number [ "," number ]
               / "char=" number [ "," number ]
  anchor       = 1*( unreserved / pct-encoded / "." / "-" / "_" )
  number       = 1*DIGIT

  pchar        = unreserved / pct-encoded / sub-delims / ":" / "@"
  pct-encoded  = "%" HEXDIG HEXDIG
  unreserved   = ALPHA / DIGIT / "-" / "." / "_" / "~"
  sub-delims   = "!" / "$" / "&" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "="
```

  - container may be file:///…, https://…, or jar:file:///a.zip!/b.txt; it has no fragment.
  - Fragment precedence: try json-pointer; else params; else range only when the fragment is exactly line=… or char=…; otherwise anchor.
  - Reserved keys inside params: symbol, line, char; others are opaque.
  - line is 1-based inclusive; char is 0-based half-open.

  **Examples**

  - file:///repo/README.md#line=40,55
  - file:///repo/lib.cs#symbol=Foo.Bar&line=12,20
  - file:///api/openapi.yaml#/components/schemas/User
  - jar:file:///artifacts/logs.zip!/trace.txt#line=1,200
  - file:///repo/README.md#installation
  - https://example.com/repo#section=intro&page=1

## Semantic Content Type

The content of files is stored as a mime-inspired content type format

```ABNF
semtype = type "/" subtype [ "+" suffix ] *( ";" param )
  param   = OWS key [ "=" token-or-quoted ]
  key     = lowercase token

  reserved params:
    kind=<token>        ; representation identifier (e.g. markdown.doc, openapi.spec, playwright.trace)
    profile="<uri>"     ; profile URI (RFC 6906)
    schema="<uri>"      ; schema/IDL URI
    version=<token>     ; representation version
    charset=<token>     ; text encoding (standard MIME)
```

  Normalize by lowercasing type/subtype/suffix/keys and sorting params by key. Unknown params must round-trip.

  ### Semantics

  - type/subtype[+suffix] spells the wire format; kind names the payload’s role in RepoQL’s graph.
  - profile and schema anchor meaning; version marks contract changes; charset applies only to text. Other parameters remain opaque.

  ### Examples

- text/markdown;kind=markdown.doc
- application/json;kind=config.app;version=2.0;profile="https://schemas.example.com/app-config-v2"
- text/typescript;charset=utf-8;kind=ts.module;schema="https://specs.deno.land/module.schema.json" 
- application/vnd.api+json;kind=api.response;profile="https://jsonapi.org/profiles/etag";version=1.1;tenant=blue
- application/ld+json;kind=metadata.structured;profile="https://schema.org/Person";schema="https://json-ld.org/schemas/person.json"
- application/wasm;kind=wasm.module;version=1.0
- text/x-python;kind=py.test;profile="https://docs.pytest.org/test-module"
- application/x-protobuf;kind=protobuf.message;schema="https://schemas.corp.com/user.proto";version=3
- video/mp4;kind=media.presentation
- application/sql;kind=migration.up;version=20240115
- text/x-diff;kind=patch.unified
- application/gzip;kind=archive.compressed
- text/calendar;charset=utf-8;kind=ical.event;profile="https://tools.ietf.org/html/rfc5545"
- application/json;kind=api.spec;profile="https://example.com/spec";version=1.0   ; normalized example

## Schema

### Core Schema

  - artifact(id,digest,byte_size,media_type,text_content,storage_uri) — unique blob per content hash.
  - node(id,kind,uri,container_uri_lowercase,artifact_id,span_id,properties,created_at,updated_at) — every document or extracted entity.
  - span(id,document_id,start_byte,end_byte,start_line,start_column,end_line,end_column) — location of text ranges.
  - edge(id,source_node_id,destination_node_id,type,is_composition,ordinal,scope_document_id,semantic_key,source_span_id,destination_span_id,composition_child_id,properties,created_at) — graph relations.
  - annotation(id,semantic_key,kind,severity,source,rule_id,message,data,scope_document_id,target_node_id,target_edge_id,target_span_id,target_uri,created_at,expires_at) — lint-style facts.
  - annotations view — adds resolved_target_uri, severity rank, joins back to document URIs.

 ### Table Macros

  - snippet(uri, context_lines) — rows: line_number, text, is_focus, optional focus columns, language, document/resolved URIs.
  - entities_by_uri(uri) — rows linking a repo URI to document/edge/span IDs and fragments.
  - annotations_for(uri, kinds, min_severity) — subset of annotations view for one document.
  - annotations_all(kinds, min_severity) — global filter over annotations view.

 ### Scalar UDF Families

  - repository_uri_* — manipulate RepoQL URIs (container, fragment, join, line_start, line_end, json_pointer, anchor, file_name, etc.).

  - media_type_* — parse/augment semantic media types (base, kind, version, with_parameter).

  - Snippet helpers — binary_preview, line_for_byte_offset, column_for_byte_offset, fragment_from_line_range, fragment_from_char_range, language_from_media_type_or_uri.

    

    ### Views

    Different artifact types often expose higher-level views layered on the core tables so you can query them using domain terms. 

    For example, when a markdown file is parsed you might see a view like
      markdown_headings(document_uri, heading_uri, level, text, slug, start_line, end_line, start_column, end_column) derived from node/edge/span, or for OpenAPI specs a openapi_endpoints(method, path, operation_id) view projected from JSON-pointer nodes. File formats such as Playwright traces can add their own views (e.g. playwright_trace_events). 

These views are discoverable at runtime—run something like:
```sql
SELECT table_name
FROM information_schema.tables
WHERE table_type = 'VIEW';
```

Then inspect the view definition with 

```sql
SELECT sql FROM duckdb_views() WHERE table_name = 'markdown_headings'; 
```
to see how it maps back to the base schema.

## Search

Search = lexical + semantic. One name. No flags.

- `file_search(q, k := 50, max_cand := 5000)` → `uri, score` (and `bm25n, fuzzn, semn` if you want them)

Intent‑only: write what you want; the host blends signals.

```sql
-- Top files by intent
SELECT uri, score, semn
FROM file_search('mermaid diagram classes', k := 10);

-- Semantics-first view
SELECT uri, semn, score
FROM file_search('embedding runtime broadcast error', k := 20)
ORDER BY semn DESC NULLS LAST;

-- Filter by file type/location
WITH r AS (
  SELECT doc_id, uri, score FROM file_search('frontmatter', k := 50)
)
SELECT r.uri, r.score
FROM r JOIN document_search ds USING (doc_id)
WHERE lower(ds.basename) LIKE '%.md' AND lower(ds.dirname) LIKE '%/docs%';
```
More info in embed:///advanced-search.md

## Quick Start

Use this SQL to catalog the embedded RepoQL docs:

  ```sql
  WITH docs AS (
      SELECT id, uri, artifact_id
      FROM node
      WHERE kind = 'document' AND uri LIKE 'embed:/%'
    ),
    parts AS (
      SELECT e.source_node_id AS doc_id,
             c.kind,
             COUNT(*) AS item_count
      FROM edge e
      JOIN node c ON c.id = e.destination_node_id
      WHERE e.is_composition
      GROUP BY e.source_node_id, c.kind
    ),
    kind_summary AS (
      SELECT doc_id,
             string_agg(kind || ':' || CAST(item_count AS TEXT), ' ') AS contents
      FROM parts
      GROUP BY doc_id
    )
    SELECT
      d.uri AS document_uri,
      replace(d.uri, 'embed:///', '') AS repo_path,
      repository_uri_file_name(d.uri) AS file_name,
      media_type_base(a.media_type) AS media_base,
      media_type_kind(a.media_type) AS media_kind,
      a.byte_size,
      COALESCE(k.contents, '') AS entity_counts
    FROM docs d
    LEFT JOIN artifact a ON a.id = d.artifact_id
    LEFT JOIN kind_summary k ON k.doc_id = d.id
    ORDER BY file_name;
  ```

Most entities in the repository will originate in a file:// node

