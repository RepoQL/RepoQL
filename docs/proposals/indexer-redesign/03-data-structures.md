# Data Structures

## IndexItem Flow Object

The central object that flows through the entire pipeline, accumulating state at each stage.

```csharp
namespace RepoQL.Core;

/// <summary>
/// A single flow object that accumulates state as it moves through the indexing pipeline.
/// Only the active stage mutates its fields. Enables easier testing and observability.
/// </summary>
public sealed class IndexItem
{
    // ─── Identity ───────────────────────────────────────────────────────

    /// <summary>Repository URI (e.g., "file:///src/Foo.cs")</summary>
    public required string Uri { get; init; }

    /// <summary>Absolute file system path</summary>
    public required string Path { get; init; }

    // ─── Discovery/Classification Stage ────────────────────────────────

    /// <summary>File size in bytes</summary>
    public long Size { get; set; }

    /// <summary>File modification time</summary>
    public DateTimeOffset MTime { get; set; }

    /// <summary>Provisional media type from file classifier</summary>
    public string? ProvisionalType { get; set; }

    /// <summary>Content digest (e.g., "xxh64:abc123...")</summary>
    public string? Digest { get; set; }

    // ─── Parsing Stage ──────────────────────────────────────────────────

    /// <summary>Resolved semantic media type</summary>
    public string? MediaType { get; set; }

    /// <summary>In-memory document model from loader</summary>
    public DocumentModel? Document { get; set; }

    /// <summary>Materialized graph records (artifacts, nodes, spans, edges)</summary>
    public Records? Records { get; set; }

    // ─── Writer Stage ───────────────────────────────────────────────────

    /// <summary>Timestamp when document was committed to database</summary>
    public DateTimeOffset? CommittedAt { get; set; }

    // ─── First-Pass Analysis Stage ──────────────────────────────────────

    /// <summary>Annotations produced by single-file analyzers</summary>
    public List<Annotation> FirstPassAnnotations { get; } = new();

    // ─── Stage Scratchpad ───────────────────────────────────────────────

    /// <summary>Free-form storage for stage-specific data</summary>
    public Dictionary<string, object> Bag { get; } = new();
}
```

### Lifecycle Example

```csharp
// Created by discovery
var item = new IndexItem {
    Uri = "file:///src/UserService.cs",
    Path = @"C:\repo\src\UserService.cs"
};

// Discovery stage
item.Size = 4521;
item.MTime = DateTimeOffset.Parse("2025-01-15T10:30:00Z");
item.Digest = "xxh64:a1b2c3d4e5f6";
item.ProvisionalType = "csharp.compilation_unit";

// Parsing stage
item.MediaType = "text/x-csharp;kind=csharp.compilation_unit";
item.Document = await loader.LoadAsync(...);
item.Records = materializer.Materialize(item.Document);

// Writer stage
item.CommittedAt = DateTimeOffset.UtcNow;

// First-pass analysis
item.FirstPassAnnotations.Add(new Annotation {
    Kind = "style",
    Severity = "warning",
    Message = "Method 'GetUser' should be async"
});
```

### Benefits

1. **Testing:** Can inspect state at any stage
   ```csharp
   [Test]
   public async Task Discovery_ComputesDigest() {
       var item = new IndexItem { Uri = "file:///test.cs", Path = "test.cs" };
       await discoveryWorker.ProcessAsync(item);
       Assert.NotNull(item.Digest);
       Assert.StartsWith("xxh64:", item.Digest);
   }
   ```

2. **Observability:** Single trace ID through pipeline
   ```csharp
   using var activity = Activity.StartActivity("repoql.index");
   activity?.SetTag("url.full", item.Uri);
   // Pass item through stages
   activity?.SetTag("repoql.nodes.count", item.Records?.Nodes.Count ?? 0);
   ```

3. **Debugging:** Inspect complete state at any point
   ```csharp
   // Breakpoint in writer
   Console.WriteLine($"Processing {item.Uri}:");
   Console.WriteLine($"  Digest: {item.Digest}");
   Console.WriteLine($"  Media: {item.MediaType}");
   Console.WriteLine($"  Nodes: {item.Records?.Nodes.Count}");
   ```

## Database Schema

### New Tables

#### batch_state

Tracks last run time for idle-time batches. Simple, single-row-per-batch approach.

```sql
CREATE TABLE IF NOT EXISTS batch_state (
  name VARCHAR PRIMARY KEY,          -- 'embeddings', 'semantic_analysis'
  last_run_at TIMESTAMP NOT NULL,    -- Last successful completion
  started_at TIMESTAMP,              -- Optional: when current batch started
  batch_id UUID                      -- Optional: current batch identifier
);

-- Initial values
INSERT INTO batch_state (name, last_run_at) VALUES
  ('embeddings', '1970-01-01 00:00:00'),
  ('semantic_analysis', '1970-01-01 00:00:00')
ON CONFLICT DO NOTHING;
```

**Usage:**
```sql
-- Get last run
SELECT last_run_at FROM batch_state WHERE name='embeddings';

-- Update after successful batch
UPDATE batch_state SET last_run_at = NOW() WHERE name='embeddings';

-- Optional: Mark batch start
UPDATE batch_state SET started_at = NOW(), batch_id = gen_random_uuid()
WHERE name='embeddings';
```

#### node_embedding

Stores embeddings for individual code nodes (methods, classes, headings, etc.).

```sql
CREATE TABLE IF NOT EXISTS node_embedding (
  node_id UUID PRIMARY KEY,
  uri VARCHAR NOT NULL,              -- Parent document URI (for convenience)
  kind VARCHAR NOT NULL,             -- Node kind (e.g., 'cs_method', 'md_heading')
  start_line INTEGER,                -- For display/linking
  end_line INTEGER,
  model VARCHAR NOT NULL,            -- Embedding model (e.g., 'bge-small-en-v1.5')
  dim INTEGER NOT NULL,              -- Embedding dimension (e.g., 384)
  embedding VARCHAR NOT NULL,        -- JSON array of floats
  updated_at TIMESTAMP NOT NULL
);

CREATE INDEX IF NOT EXISTS node_embedding_uri_idx ON node_embedding(uri);
CREATE INDEX IF NOT EXISTS node_embedding_kind_idx ON node_embedding(kind);
CREATE INDEX IF NOT EXISTS node_embedding_model_idx ON node_embedding(model);
CREATE INDEX IF NOT EXISTS node_embedding_updated_at_idx ON node_embedding(updated_at);
```

**Example rows:**
```sql
-- Method embedding
INSERT INTO node_embedding VALUES (
  '550e8400-e29b-41d4-a716-446655440000',           -- node_id
  'file:///src/UserService.cs',                     -- uri
  'cs_method',                                      -- kind
  42,                                               -- start_line
  57,                                               -- end_line
  'bge-small-en-v1.5',                              -- model
  384,                                              -- dim
  '[0.123, -0.456, 0.789, ...]',                    -- embedding (JSON)
  '2025-01-15 10:30:45'                             -- updated_at
);

-- Heading embedding
INSERT INTO node_embedding VALUES (
  '660e8400-e29b-41d4-a716-446655440001',
  'file:///docs/Architecture.md',
  'md_heading',
  10,
  10,
  'bge-small-en-v1.5',
  384,
  '[0.234, -0.567, 0.890, ...]',
  '2025-01-15 10:31:02'
);
```

### Modified Tables

**No changes to core tables!** The following existing tables are used as-is:
- `artifact` - stores `updated_at` (already present, used for dirty tracking)
- `node` - unchanged
- `span` - unchanged
- `edge` - unchanged
- `annotation` - unchanged
- `document_embedding` - already exists, unchanged

### Schema Diagram

```
┌────────────────┐
│  batch_state   │  (NEW)
├────────────────┤
│ name PK        │
│ last_run_at    │
│ started_at     │
│ batch_id       │
└────────────────┘

┌──────────────────┐
│ node_embedding   │  (NEW)
├──────────────────┤
│ node_id PK       │
│ uri              │───┐
│ kind             │   │
│ start_line       │   │
│ end_line         │   │
│ model            │   │
│ dim              │   │
│ embedding (JSON) │   │
│ updated_at       │   │
└──────────────────┘   │
                       │
┌──────────────────────┴─────┐
│ node (existing)            │
├────────────────────────────┤
│ id PK                      │
│ kind                       │
│ uri                        │
│ artifact_id FK ─────┐      │
│ span_id FK          │      │
│ properties          │      │
└─────────────────────┼──────┘
                      │
┌─────────────────────┴──────┐
│ artifact (existing)        │
├────────────────────────────┤
│ id PK                      │
│ digest                     │
│ media_type                 │
│ content                    │
│ updated_at ◄───────────────┼─── Used for dirty tracking
│ text_content               │
│ headline                   │
│ summary                    │
│ structure                  │
└────────────────────────────┘

┌────────────────────────┐
│ document_embedding     │  (existing, unchanged)
├────────────────────────┤
│ doc_id PK              │
│ model                  │
│ dim                    │
│ embedding (JSON)       │
│ updated_at             │
└────────────────────────┘
```

## Query Patterns

### Find Documents Needing Embeddings

```sql
-- All documents changed since last embedding run
SELECT n.id, a.text_content
FROM node n
JOIN artifact a ON a.id = n.artifact_id
WHERE n.kind = 'document'
  AND a.text_content IS NOT NULL
  AND a.updated_at > (
    SELECT last_run_at FROM batch_state WHERE name='embeddings'
  );
```

### Find Nodes Needing Embeddings

```sql
-- All code nodes changed since last embedding run
SELECT
  n.id AS node_id,
  n.uri,
  n.kind,
  s.start_line,
  s.end_line,
  SUBSTR(a.text_content, s.start_byte, s.end_byte - s.start_byte) AS text
FROM node n
JOIN artifact a ON n.artifact_id = a.id
JOIN span s ON n.span_id = s.id
WHERE n.kind IN (
  'cs_class', 'cs_method', 'cs_interface', 'cs_property',
  'md_heading', 'md_code_block',
  'graphql_type', 'graphql_field'
)
AND a.updated_at > (
  SELECT last_run_at FROM batch_state WHERE name='embeddings'
);
```

### Find Documents Needing Semantic Analysis

```sql
-- All C# documents changed since last semantic analysis
SELECT n.uri
FROM node n
JOIN artifact a ON a.id = n.artifact_id
WHERE n.kind = 'document'
  AND a.media_type LIKE '%csharp%'
  AND a.updated_at > (
    SELECT last_run_at FROM batch_state WHERE name='semantic_analysis'
  )
LIMIT 5000;  -- Batch size limit
```

### Search Node Embeddings (Semantic Code Search)

```sql
-- Find methods semantically similar to query
WITH query_vec AS (
  SELECT embed_text_json(
    'Represent this sentence for searching relevant passages: validate JWT token'
  ) AS qvec
)
SELECT
  ne.node_id,
  ne.uri,
  ne.kind,
  ne.start_line,
  ne.end_line,
  cosine_similarity_json(q.qvec, ne.embedding) AS similarity
FROM query_vec q
CROSS JOIN node_embedding ne
WHERE ne.kind IN ('cs_method', 'cs_class')
ORDER BY similarity DESC
LIMIT 50;
```

### Combined Document + Node Search

```sql
-- Search both files and code nodes
WITH query_vec AS (
  SELECT embed_text_json(
    'Represent this sentence for searching relevant passages: authentication'
  ) AS qvec
),
doc_results AS (
  SELECT
    'document' AS result_type,
    de.doc_id AS id,
    n.uri,
    NULL AS kind,
    NULL AS start_line,
    cosine_similarity_json(q.qvec, de.embedding) AS similarity
  FROM query_vec q
  CROSS JOIN document_embedding de
  JOIN node n ON n.id = de.doc_id
),
node_results AS (
  SELECT
    'node' AS result_type,
    ne.node_id AS id,
    ne.uri,
    ne.kind,
    ne.start_line,
    cosine_similarity_json(q.qvec, ne.embedding) AS similarity
  FROM query_vec q
  CROSS JOIN node_embedding ne
)
SELECT * FROM doc_results
UNION ALL
SELECT * FROM node_results
ORDER BY similarity DESC
LIMIT 50;
```

## Serialization

### Embedding JSON Format

Embeddings are stored as JSON arrays for portability (no VSS extension required):

```csharp
// Serialize
public static string SerializeFloatArray(float[] vector) {
    return JsonSerializer.Serialize(vector);
    // Result: "[0.123,-0.456,0.789,...]"
}

// Deserialize (in SQL)
SELECT from_json(embedding, 'LIST<FLOAT>') FROM document_embedding;
```

**Format details:**
- UTF-8 encoded JSON
- No whitespace (compact)
- Float precision: ~6 decimal places (sufficient for embeddings)
- Size: ~15 bytes per dimension (384 dims × 15 bytes ≈ 5.8 KB per embedding)

### Records Structure

Unchanged from current implementation. See `Records.cs`:

```csharp
public sealed class Records {
    public List<Artifact> Artifacts { get; init; } = new();
    public List<Node> Nodes { get; init; } = new();
    public List<Span> Spans { get; init; } = new();
    public List<Edge> Edges { get; init; } = new();
    public List<Annotation> Annotations { get; init; } = new();
}
```

## Memory Management

### IndexItem Lifecycle

```csharp
// Created by discovery
var item = new IndexItem { ... };

// Flows through queues (references held)
await _parsingQueue.EnqueueAsync(item, ct);
await _writerQueue.EnqueueAsync(item, ct);
await _enrichmentQueue.EnqueueAsync(item, ct);

// After first-pass analysis completes, eligible for GC
// Large objects (Document, Records) can be cleared if needed:
item.Document = null;  // Allow early GC of parsed document
item.Records = null;   // Allow early GC of materialized records
```

**Pressure points:**
- `IndexItem.Document` - can be large (Roslyn syntax trees)
- `IndexItem.Records` - can be large (thousands of nodes)
- Solution: Clear after writer commits if needed

### Batch State Memory

```csharp
// Batch state is small (single row per batch type)
// Kept in memory: NO
// Queried on-demand: YES
var lastRun = db.QuerySingle<DateTime>(
    "SELECT last_run_at FROM batch_state WHERE name='embeddings'");
```

**No in-memory caching needed** - database query is fast (indexed PK lookup).

## Migration

### Adding New Tables

```sql
-- Run during startup or migration
CREATE TABLE IF NOT EXISTS batch_state (...);
CREATE TABLE IF NOT EXISTS node_embedding (...);

-- Initialize batch state if empty
INSERT INTO batch_state (name, last_run_at)
VALUES ('embeddings', '1970-01-01'), ('semantic_analysis', '1970-01-01')
ON CONFLICT DO NOTHING;
```

### Backward Compatibility

**Schema changes are additive:**
- ✅ New tables (batch_state, node_embedding)
- ✅ No modifications to existing tables
- ✅ No breaking changes to existing queries

**Rollback safety:**
- Old code ignores new tables
- New tables can be dropped without affecting core functionality
- Embeddings can be disabled via `REPOQL_EMBED_ENABLED=0`
