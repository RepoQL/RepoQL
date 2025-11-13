# Proposal: Ast-grep Integration for RepoQL Enrichment

> **Note:** At the time this was written the `RepositoryIndexer` host owned the enrichment pipeline. Those responsibilities now live in the `RepoqlHost` + `IndexingCoordinator` + `IndexingEngine` stack.

## Summary
RepoQL should ingest ast-grep findings during enrichment so repositories can define structural linting rules that surface alongside existing annotations. We will execute ast-grep through the CLI (the recommended integration point) while auto-detecting repositories that already maintain `sgconfig.yml`. The design keeps RepoQL’s batch pipeline intact, allows future adoption of the ast-grep LSP or Rust APIs, and positions us to reuse the same infrastructure for other structural tools.

## Background
- RepoQL today parses files into format-specific artifacts (Markdown, csproj, plain text, etc.), enriches them with analyzers, and persists annotations in DuckDB (see `src/RepoQL.Core/RepositoryIndexer.cs:915` and `src/RepoQL.Core/Analysis/AnnotationResultWriter.cs`).
- Users can only rely on built-in analyzers; they cannot author repository-specific rules that share the same query surface.
- ast-grep already supports syntactic pattern matching and rewriting across many languages and has an established CLI workflow (`ast-grep run/scan`, YAML rules, fix transforms). The project explicitly states the CLI is the primary automation surface; the Rust crate exists but “usually you will only need ast-grep CLI instead of this crate” (ast-grep core docs).
- ast-grep also ships an LSP aimed at editors, exposing diagnostics and code actions via `textDocument/*` notifications (editor integration guide). While powerful, it expects incremental document streams rather than bulk scans.

## Goals
1. Execute ast-grep rules automatically during repo enrichment and write results as RepoQL annotations, honoring `.editorconfig` severity overrides and autofix flags.
2. Discover and reuse any existing ast-grep project the repository already maintains (e.g., root `sgconfig.yml`, `.astgrep/sgconfig.yml`) before falling back to a RepoQL-managed config under `.repoql/ast-grep`.
3. Allow repository owners to drop additional rules into a well-known location and run `ast-grep test` locally, without bespoke RepoQL-specific authoring.
4. Emit structured metadata (captures, fix suggestions) so annotations participate in current dashboards, X-Ray summaries, and future agent tooling.
5. Lay groundwork for other structural tools (e.g., Semgrep, Biome) by establishing a clear analyzer boundary and execution runner concept.

## Non-goals
- Replace existing format analyzers or overhaul materialization.
- Deliver real-time diagnostics or editor integrations in this iteration.
- Build a .NET binding around `ast-grep-core` or ship an embedded LSP client; those remain future options once the CLI path is stable.

## Approach Overview
1. **Rule discovery.** Detect ast-grep configuration in this order, preferring user-owned projects:
   - `<repo-root>/sgconfig.yml`
   - `<repo-root>/.astgrep/sgconfig.yml`
   - `.repoql/ast-grep/sgconfig.yml`
   - Synthesized config pointing to `.repoql/ast-grep/rules/`
   The discovery returns a root path and config file for subsequent scans.
2. **Rule catalog.** Parse rule files once at startup (and on demand later) to capture rule ids, languages, default severity/autofix metadata, and filesystem paths. Expose APIs to retrieve rules per language or by id.
3. **Analyzer integration.** Introduce `AstGrepAnalyzer : IFormatAnalyzer` that:
   - Supports documents whose `SemanticMediaType` maps to an ast-grep language alias (e.g., `.cs` → `CSharp`).
   - For each document, resolves applicable rules, combines repository `.editorconfig` overrides with rule metadata, and invokes ast-grep via the CLI to obtain structured matches (`--json`).
   - Converts matches to `AnalysisResult` (namespaced rule ids `astgrep/<id>`, semantic key, severity, target region) and optional `AnalysisFix` entries when autofix is allowed.
   - Implements `AnalyzeEmbeddedAsync` so Markdown code blocks inherit linting based on fenced language labels (re-using the current embedded analyzer pipeline).
4. **Execution runner.** Wrap CLI invocation in `AstGrepRunner` that:
   - Locates the ast-grep binary (`REPOQL_AST_GREP_PATH` override, then PATH).
   - Executes `ast-grep scan --config <sgconfig> --filter <regex>` against specific files, using `--json=stream` for machine-readable output.
   - Enforces a concurrency limit (default four workers, tunable via config).
   - Surfaces process failures once per rule/rule-set in RepoQL logs without failing the enrichment pipeline.
5. **Service registration.** Modify `AddRepoIndexer` to:
   - Register the catalog, runner, and analyzer as singletons.
   - Replace format descriptors (`FormatDescriptor` in `RepoIndexerServiceCollectionExtensions.cs`) with a composite analyzer (`CompositeAnalyzer(markdownAnalyzer, astGrepAnalyzer)`, etc.), ensuring ast-grep runs alongside existing analyzers.
6. **Annotation mapping.** Extend `AnalysisResult` generation to include:
   - `Data` payload with `captures`, `rulePath`, and match metadata.
   - `AnalysisTarget` derived from line/column offsets (using `DocumentModel.LineMap`).
   - `AnalysisFix` records translating ast-grep replacements into RepoQL spans when `.editorconfig` allows autofix.
7. **Telemetry.** Emit counters/timers (e.g., matches per rule, scan duration, failure count) via the existing `IndexingMetrics` meter to monitor overhead and success rate.

## LSP & Rust Library Considerations
- **LSP potential.** Implementing an LSP client would eventually let RepoQL stream diagnostics and code actions from many tools (eslint, Biome, Pyright, ast-grep, etc.) in real time. However, it requires managing `textDocument/didOpen/didChange/didClose`, in-memory buffers, cancellation, and server lifecycles—significant groundwork beyond our current batch pipeline. We propose deferring this until there is a clear real-time use case, at which point the ast-grep analyzer could optionally reuse the same client.
- **Rust binding.** Directly consuming `ast-grep-core` would avoid process spawning but demands maintaining a .NET binding, shipping tree-sitter grammars, and tracking ABI changes. The ast-grep documentation advises most consumers to stick with the CLI. We can revisit this optimization if process overhead becomes a bottleneck and we are prepared to invest in managed/native interop.

## Detailed Design

### Rule Catalog & Discovery
- Catalog initialization:
  - Walk configured directories (`sgconfig.yml` includes `ruleDirs`, `utilDirs`).
  - Parse YAML to extract `id`, `language`, `metadata.repoql`, `fix` presence.
  - Persist in-memory structures keyed by id and language.
- Auto-refresh strategy (phase 2): watch `.repoql/ast-grep` with `FileSystemWatcher` to invalidate caches when rules change.
- Validation: log warnings (not errors) for malformed rules; skip during scanning.
- Discovery precedence and opt-out:
  1. `<repo-root>/sgconfig.yml`
  2. `<repo-root>/.astgrep/sgconfig.yml`
  3. `.repoql/ast-grep/sgconfig.yml`
  4. Synthesised config targeting `.repoql/ast-grep/rules/`
- Respect `REPOQL_AST_GREP_ENABLED=false` to disable globally (surface as informational metric only; no annotations emitted). Individual rules can be disabled via `.editorconfig` (`repoql.analyzer.astgrep/<ruleId>.severity = none`).

### Analyzer Flow
1. On enrichment, RepoQL obtains the persisted document or reloads through `IAnalysisWorkspace`.
2. `AstGrepAnalyzer` checks catalog for rules matching `document.MediaType`.
3. Determine severity using a single mapping table (see below). Apply `.editorconfig` overrides last; default to rule metadata when unspecified. Skip matches whose effective severity is `None`.
4. Invoke `AstGrepRunner` to scan the document path with the filtered rule set.
5. Parse each JSON match:
   - Convert ast-grep positions (1-based, column) into RepoQL conventions: store `AnalysisRegion.StartLine/EndLine` as 1-based line numbers; compute `StartChar/EndChar` as 0-based UTF-8 byte offsets with CRLF normalization. Always compose RepoURIs via helper APIs (`RepoUri.FromLines`, etc.) to preserve casing and formatting.
   - Populate `AnalysisFix` if CLI output includes rewrite text and autofix enabled.
   - Augment `AnalysisResult.Data` with captures and diagnostic info.
6. Compute deterministic `SemanticKey` as `lint:astgrep:{rule_id}:{container_uri_lower}:{start_line}:{end_line}:{sha1(core-captures)}` where `core-captures` is a sorted JSON encoding of the main capture set. This guarantees idempotent upserts across runs.
7. Aggregate results per document and hand to `AnnotationResultWriter`.

### Execution Runner
- Accepts rule ids and file path.
- Builds command:
  ```
  ast-grep scan \
    --config "<sgconfigPath>" \
    --filter "^(rule1|rule2)$" \
    --json=stream \
    --no-ignore hidden \
    "<filePath>"
  ```
- Captures stdout/stderr, logs stderr on failure, returns parsed matches.
- Uses `SemaphoreSlim` to limit concurrent processes (default 4, configurable) and enforces a per-process timeout (default 30s). Cancels via `_stopping.Token`.
- If the ast-grep binary is missing or exits non-zero, emit a warning once per run, record metrics, and skip producing annotations rather than failing the pipeline.
- Metrics (`IndexingMetrics`): scan duration, matches per rule, failure counts, timeout occurrences.

### Configuration & Opt-out
- Add environment flag `REPOQL_AST_GREP_ENABLED` (default on). When false, the analyzer short-circuits.
- Support `.editorconfig` severity override to disable individual rules (set severity to `none`).
- Provide CLI command (`repoql host lint --ast-grep`) to run ast-grep against the repository manually, useful for validation without full indexing.
- When globally disabled, emit a single telemetry warning and suppress annotations; X-Ray `headline` should mention “ast-grep disabled” only in that scenario.

### Targeting & Data Contract
- Language routing uses semantic media type first: map `document.MediaType.Kind` to an ast-grep language alias via a static table. Fall back to the RepoUri extension through the existing `language_from_media_type_or_uri(media_type, uri)` UDF to avoid drift.
- Annotation payload:
  - `annotation.kind = 'lint'`
  - `annotation.source = 'ast-grep'`
  - `annotation.rule_id = 'astgrep/<id>'`
  - `annotation.message` from CLI match.
  - `annotation.severity` mapped via the table above.
  - `annotation.data` minimal: `{ "captures": {...}, "rulePath": "<relative-or-abs>", "fix": { "text": "...", "edits": [...] }? }`. Avoid large payloads to keep `annotations_*` tables slim.
- When ast-grep returns edits, translate to RepoPatch representation:
  - Sort replacements descending by byte offset.
  - Each edit carries start/end byte positions and replacement text.
  - Include optional precondition digest (`repoql.digest`) when available.
  - Feed into existing fix pipeline (SARIF export, `repoql fix`, `repoql verify`). Always re-run `repoql verify` after fix application; gate merge on zero remaining annotations at or above configured severity.
- Targets:
  - Prefer setting `Target.SpanId` when a matching span exists; otherwise set `Target.TargetUri = RepoUri.FromLines(container, startLine, endLine)`. Never persist fragments on document nodes.
- Severity mapping table (authoritative):

  | ast-grep text | RepoQL `AnalysisSeverity` |
  | --- | --- |
  | `none` | `AnalysisSeverity.None` (suppressed) |
  | `hint`, `info` | `AnalysisSeverity.Suggestion` |
  | `warning` | `AnalysisSeverity.Warning` |
  | `error` | `AnalysisSeverity.Error` |

  `.editorconfig` overrides apply after this mapping; SARIF export reuses the same translation.

### Markdown Embedded Code
- For fenced blocks:
  - Run ast-grep against the block text but translate match ranges back into the parent Markdown document using block span offsets.
  - Always annotate the host Markdown document; do not manufacture standalone URIs for code blocks.
  - Skip blocks whose language label cannot be mapped to a known alias.

### Security & Correctness
- Treat rule files as untrusted input; quote all arguments, set working directory to repo root, and avoid shell globbing.
- Normalize Windows paths (drive letters, separators) via RepoUri utilities before passing to ast-grep; ensure annotations use canonical `file://` URIs.
- Skip archives (`zip:`, `jar:`) until ast-grep supports virtual file systems; log the skip reason.
- Apply timeouts on large files; emit partial results and continue rather than failing the document.

### Testing
- Golden fixtures covering offset conversions (CRLF vs LF), severity overriding, semantic key stability, fix ordering, Markdown block remapping, SARIF round-trip, Windows path handling, and RepoPatch application sequencing.

### Operational Queries
- Lint queue:  
  `SELECT source, rule_id, severity, message, resolved_target_uri FROM annotations_all('lint','info') WHERE source='ast-grep' ORDER BY severity_rank DESC, created_at DESC;`
- Snippet preview:  
  `SELECT * FROM snippet('<resolved_target_uri>', 3);`

## Risks & Mitigations
| Risk | Mitigation |
| --- | --- |
| Process overhead for large repos | Limit concurrency, monitor metrics, batch multiple files per call in follow-up if needed. |
| Missing ast-grep executable | Detect at startup, log warning, skip analyzer while keeping RepoQL functional. |
| User misconfiguration (broken rules) | Catalog logs warnings and skips unhealthy rules instead of failing enrichment. |
| Windows path handling | Normalize RepoQL URIs to native paths before invoking CLI; cover with integration tests. |
| Rule conflicts with `.editorconfig` semantics | Namespace rule ids (`astgrep/<id>`) to avoid collisions and reuse existing override mechanism. |

## Extensions
- **LSP client foundation** – If RepoQL needs streaming diagnostics, build a reusable client that supports ast-grep and other servers (eslint, Biome, Pyright).
- **Rule testing support** – Optionally run `ast-grep test` when repositories supply fixtures, exposing failures through RepoQL tooling.
- **Batch execution** – Explore scanning multiple files per process if profiling shows CLI startup overhead dominates.
- **UI integrations** – Surface captures and autofix previews directly in dashboards or agent responses once annotations are available.

## Conclusion
Integrating ast-grep through its CLI gives RepoQL immediate access to structural linting across many languages while honoring existing repository configurations. The proposed catalog, runner, and analyzer components align with RepoQL’s enrichment architecture and remain compatible with future enhancements such as LSP streaming or native bindings.
