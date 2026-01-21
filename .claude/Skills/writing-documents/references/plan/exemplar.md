# Plan: FileScanner Component

Implements: [Semantic Search Design](../design/exemplar.md) — FileScanner, TextExtractors, Chunker

## Scope

**Covers:**
- `IFileScanner` interface and `HashingFileScanner` implementation
- `ITextExtractor` interface with JSON, YAML, CSV, Parquet implementations
- `IChunker` interface and `OverlappingChunker` implementation
- File hash persistence in SQLite `files` table

**Does not cover:**
- Embedding generation (Plan: Embedder Integration)
- Vector storage and similarity search (Plan: Index Component)
- CLI commands and user interaction (Plan: CLI Shell)

## Enables

Once FileScanner exists:
- **Incremental indexing** — only process files that changed, not the entire folder every time
- **Change detection** — new, modified, and deleted files identified in a single scan
- **Plan: Embedder Integration** can proceed — consumes `TextChunk` output from extractors
- **Plan: Index Component** can proceed — stores chunks with file path and hash metadata

This is the foundation layer. All downstream plans depend on it.

## Prerequisites

- SQLite database initialized with schema from design (`files` and `chunks` tables)
- .NET project structure with dependency injection configured
- [ParquetSharp](https://github.com/G-Research/ParquetSharp) available — `G-Research.ParquetSharp` NuGet package

## North Star

Point at a folder, get back exactly what changed — no false positives, no missed changes. Complete scan of 1000 files in under 2 seconds on commodity laptop hardware.

## Done Criteria

### FileScanner
- The FileScanner shall recursively discover files with supported extensions
  - When folder contains no supported files, return empty collection
  - When file extension has no registered extractor, skip silently
- The FileScanner shall compute SHA256 hash of each file's contents
- The FileScanner shall classify files by comparing against stored hashes
  - New: file exists on disk, no record in store
  - Modified: file exists, hash differs from stored hash
  - Deleted: record exists in store, file missing from disk

### TextExtractors
- The extractor registry shall resolve extractors by file extension
- Each extractor shall implement `ITextExtractor` with `CanHandle` and `Extract`
- The JsonExtractor shall deserialize and re-serialize as indented text
- The YamlExtractor shall extract file content as plain text
- The CsvExtractor shall prepend column headers to each row's text
- The ParquetExtractor shall convert rows to text via ParquetSharp
  - If parsing fails, throw `ExtractionException` with file path and inner exception

### Chunker
- The OverlappingChunker shall split text into chunks within configured token limit
- The OverlappingChunker shall overlap consecutive chunks by configured ratio
- The OverlappingChunker shall prefer splitting at whitespace boundaries
  - When text is shorter than limit, return single chunk
- Each chunk shall include start and end character offsets for source mapping

## Constraints

- **No MIME sniffing** — file extension determines extractor; design chose simplicity over magic byte detection
- **Text extraction only** — no JSON path queries, CSV column filtering, or structural awareness; design explicitly traded structure for simplicity
- **Standard library hashing** — use `System.Security.Cryptography.SHA256`; no external dependencies for this

## References

- [Semantic Search Design](../design/exemplar.md) — component contracts, data flow diagrams, error handling table
- [ParquetSharp](https://github.com/G-Research/ParquetSharp) — `G-Research.ParquetSharp`, see `ParquetFileReader` for row-by-row reading

## Error Policy

Errors should not halt the scan. When a file cannot be read or parsed:
1. Log warning with file path and error details
2. Skip the file
3. Continue processing remaining files
4. Include warning count in final summary

This aligns with north star — users expect the tool to do its best, not fail on first problem.
