# Repository Query Language

<CONCEPT>
Treat the entities and structures contained inside repo files as a database to quickly understand repository contents and find features in many different file types

**Read unfamiliar files only after searching with  RepoQL first**
</CONCEPT>

<PURPOSE>
- Find structures in files with semantic search, avoid reading files you don't need to
- Understand contents of files without token waste (Structure, relationships, dependencies, technologies)
- See linting errors across many file types (annotations)
- Understand "what uses this?" and "What links to this?" and "What breaks if I change this?"
</PURPOSE>

<CONTEXT>
- Dialect is DuckDB flavored SQL with custom UDFs
- Assume all file types are supported
- Every entity is represented by a repo URI e.g.
  `file:///repo/lib.cs#symbol=Foo.Bar&line=12,20`
  `docs:///quickstart`
- Semantic mime type indicates both file type and contents e.g.
  `application/x-protobuf;kind=protobuf.message;schema="https://schemas.corp.com/user.proto";version=3`
</CONTEXT>

<SCHEMA>
 Everything is a graph. Files are nodes with artifacts (bytes). Entities inside files (headings, functions, etc.) are child nodes connected by edges. Precise locations use spans. Everything else (lint, metrics, outlines) is annotations.

Core Tables

-- Content (bytes + text + x-ray summaries)
artifact(id, digest, media_type, text_content, headline, summary, structure)

-- Entities (documents and everything inside them)
node(id, kind, uri, artifact_id, span_id, properties[JSON])
-- Only 'document' nodes have uri; others addressed via span

-- Relationships (composition=tree, references=graph)
edge(id, source_node_id, destination_node_id, type, is_composition, ordinal)
-- type: 'HAS_PART' (composition) or 'REFERS_TO', 'CALLS', etc.

-- Locations (precise line/char ranges)
span(id, document_id, start_line, end_line, start_byte, end_byte)
-- Lines: 1-based inclusive. Chars: 0-based half-open

-- Diagnostics & facts (lint, outlines, metrics, etc.)
annotation(id, kind, severity, source, message, data[JSON], scope_document_id, target_node_id, resolved_target_uri)

</SCHEMA>

<ESSENTIAL_MACROS>
SELECT * FROM xray_documents()  -- document inventory with summaries
SELECT * FROM snippet('file:///path#line=42', 3)  -- code preview with context

-- Semantic + lexical search
SELECT uri, score FROM search('auth JWT refresh', k := 10)
SELECT uri, score FROM search('config', scope := 'file:///src/%')  -- scoped

-- Symbol lookup (functions/classes)
SELECT uri, symbol, kind, line_start FROM _search_candidates('ProcessRequest', k := 10) WHERE scope = 'object'

-- Diagnostics
SELECT * FROM annotations WHERE severity = 'error'

-- LLM-powered summarization
SELECT llm_summarize(json_data, 'What patterns exist?', 300)
</ESSENTIAL_MACROS>

Docs at docs:///quickstart.md, docs:///sql-reference.md, docs:///advanced-search.md

<SEARCH_TIPS>
search(keywords, scope, k) → ranked documents with semantic + BM25 scoring
_search_candidates(q, k) → documents + objects (use WHERE scope='object' for symbols)
- Symbol exact match scores 4.0 BM25
- dense_score NULL → embeddings still loading
</SEARCH_TIPS>

## Examples

### List embedded RepoQL documentation

```postgresql
SELECT
      n.uri, /* e.g. docs:///querying-markdown.md*/
      a.headline, /* Querying Markdown with RepoQL — querying-markdown.md | markdown.doc | 5725 | 151 lines | lang: sql | topics: Core Schema Mapping, Markdown Views, Markdown-Specific UDFs & Macros*/
      a.summary, /* Most important details of contents, format depends on mime, < 10 lines */
      a.structure /* Expanded details of contents, format depends on mime, < 25 lines */
  FROM node AS n /* node = file contents, usually 1:1 with artifact */
  JOIN artifact AS a ON n.artifact_id = a.id /* artifact = node container (usually file) */
  WHERE n.kind = 'document' 
    AND n.uri LIKE 'docs://%' /* docs are embedded, repo files usually file:/// */
  ORDER BY LOWER(n.uri);
```

### Fetch Content

```postgresql
SELECT a.text_content
  FROM node AS n
  JOIN artifact AS a ON n.artifact_id = a.id
  WHERE n.uri = 'docs:///quickstart.md';
```

### List all markdown docs in repo + headlines
```postgresql
SELECT
      n.uri,
      a.headline
  FROM node AS n
  JOIN artifact AS a ON n.artifact_id = a.id
  WHERE n.kind = 'document'
    AND a.media_type LIKE '%markdown.doc%'
  ORDER BY LOWER(n.uri);
/*
Do this before starting work so that you know what documentation exists
*/
```

### Ranked semantic search + snippets

```postgresql
WITH search_results AS (
    SELECT uri, score
    FROM search('navigation loading', k := 3)
  )
  SELECT
    sr.uri,
    sr.score,
    sn.line_number,
    sn.text,
    sn.is_focus
  FROM search_results AS sr,
       LATERAL snippet(sr.uri, 2) AS sn
  ORDER BY sr.score DESC, sn.line_number;
  /*
  - search(keywords, k) does semantic + BM25 lookup (k := 3 keeps top 3)
  - snippet(uri, 2) returns 2 lines of context; is_focus marks focal lines
  - Order by score DESC for best matches first
  */
```

```postgresql
/* format-specific views */
SELECT view_name FROM duckdb_views() ORDER BY view_name
```

### POSIX Command line

Repoql is also available as a command line tool - useful for piping

```bash
repoql query "WITH gql_headings AS (SELECT heading_uri, document_uri, text FROM markdown_headings snippet(heading_uri, 0, 120) AS paragraph FROM gql_headings" --format JsonLD \
  | jq -r '
      .[]
      | [
          .uri,
          .heading,
          (.paragraph | gsub("\n"; " ") | truncate(160))
        ]
      | @tsv
  ' \
  | column -t -s $'\t'
```

<USEFUL_FIRST_QUERIES>

-- Discover embedded RepoQL documentation
SELECT n.uri, a.headline FROM node n JOIN artifact a ON n.artifact_id = a.id
WHERE n.kind = 'document' AND n.uri LIKE 'docs://%' ORDER BY n.uri;

-- Discover repository documentation
SELECT n.uri, a.headline FROM node n JOIN artifact a ON n.artifact_id = a.id
WHERE n.kind = 'document' AND a.media_type LIKE '%markdown%' ORDER BY n.uri;

-- Full reference at docs:///sql-reference.md
-- SQL patterns at docs:///quickstart.md

</USEFUL_FIRST_QUERIES>
