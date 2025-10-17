# Proposal: `glob_match` UDF for RepoQL

## Summary
Introduce a DuckDB scalar UDF `glob_match(uri, pattern, ignore_case := TRUE, default_scheme := 'file:///') -> BOOLEAN` that applies Git-style glob rules to RepoQL URIs. The helper keeps glob semantics consistent across CLI tools, macros, and ad-hoc SQL, reducing duplicated pattern translation logic and enabling future filters to stay server-side and deterministic.

## Goals
- Provide a single, documented globbing primitive for queries, macros, and automation agents.
- Match familiar Git-style glob semantics to minimize onboarding friction.
- Keep evaluation deterministic by matching against stored URIs instead of dynamically scanning the filesystem.

## Non-goals
- Implement `.gitignore` rule precedence (`!`, `#` comments, etc.).
- Provide streaming filesystem enumeration during query execution.
- Replace existing higher-level CLI convenience commands—those will delegate to the UDF.

## Motivation
- **Unified semantics.** The CLI currently emulates globbing by translating patterns into `LIKE` expressions (`TerminalCommands.BuildLikePattern`). Every macro or tool that wants glob support must replicate those rules, increasing divergence risk.
- **Agent ergonomics.** SQL-first workflows (agents, automation) can filter documents with `WHERE glob_match(uri, 'src/**/*.md')`, avoiding brittle escaping and keeping queries short.
- **Deterministic results.** Operating inside DuckDB ensures filters apply to the indexed repository state, instead of hitting the live filesystem per query (which can drift or miss synthetic URIs such as `embed:///…`).

## Intended Use Cases
- Narrowing `xray_documents()` or custom document inventories to a sub-tree (`glob_match(uri, 'src/**/*.md')`).
- Scoping annotation or lint queries to specific folders or file types without hand-written `LIKE` clauses.
- Combining with macros like `file_search` to keep semantic search output within directories relevant to a policy or feature area.
- Powering CLI filters (e.g., `repoql xray src/**/*.md`) by delegating the pattern directly to SQL.

## Design Overview

### Function signature
```sql
glob_match(uri TEXT, pattern TEXT,
           ignore_case BOOLEAN := TRUE,
           default_scheme TEXT := 'file:///') -> BOOLEAN
```

- `uri`: RepoQL document or node URI (absolute).
- `pattern`: Git-style glob string (see below).
- `ignore_case`: lowercases both operands when `TRUE`, matching current CLI defaults.
- `default_scheme`: prepended when `pattern` lacks a URI scheme, allowing repo-relative patterns (`src/**/*.cs`) to remain ergonomic.
- Returns `NULL` when `uri` or `pattern` is blank, otherwise `TRUE`/`FALSE`.

### Dialect (Git-style glob)
- `*` matches any run of characters within a single path segment.
- `?` matches a single character within a segment.
- `**` matches zero or more segments (cross-directory).
- Leading `/` anchors to the repository root (`file:///repo/` once normalized).
- Backslashes normalize to `/`.
- Patterns may include scheme prefixes (`file:///`, `embed:///`) to target non-default stores.
- Negation (`!`) is out-of-scope for the first iteration; callers can combine predicates (`WHERE glob_match(...) AND NOT glob_match(...)`) if needed.

### Normalization pipeline
1. **Trim / validate.** If either argument is null/empty, return null to preserve three-valued logic.
2. **Scheme inference.** When the pattern lacks `://`, prepend `default_scheme` and ensure exactly one slash after it (mirrors CLI behavior).
3. **Path canonicalization.** Replace backslashes with forward slashes, collapse repeated `/`, and lowercase inputs when `ignore_case` is true.
4. **Glob → regex translation.**
   - Escape regex metacharacters (`.`, `+`, `(`, `)`, `[`, `]`, `{`, `}`, `|`, `^`, `$`).
   - Replace `**/` with `(?:.*/)?` and a terminal `**` with `.*`.
   - Replace `*` with `[^/]*` and `?` with `[^/]`.
   - Anchor the regex with `^…$`.
5. **Cache compiled regex.** Maintain a static `ConcurrentDictionary<(string pattern, bool ignoreCase, string defaultScheme), Regex>` guarded with `RegexOptions.Compiled | RegexOptions.CultureInvariant` for reuse across vectorized batches.
6. **Match execution.** Apply the regex to the normalized URI string; return boolean result.

### Registration
Add the new function to `RepositoryUserDefinedFunctions.RegisterAll`, adjacent to other URI helpers. Mark it pure (`isPureFunction: true`) so DuckDB can reuse results when inputs repeat.

### Integration touches
- **CLI:** Update `TerminalCommands.BuildLikePattern` to delegate to SQL (`WHERE glob_match(uri, ?)`) once the UDF ships, eliminating the custom translation code.
- **Docs:** Document the helper alongside existing URI UDFs in `docs/Schema.md`, with a short table of examples.
- **Macros:** Optional future macro `documents_glob(pattern)` can wrap the predicate for discoverability.

## Sample Usage
```sql
-- Filter Markdown documents inside src/
SELECT uri, headline
FROM xray_documents()
WHERE glob_match(uri, 'src/**/*.md');

-- Only README variants in the repository root (case-sensitive)
SELECT uri
FROM node
WHERE kind = 'document'
  AND glob_match(uri, '/README?.md', ignore_case := FALSE);

-- Combine with semantic search to keep results under docs/
WITH ranked AS (
  SELECT uri, score
  FROM file_search('embedding runtime', k := 100)
)
SELECT *
FROM ranked
WHERE glob_match(uri, 'docs/**/*.md');
```

## Implementation Plan
1. Implement the translator and caching helper inside `RepositoryUserDefinedFunctions` and register `glob_match`.
2. Add unit tests covering positive/negative cases, case sensitivity, inferred scheme, and `**` semantics (`RepoQL.Tests`).
3. Update CLI commands/macros to use `glob_match` where globbing is required.
4. Document the UDF in `docs/Schema.md` and update any quickstart/advanced-search examples.

## Risks & Mitigations
- **Regex translation bugs.** Start with focused tests mirroring Git’s glob expectations, including tricky edge cases (`**/*.md`, leading slash, double wildcards). Keep the translator small and comment it thoroughly.
- **Performance regressions.** Cache compiled regexes and avoid per-row allocations; trim memory usage by limiting cache size if necessary (LRU or `ConcurrentDictionary` with eviction logic).
- **Dialect drift.** Call out Git-style rules in the docs and reuse the same translator everywhere (CLI & macros) to avoid divergence.
