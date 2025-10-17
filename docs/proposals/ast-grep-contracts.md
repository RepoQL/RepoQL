# Ast-grep Integration: Contracts & Data Structures

This note records the concrete types, helper functions, and payload contracts that the implementation will introduce or extend. Each entry lists the namespace/file that will host the definition.

## Core Types

| Type | Location | Purpose |
| --- | --- | --- |
| `AstGrepRuleCatalog` | `RepoQL.Core.Analysis.AstGrep` | Loads rule metadata from discovered `sgconfig.yml` roots. Exposes `GetRulesForLanguage(string)` and `TryGetRule(string, out AstGrepRule)` where `AstGrepRule` captures `Id`, `Language`, default severity, autofix hint, and file path. |
| `AstGrepRunner` | `RepoQL.Core.Analysis.AstGrep` | Shells out to `ast-grep scan`, enforces timeouts, concurrency cap, and parses `--json=stream` output into `AstGrepMatch` DTOs. |
| `AstGrepAnalyzer` | `RepoQL.Core.Analysis.AstGrep` | Implements `IFormatAnalyzer`, coordinates rule filtering, severity resolution, semantic-key creation, RepoURI conversions, Markdown embed mapping, and fix translation. |
| `AstGrepMatch` | `RepoQL.Core.Analysis.AstGrep` | In-memory representation of a CLI match: `RuleId`, `Message`, ranges (line/column + byte), captures `Dictionary<string,string>`, optional `Replacement`. Not persisted. |
| `AstGrepSeverityMap` | `RepoQL.Core.Analysis.AstGrep` | Static helper returning `AnalysisSeverity` for raw ast-grep severities (`none`, `hint`, `info`, `warning`, `error`). Used by analyzer and SARIF exporter. |
| `CompositeAnalyzer` | existing (`RepoQL.Core.Analysis`) | Reused to pair ast-grep with current format analyzers. No structural change; we will instantiate it when registering services. |

## Configuration Discovery

| Contract | Description |
| --- | --- |
| `AstGrepConfigLocator` (helper inside catalog) enumerates config roots in this precedence: (1) `<repo-root>/sgconfig.yml`, (2) `<repo-root>/.astgrep/sgconfig.yml`, (3) `.repoql/ast-grep/sgconfig.yml`, (4) synthetic config if none present. Returns `(configPath, rootDirectory)`. |
| Environment variable `REPOQL_AST_GREP_ENABLED` (default `true`). When `false`, `AstGrepAnalyzer` short-circuits and logs a single warning. |
| `.editorconfig` overrides: handled by existing `EditorConfigSettingsProvider` using keys `repoql.analyzer.astgrep/<ruleId>.severity` and `.autofix`. Severity `none` disables the rule. |

## Runner Contracts

| Field | Details |
| --- | --- |
| Working directory | Repo root (from `AddRepoIndexer`). |
| Command | `ast-grep scan --config "<sgconfigPath>" --filter "<regex>" --json=stream --no-ignore hidden "<filePath>"` (all arguments quoted). |
| Timeout | Configurable; default 30 seconds per process. |
| Concurrency | `SemaphoreSlim` capped at 4 (configurable via options). |
| Metrics | `IndexingMetrics.RecordAstGrepRun(duration, matches, ruleId)` and counters for failures/timeouts. |

## Annotation Contracts

| Field | Value |
| --- | --- |
| `annotation.kind` | `lint` |
| `annotation.source` | `ast-grep` |
| `annotation.rule_id` | `astgrep/<original-rule-id>` |
| `annotation.severity` | From severity map, after `.editorconfig` override. |
| `annotation.message` | From CLI match text. |
| `annotation.semantic_key` | `lint:astgrep:{rule_id}:{container_uri_lower}:{start_line}:{end_line}:{sha1(sorted-captures)}` |
| `annotation.data` | Minimal JSON `{ "captures": { ... }, "rulePath": "<relative-or-abs>", "fix": { "text": "...", "edits": [...] }? }` |
| `annotation.target` | Prefer `SpanId`. Otherwise set `TargetUri = RepoUri.FromLines(container, startLine, endLine)` without fragment duplication. |

### Fix Representation

`AstGrepAnalyzer` converts matches with rewrites into a `RepoPatch`:

1. Collect edits as `(startByte, endByte, newText)` using UTF-8 offsets.
2. Sort descending by `startByte` to maintain stability.
3. Include optional precondition digest (`repoql.digest`) from `DocumentModel.Metadata`.
4. Produce `AnalysisFix` with `Description` (from rule metadata when provided) and `Replacements` referencing the original RepoUri.
5. Existing fix tooling (`repoql fix`, SARIF export) consumes the same `AnalysisFix` object.

### Severity Table (authoritative)

| Raw | `AnalysisSeverity` |
| --- | --- |
| `none` | `AnalysisSeverity.None` |
| `hint`, `info` | `AnalysisSeverity.Suggestion` |
| `warning` | `AnalysisSeverity.Warning` |
| `error` | `AnalysisSeverity.Error` |

Reuse this map everywhere we convert ast-grep severities (annotations, SARIF).

## Offset & URI Handling

| Contract | Description |
| --- | --- |
| Line numbers | Store as 1-based (`StartLine`, `EndLine`). |
| Byte offsets | Compute as 0-based UTF-8 positions with CRLF normalization to match RepoQL expectations. |
| RepoURIs | Always use `RepoUri.FromLines` / `RepoUri.FromChars`. Preserve container casing; do not hand-roll fragments. |
| Markdown embeds | Translate fenced-block offsets back to host Markdown document before emitting annotations; use the block’s `DocumentSpan` from `MarkdownLoader`. |

## Metrics & Telemetry

| Metric | Description | Location |
| --- | --- | --- |
| `repoql.astgrep.run.duration` | Histogram of runner durations. | `IndexingMetrics` |
| `repoql.astgrep.matches` | Counter labelled by rule id. | `IndexingMetrics` |
| `repoql.astgrep.failures` | Counter for non-zero exits/timeouts. | `IndexingMetrics` |
| X-Ray headline note | Emit “ast-grep disabled via REPOQL_AST_GREP_ENABLED=0” when analyzer is globally disabled. | `AnnotationResultWriter` / X-Ray templating |

## CLI Surface

- `repoql host lint --ast-grep` → invokes `AstGrepRunner` across repo using current config. Located in `RepoQL.ConsoleApp.Commands`.
- Helpful SQL snippets (documented in docs and/or command help):
  - Queue:  
    `SELECT source, rule_id, severity, message, resolved_target_uri FROM annotations_all('lint','info') WHERE source='ast-grep' ORDER BY severity_rank DESC, created_at DESC;`
  - Snippet preview:  
    `SELECT * FROM snippet('<resolved_target_uri>', 3);`

## Testing

Add golden tests under `src/tests/RepoQL.Tests/AstGrep`:

- Severity override precedence.
- Semantic-key stability with identical matches.
- CRLF vs LF byte offset calculation.
- Markdown fenced-block annotation remapping.
- RepoPatch ordering and SARIF round-trip.
- Windows path normalization when invoking the runner.

These tests will assert the contracts above and guard against regressions.
