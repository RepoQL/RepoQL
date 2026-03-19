# Changelog

## 1.5.2

- Switch embedding model from voyage-context-3 to voyage-4-lite (60x faster, 9x cheaper, same 1024 dimensions)
- Add accurate Voyage token counting using embedded BPE tokenizer (Qwen2, 151K vocab) for contextual group splitting
- Add `safe_cosine` SQL macro to handle mixed-dimension embeddings without crashing
- Normalize Voyage 4 model family names so cache treats voyage-4-lite/voyage-4/voyage-4-large as interchangeable
- Fix contextual embedding error handling: connection failures disable cloud for the run, payload failures skip only the current batch
- Fix parse command URI canonicalization on Windows (absolute paths now correctly relativized)
- Fix pipeline error propagation in DocumentPreviewService (errors no longer silently swallowed)
- Fix JWT issuer validation for custom AuthKit domain (prefix matching instead of exact)
- Disable JWT audience validation (WorkOS access tokens don't include an aud claim)
- Remove stale `intent=` parameter from explore suggestions in MCP system prompt and error messages
- Reorganize help:// docs from nested repoql/ prefix to flat layout (commands/, formats/, schema/, etc.)
- Add PHP, TypeScript format docs and per-function/per-view documentation
- Consolidate Claude Code skills: remove old code-intelligence/sql-expert/writing-documents, add effective-markdown
- Remove repo_index SQL view (replaced by UDF-based diagnostics)
- Add queue observability tests
- Delete unused repoql-embedding and repoql-inference Cloud Run services (consolidated into repoql-cloud)
- Fix stale proto comment for rerank model default (rerank-2.5, not lite)
- Bump version to 1.5.2

## 1.4.10

- Simplify search SQL: centralize scope filtering into `_scope_filter` macro, eliminate `repo_index` from hot path
- Replace Intent enum with continuous breadth parameter (1-10) for explore tool
- Add tree-sitter C# parser prototype with query-based extraction
- Add Voyage AI reranking and contextual embedding support
- Consolidate codex skills into unified effective-delegation skill
- Fix `repo_index` non-deterministic `DISTINCT ON` with `QUALIFY ROW_NUMBER()`
- Fix `search()` case-sensitive LIKE scope filtering (now ILIKE)
- Fix DuckDB macro parameter/column name collision (`scope` → `node_scope`)
- Fix config command reset test expecting `<not set>` instead of default value
- Add research docs: tree-sitter, Roslyn performance, reranking landscape, embedding hosting

## 1.4.8

- Migrate PHP format from ANTLR to tree-sitter with zero-dependency native parsing
- Wire single-pass combined query for Go format (1 tree walk instead of 14)
- Fix generic receiver type stripping so methods on `Set[T]` associate with struct `Set`
- Add Cloud Run deployment for embedding service
- Default remote embedding URL to Cloud Run endpoint
- Fix container base image and Cloud Run startup binding

## 1.4.7

- Add SARIF import with producer normalization, annotation replacement, and `help://` docs
- Add `::memory` command with host-side memory UDFs and GC tuning
- Add `snippet_glob` UDF for code preview across URI patterns
- Add TrustSignal status footer with percentage progress, failed/stale counts, and NOT READY guard
- Split `RepoQL.Explore` into `RepoQL.Explore` + `RepoQL.Read` for cleaner project boundaries
- Remove ~4,100 lines of dead allocation and refinement code from Explore
- Add linux-arm64 to native publish workflow
- Fix DuckDB-managed deadlock via reentrant exclusive section
- Fix SQL errors returned as success to prevent parallel tool call cancellation
- Fix phantom ART index constraint violations on container_uri_lowercase

## 1.4.5

- Add file detail panel to dashboard with headline, metrics, timing, symbols, and error display
- Flow x-ray headline and structure from artifact table through UriRegistry to dashboard API
- Add source tabs to switch between local and imported repos in the dashboard
- Add structure tooltip on hover and detail flyout on click
- Fix anonymous type trimming in dashboard endpoints with Dictionary-based DTOs

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
