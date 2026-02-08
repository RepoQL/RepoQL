---
description: Plan for SimHash fingerprinting at index time and near-duplicate detection at query time
tags: [indexing, simhash, dedup, duplicates, explore]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: SimHash Dedup

Implements: [Intelligent Context Design](../../designs/future/intelligent-context.md) — SimHash Storage Decision section, Data Flow (Index-Time Pipeline)

## Scope

**Covers:**
- `SimHashCalculator` — token extraction and 64-bit fingerprint computation
- `artifact.simhash` column addition (schema migration)
- SimHash computation during artifact construction in indexing pipeline
- `IDuplicateDetector` interface and `SimHashDuplicateDetector` implementation
- `ExploreResult` extension with `DuplicateOf` and `HammingDistance` fields
- SimHash projection in search results
- Duplicate annotation rendering in OutputComposer
- Duplicate grouping (duplicates rendered near their canonical)

**Does not cover:**
- Focused snippets (Plan: 01-focused-snippets)
- Query expansion (Plan: 02-query-expansion)
- Clustering (Plan: 04-clustered-output) — duplicate groups become a cluster type there
- Budget demotion of duplicates (Plan: 05-three-level-allocation) — allocation changes live there

## Enables

Once SimHash Dedup exists:
- **Duplicate files identified** — agents see "near-duplicate of X, hamming=2" instead of redundant content
- **Plan 04** can form duplicate clusters from annotated results
- **Plan 05** can demote duplicate EV to recover budget for non-duplicate results
- **`find_clones()` macro** becomes possible — "what files are copies of this one?"

## Prerequisites

- `artifact` table exists with current schema
- DuckDB `bit_count()` function available (built-in)
- Indexing pipeline runs `Materialize()` to build artifact records before commit

## North Star

Copy-pasted files, vendored code, and backup copies are identified at near-zero cost — 8 bytes stored, 10 nanoseconds compared. Agents know copies exist without reading them twice.

## Done Criteria

### Schema

- The artifact table shall have a `simhash UBIGINT` column
  - When the column does not exist (fresh database), it shall be created in table definition
  - When upgrading an existing database, migration shall `ALTER TABLE artifact ADD COLUMN simhash UBIGINT`
  - Existing rows shall have NULL simhash until reindexed

### SimHashCalculator

- The SimHashCalculator shall accept content as a string and return a `ulong` fingerprint
- Token extraction shall split content into tokens with weights:
  - Identifiers (split from camelCase/PascalCase): weight 1.0
  - Language keywords (`class`, `function`, `if`, `return`, etc.): weight 0.5
  - Structural tokens (`{`, `}`, `=>`, `:`): weight 0.3
  - String literals: weight 0.0 (ignored)
  - Comments: weight 0.0 (ignored)
  - Whitespace: weight 0.0 (ignored)
- All tokens shall be lowercased before hashing
- The hash function shall produce well-distributed 64-bit values (xxHash or equivalent)
- The voting algorithm shall: for each (token, weight), hash to 64 bits, for each bit position add weight if bit set else subtract weight, then set fingerprint bit where vote > 0
- Identical content shall produce identical fingerprints
- When content is empty or null, return 0

### Indexing Integration

- SimHash shall be computed during artifact construction, alongside headline, structure, and token_count
- The computed value shall be set on the Artifact record's `simhash` field
- SimHash shall be recomputed when file content changes (same trigger as re-parsing)
- SimHash computation shall not block or fail the indexing pipeline
  - If computation throws, log warning and set simhash to NULL

### Search Projection

- The search engine shall project `a.simhash` from the artifact table in search results
- SearchResult shall carry `SimHash` as `ulong?`
  - When artifact has NULL simhash, SearchResult.SimHash shall be null

### IDuplicateDetector

- The `IDuplicateDetector` interface shall define `DuplicateResult Detect(IReadOnlyList<SearchResult> results)`
- `DuplicateResult` shall contain `IReadOnlyList<AnnotatedResult>` preserving input order
- `AnnotatedResult` shall include original `SearchResult`, `DuplicateOf` (string, nullable), and `HammingDistance` (int, nullable)

### SimHashDuplicateDetector

- The detector shall process results in score-descending order
- For each result, compute Hamming distance (popcount of XOR) against every result already marked canonical
- When Hamming distance ≤ 3 against any canonical, mark as duplicate of that canonical
  - Set `DuplicateOf` to the canonical's URI
  - Set `HammingDistance` to the computed distance
- When Hamming distance > 3 against all canonicals, mark as canonical (DuplicateOf = null)
- When SimHash is null on a result, treat as canonical (never matches as duplicate)
- When all results have null SimHash, return all as canonical (passthrough)

### ExploreResult Extension

- ExploreResult shall include `DuplicateOf` (string?) and `HammingDistance` (int?) fields, defaulting to null
- SearchResult-to-ExploreResult conversion shall map DuplicateOf and HammingDistance from AnnotatedResult

### Output Rendering

- Duplicates shall be rendered near their canonical in the output (reorder after detection, before rendering)
- Duplicate rendering format: `{confidence}% {uri}  (near-duplicate of {canonical_filename}, hamming={distance})`
  - Use filename only for canonical reference, not full URI
- Duplicates shall render at Compact level (URI + headline + duplicate annotation)
  - Representation level is advisory in this plan; enforcement via allocation is Plan 05

### Passthrough

- When no results have simhash values, the detector shall return all results unchanged
- The pipeline with DuplicateDetector returning passthrough shall produce output identical to today's pipeline minus the reordering of duplicates near canonicals

## Constraints

- **Artifact column, not annotation** — simhash is a computed content property like token_count; stored directly on artifact
- **Threshold is 3** — conservative; false positives are worse than missed clones. Tunable later
- **Demote, not hide** — duplicates appear in output with annotation; agents can still read() them
- **No cross-repository dedup** — SimHash is per-repository; imported repos have separate artifact entries
- **Language-agnostic tokenization** — token extraction shall use regex-based splitting (not AST parsing). The tokenizer must work on any text content: code in any language, markdown, YAML, etc. Regex patterns: split on whitespace and punctuation, recognize `camelCase`/`PascalCase` boundaries, lowercase all tokens. Language keywords are identified from a static multi-language keyword set, not per-file language detection

## References

- [Intelligent Context Design](../../designs/future/intelligent-context.md) — SimHash Storage Decision, contracts, data flow
- [SimHash Dedup Flow](../../flows/future/intelligent-context/simhash-dedup.md) — index-time and query-time flows
- `src/RepoQL.Data.DuckDB/Schema/Tables/artifact.sql` — artifact table definition
- Format loaders (e.g., `src/Formats/RepoQL.Formats.DotNet/CSharpLoader.cs`) — Materialize() where simhash is computed
- `src/RepoQL.Explore/ExploreResult.cs` — record to extend
- `src/RepoQL.Explore/OutputComposer.cs` — rendering to modify
- `src/RepoQL.Explore/Search/ExploreSearchEngine.cs` — simhash projection

## Error Policy

SimHash failures must not affect indexing or search:
1. If SimHashCalculator throws during indexing, log warning and store NULL — file is indexed normally without fingerprint
2. If duplicate detection throws during explore, log warning and return all results as canonical — explore continues without dedup
3. NULL simhash is always treated as canonical — never produces false duplicate matches
