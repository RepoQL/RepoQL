# Changelog

## 1.4.4

- Add multi-source git history indexing for imported repos
- Stream text search through mounted file systems with multiline regex support
- Rewrite inspect refinement with knapsack budget allocation and short headlines
- Clean up orphaned git history on import removal
- Add regex timeout protection to grep/regex read modifiers

## 1.4.1

- Add RepoQL.Analyzers with 7 code convention analyzers (RQL001–RQL007) promoted to errors
- Add cancellation-aware DuckDB read and query path
- Rewrite dashboard to Solid.js with delta streaming and performance improvements
- Add build-time `help://` snapshot for instant documentation on startup
- Replace `grep_matches`/`regex_matches` SQL macros with C# UDFs to fix OOM
- Add two-tier ONNX session management and reduce embedding log noise
- Fix zombie lock preventing host self-recovery after crash
- Fix search macro to honor `k` parameter after rescue expansion
- Fix node container key collisions in deploy script
- Handle files deleted between discovery and indexing gracefully
- Recover double-escaped JSON in `parse()` from MCP transport

## 1.4.0

- Add JSON format support with parser, pipeline, JSONC/JSON5 normalization, and secret detection
- Add embedded dashboard with real-time pipeline visualization
- Add `::host.restart`, `::reindex[scope?]`, and `::?` command discovery
- Add snapshot loader for pre-computed indexed data
- Preserve semantic completeness during async VSS rebuilds
- Push scope into search candidate generation
- Replace URI fragment filters with typed `doc_id` joins
- Fix edge fragment parsing for `snippet` and `entities` macros
- Fix silent commit skips and simplify reindex coordinator
- Surface operation summary in `::reindex` and add missing command docs

## 1.3.31

- Add `::command` framework for imperative commands in the query tool
- Add `::repo[path]` command to switch active repository
- Run structure embedding rebuilds asynchronously with coalescing
- Fix IL trimmer stripping command implementations in published builds

## 1.3.30

- Add `similar` read modifier with `find_similar()` SQL macro for semantic similarity search
- Surface repo context file (claude.md/agents.md/readme.md) in import response
- Fix MCP startup error with convert_to_json unwrap arg
- Fix Filesystems view grouping by scheme instead of per-mount
- Fix timeout cleanup for pending document catalog state
- Add shared DI memory cache and C# workspace session expiry

## 1.3.29

- Add Ruby format support with tree-sitter parsing and 12 SQL views
- Add PDF format support with text extraction, bookmarks, forms, and annotations
- Add `grep` and `regex` read modifiers with SQL macros
- Fix zombie host process blocking implicit start

## 1.3.28

- Add Word document (.docx) format support with heuristic heading detection
- Add `changes` read modifier for working copy status
- Fix reimport hang when all files are up-to-date

## 1.3.27

- Separate `explain` from `explore` as a distinct MCP tool
- Add CSV/TSV/PSV format loader with column typing and SQL macros
- Rewrite TypeScript loader with rich structure, SQL views, and type edges
- Bring PHP format up to north star with URIs and cross-format views
- Make symbol fragment matching more permissive
- Fix read() line fragments and budget recommendations
- Fix CI cross-platform build issues

## 1.3.26

- Add CLI commands for query, explore, read, and import
- Add `zoom_and_enhance` UDF for binary chop semantic refinement
- Add implicit shutdown watchdog for idle host
- Add operation-based waiting for imports with scope readiness checks
- Remove indexing barriers — health flips to SERVING immediately
- Generate structure embeddings in the hot path
- Switch bulk inserts to DuckDB Appender API
- Add markdown codeblocks view and anchor-based URI addressing

## 1.3.25

- Add read modifiers: headline, structure, tree, history, lint, find
- Add tree detail levels (folders, files, headlines)
- Add `find` modifier for semantic search within file scope
- Rename docs filesystem from repoql-docs:// to help://
- Wire UriRegistry through DI and embedding pipeline
- Add line-range-based glob pattern matching
- Add per-item timeout and slow operation warnings for indexing
- Rewrite explore and read tool descriptions

## 1.3.22

- Rename xray to explore
- Add RepoQL socket path resolver and diagnostics
- Fix host startup race condition
- Fix long path support on Windows
- Improve search resilience and globbing
- Introduce central package management

## 1.3.20

- Switch to E5 small embeddings
- Remove max results from query tool — controlled with tokens now
- Add north star documents
- Improve .NET formatting and fix MCP deserialization

## 1.3.19

- Add git history search
- Fix MCP result handling
- Add global MCP server configuration support
