# Local Semantic Search Tool — Design

## North Star

Point at a folder. Search everything in it with natural language. See results with enough context to decide. Never miss a match. No setup, no internet, no fuss.

## Context

A CLI tool for semantic search across mixed-format files (JSON, YAML, CSV, Parquet) on a developer laptop. Small scale (<1000 files), persistent incremental index, zero configuration.

**Informed by:** `references/research/exemplar.md` — embedding models for developer laptops

## Constraints

- Bundled model, works offline
- Laptop resources (CPU, 8-16GB RAM)
- Recall over precision
- Text extraction only (no structural awareness)
- Few seconds latency acceptable

---

## Components

```
┌─────────────────────────────────────────────────────────────┐
│                         CLI                                  │
│  index <folder>  |  search <query>  |  status               │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                      Orchestrator                            │
│  - Coordinates indexing pipeline                            │
│  - Coordinates search pipeline                              │
└─────────────────────────────────────────────────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────┐      ┌─────────────┐      ┌─────────────┐
│ FileScanner │      │  Embedder   │      │   Index     │
│             │      │             │      │             │
│ - Discover  │      │ - ONNX      │      │ - SQLite    │
│ - Hash      │      │ - MiniLM    │      │ - Store     │
│ - Detect Δ  │      │ - Batch     │      │ - Search    │
└─────────────┘      └─────────────┘      └─────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────┐
│                    TextExtractors                            │
│  JsonExtractor | YamlExtractor | CsvExtractor | ParquetEx   │
└─────────────────────────────────────────────────────────────┘
```

---

## Contracts

### ITextExtractor

```csharp
public interface ITextExtractor
{
    bool CanHandle(string filePath);
    IEnumerable<TextChunk> Extract(string filePath);
}

public record TextChunk(
    string Content,
    int StartOffset,
    int EndOffset
);
```

**Implementations:**
- `JsonExtractor` — Reads JSON, serializes to indented string, chunks
- `YamlExtractor` — Reads as text, chunks
- `CsvExtractor` — Reads rows, prepends headers to each chunk
- `ParquetExtractor` — Converts to row text via ParquetSharp, chunks

### IChunker

```csharp
public interface IChunker
{
    IEnumerable<TextChunk> Chunk(string text, ChunkOptions options);
}

public record ChunkOptions(
    int MaxTokens = 500,
    float OverlapRatio = 0.2f
);
```

**Implementation:** `OverlappingChunker` — Fixed-size with configurable overlap.

### IEmbedder

```csharp
public interface IEmbedder
{
    float[] Embed(string text);
    float[][] EmbedBatch(string[] texts);
}
```

**Implementation:** `OnnxEmbedder`
- Loads `all-MiniLM-L6-v2.onnx` from embedded resource
- Uses `Microsoft.ML.OnnxRuntime`
- Tokenizes with bundled vocabulary
- Returns 384-dimensional float array

### IIndex

```csharp
public interface IIndex
{
    void Upsert(IndexedChunk chunk);
    void DeleteByFile(string filePath);
    IEnumerable<SearchResult> Search(float[] queryEmbedding, int topK);
    IndexStats GetStats();
}

public record IndexedChunk(
    string FilePath,
    string FileHash,
    int ChunkIndex,
    string Content,
    float[] Embedding
);

public record SearchResult(
    string FilePath,
    int ChunkIndex,
    string Content,
    float Score
);

public record IndexStats(
    int FileCount,
    int ChunkCount,
    DateTime LastUpdated
);
```

**Implementation:** `SqliteIndex`
- Database at `~/.semsearch/index.db`
- Vectors stored as BLOBs
- Brute-force cosine similarity (acceptable at <1000 files)

### IFileScanner

```csharp
public interface IFileScanner
{
    IEnumerable<ScannedFile> Scan(string folderPath);
}

public record ScannedFile(
    string Path,
    string Hash,
    bool IsNew,
    bool IsModified,
    bool IsDeleted
);
```

**Implementation:** `HashingFileScanner`
- Computes SHA256 of file content
- Compares against stored hashes in index
- Returns delta (new, modified, deleted files)

---

## Data Flow

### Index Command

```
index <folder>
    │
    ▼
FileScanner.Scan(folder)
    │
    ├─► For deleted files:
    │       Index.DeleteByFile(path)
    │
    ├─► For new/modified files:
    │       extractor = FindExtractor(path)
    │       chunks = extractor.Extract(path)
    │       embeddings = Embedder.EmbedBatch(chunks.Select(c => c.Content))
    │       for each (chunk, embedding):
    │           Index.Upsert(new IndexedChunk(...))
    │
    ▼
Print summary: "Indexed 42 files (3 new, 2 modified, 1 deleted)"
```

### Search Command

```
search <query>
    │
    ▼
queryEmbedding = Embedder.Embed(query)
    │
    ▼
results = Index.Search(queryEmbedding, topK: 10)
    │
    ▼
For each result:
    Print "[{score:F2}] {filePath} (chunk {chunkIndex})"
    Print "    ...{contextSnippet}..."
```

---

## CLI Specification

```
semsearch index <folder> [--force]
    Index or update the search index for <folder>
    --force: Re-index all files, ignore cache

    Output:
    Scanning... 150 files
    Indexing... ████████████████████ 100%
    Done: 150 files indexed (12 new, 3 modified, 0 deleted)

semsearch search <query> [--top <n>] [--json]
    Search the index with natural language query
    --top: Number of results (default: 10)
    --json: Output as JSON for scripting

    Output:
    [0.87] ./data/users.json (chunk 3)
        ..."email": "jane@example.com", "role": "admin"...

    [0.82] ./config/settings.yaml (chunk 1)
        ...admin_email: jane@example.com...

semsearch status
    Show index statistics

    Output:
    Index: ~/.semsearch/index.db
    Files: 150
    Chunks: 1,247
    Last updated: 2026-01-19 10:30:00
```

---

## SQLite Schema

```sql
CREATE TABLE files (
    path TEXT PRIMARY KEY,
    hash TEXT NOT NULL,
    indexed_at INTEGER NOT NULL
);

CREATE TABLE chunks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE,
    chunk_index INTEGER NOT NULL,
    content TEXT NOT NULL,
    embedding BLOB NOT NULL,
    start_offset INTEGER NOT NULL,
    end_offset INTEGER NOT NULL,
    UNIQUE(file_path, chunk_index)
);

CREATE INDEX idx_chunks_file ON chunks(file_path);
```

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Malformed JSON/YAML | Log warning, skip file, continue |
| Unreadable file (permissions) | Log warning, skip file, continue |
| Unknown file extension | Skip silently |
| Empty file | Skip silently |
| Index database locked | Retry 3x with backoff, then fail |
| Out of memory during embedding | Reduce batch size, retry |

Summary printed at end: "Completed with 3 warnings (see ~/.semsearch/log)"

---

## Configuration

Default: zero config. Optional `~/.semsearch/config.yaml`:

```yaml
# All optional, sensible defaults
model: all-MiniLM-L6-v2          # or path to custom ONNX
chunk_size: 500                   # tokens
chunk_overlap: 0.2                # ratio
top_k: 10                         # default results
extensions:                       # additional extractors
  - .txt
  - .md
```

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| MiniLM (22MB) | EmbeddingGemma (200MB) | Bundle size; quality adequate |
| SQLite BLOBs | Vector database | Simplicity; scale is small |
| Text extraction | Structural parsing | Complexity elimination |
| Explicit index | File watching | Predictability |
| Fixed chunking | Semantic chunking | Simplicity; overlap compensates |
| Embedded model | External service | Zero config requirement |

## Alternatives Considered

**Ollama integration:** Model flexibility but breaks "no setup" constraint.

**Vector database (Milvus, Qdrant):** Overkill at <1000 files; adds deployment complexity.

**Structural search:** JSON path, CSV columns. Adds complexity without matching north star.

## Risks

| Risk | Mitigation |
|------|------------|
| Cosine similarity slow at scale | Document <1000 file limit; upgrade path is ANN index |
| Model too large | MiniLM at 22MB is conservative |
| Chunk boundaries miss matches | Overlap; tunable if needed |

## Extension Points

- `ITextExtractor` — Add formats without core changes
- Model path configurable — Power users can swap models
- `--json` output — Scripting support built in
