---
source: Agent UAT (Claude Opus 4.6)
date: 2026-02-13
commit: 76f2631ecc1eed71a78b7754b8ca3174748b277d
issues: [89, 90, 91, 92, 93, 94, 95]
scope: Multi-repo stress test (~35K files, ~4.5M lines, 440K edges)
---

# RepoQL Stress Test: Agent Experience Report

An LLM agent (Claude Opus 4.6) was given free rein to push RepoQL as hard as possible across a multi-repo knowledge base with two imported GitHub repositories (~35K files, ~4.5M lines, 440K graph edges). This document captures what worked, what didn't, and what would make the biggest difference — written for the RepoQL development team.

---

## The Discoverability Problem

This is the single biggest issue. RepoQL has extraordinary capabilities that agents won't find unless they stumble into them or are told.

### What I actually used vs what exists

I spent the entire session using ~15% of the available surface area. Here's the gap:

**Views:** 51 exist. I used 8 (`Files`, `Types`, `Functions`, `markdown_headings`, `csharp_types`, `php_inheritance`, `markdown_capsules`, `typescript_components`). I never touched `markdown_links`, `markdown_codeblocks`, `csharp_members`, `csharp_namespaces`, `php_members`, `php_trait_usage`, `typescript_imports`, `typescript_declarations`, `ruby_*` (12 views), `pdf_*` (3 views), `git_recent`, `repo_index`, `annotations`, or `Filesystems`.

**Functions:** I discovered `search`, `search_symbol`, `snippet`, `related`, `ask`, `glob_files`, `parse`, `find_similar`, `explore_structured`, `git_blame`, `git_diff`, `git_status`, `git_file_history`, `csv`, `csv_files`, `csv_schema`, `csv_data`, `csv_preview`, `grep_matches`, `llm_extract`, `zoom_and_enhance`, `vss_match`, `vss_join`, `mcp_tools`, `mcp_tool_params`, `annotations_for`, `changes_related_to`, `entities_by_uri`, and `glob_match`. I discovered most of these only by querying `duckdb_functions()` — the tool description mentions a fraction.

**Hidden parameters:** Many functions accept glob patterns and URI lists that aren't obvious from the tool description. `related()` has `uri_glob` and `mime_glob`. `search()` has `scope`, `sem_threshold`, `bm25_threshold`, `derank_factor`, `enable_body_rescue`. `glob_files()` has `default_scheme`, `ignore_case`, and `uris`. `changes_related_to()` takes keywords, not a URI. I discovered these by querying `duckdb_functions()` for parameter lists.

### What would help

1. **A `SELECT * FROM help()` table** that lists all views, table macros, and their column schemas in one query. An agent's first instinct is SQL — let them discover via SQL.

2. **Consistent column naming across views.** `csharp_types` uses `document_uri`. `markdown_headings` uses `document_uri`. But I had to error to discover this — I guessed `file_uri` first. A convention documented once would save every agent a round-trip.

3. **The tool description for `query` should mention `explore_structured`, `glob_files` with symbol fragments, `parse()`, `csv()`, `find_similar()`, and `changes_related_to()`.** These are the most powerful composition primitives and none appear in the current tool description's quick examples.

---

## What Works Brilliantly

### Tier 1: Composition primitives

**`parse()` + JOIN** — Inline CSV lookup tables joined against any view. Auto-detects comma, pipe, and tab delimiters. This is the most frequently useful pattern for ad-hoc categorization (team ownership, domain mapping, SLA tiers) against the codebase graph.

```sql
SELECT t.team, COUNT(*) as types
FROM parse('service,team
Identity,Foundation
Community,Foundation
PaymentQuery,Giving') t
JOIN csharp_types ct ON ct.document_uri LIKE '%/services/' || t.service || '/%'
GROUP BY t.team
```

**`glob_files()` with symbol fragments** — Not a file finder. A universal symbol address resolver. Supports multiple `;`-delimited patterns with full scheme URIs, `!` exclusions, `#symbol=` matching, `default_scheme`, and `ignore_case`.

```sql
SELECT uri FROM glob_files(
  'github://pushpay/platform-services/services/Identity/**/*.cs#symbol=*Service;'
  'github://pushpay/platform-services/services/Community/**/*.cs#symbol=*Service;'
  '!**/*Test*'
)
```

**`explore_structured()`** — The bridge between explore-as-a-tool and query-as-SQL. Returns `uri, confidence, kind, headline, structure, snippet, parent_uri, depth` as rows. Results go to symbol level (individual methods, properties, types). Fully composable — JOIN with `Files`, `csharp_types`, feed into `find_similar`, anything.

**`csv()`** — Reads any CSV file in the index as a SQL table with zero setup. Combined with `csv_files(pattern)` for metadata discovery and `csv_schema()` for column inference.

**`find_similar()` as SQL** — Returns `uri, similarity, headline` as a JOINable table. Accepts a `scope_glob` parameter for scoping the search space.

### Tier 2: Analysis tools

**`PIVOT`** — Instant cross-tab analysis. Service × language lines-of-code matrix in one query.

**`changes_related_to(keywords)`** — Semantic git search. Takes keywords (not a URI), returns commits with a `related_files` field showing the subset of changed files semantically relevant to the concept. The agent expected a URI parameter and used it wrong for several attempts.

**Language-specific views** — `csharp_types`, `php_inheritance`, `php_types`, `php_members`, `typescript_components`, `markdown_capsules`, `markdown_headings`, `markdown_links`, `Functions`, `Types`. These replace 12-line recursive CTEs against raw `node`/`edge` tables. The agent wrote recursive CTEs for 15 minutes before discovering these existed.

**`git_blame(scope_glob)`** — Blame across multiple files at once with a glob pattern. Not single-file only.

### Tier 3: The explain tool

`explain` produced the two most impressive results of the entire session:

1. "What are PushPay's most critical architectural risks?" → Deep SPOF analysis citing race condition FIXMEs from PHP source code, SQL Server as universal dependency, auth server as single point of failure, with evidence from three repos.

2. "Trace the flow from when a payment is made to all downstream services" → Complete event-driven architecture walkthrough citing Mermaid diagrams, C# source, PHP handlers, test specs, and schema ERDs.

Both answers included file:///path#line=N,M citations that were verifiable. This is the highest-value tool for complex questions where the agent doesn't know where to look.

---

## The Comment-as-Prompt Pattern

SQL comments are read by the LLM summarizer. This has non-obvious consequences:

**The problem:** When the agent writes `-- Fix: column is document_uri` or `-- explore as a SQL macro — composable with other queries`, the summarizer interprets these as intent and weaves them into its synthesis. This produced bizarre results — an answer about "SQL macros" when the query was about payment reconciliation, because the comment mentioned "SQL macro."

**The implication:** Comments are a prompt channel, not just documentation. This is actually a powerful feature if used deliberately, but it's a trap when used for debugging notes.

**Recommendation:** Document this behavior. It enables a useful pattern — put a question in the SQL comment and the summarizer will try to answer it using the query results as context. But agents need to know that debugging comments will pollute results.

---

## Cross-Language Similarity: Detailed Findings

### What works

| Seed type | Score | Example |
|---|---|---|
| Same language, same domain (C# → C#) | 0.76–0.86 | Identity Startup.cs → Community Startup.cs |
| Doc → doc, same topic | 0.60–0.87 | Identity gestalt → auth docs |
| Competitor → competitor | 0.71–0.74 | Tithely README → Planning Center pricing |
| Product doc → customer feedback | 0.44–0.49 | Check-in concept → NPS detractor about check-in |
| Symbol-level C# → PHP | 0.51–0.57 | `ConvertPersonCriteriaToCcbPersonSearch` → `IndividualSearchGateway` |

### What fails (0.00)

| Seed | Scope | Why |
|---|---|---|
| Whole C# file → PHP files | Different language = different embedding space |
| C# Startup.cs → PHP | Framework boilerplate overwhelms domain signal |
| GraphQL type definition → PHP | Structural code, not semantic |
| Product docs → competitor docs | Different framing registers |
| Technical docs → operational guidance | Architecture ≠ runbooks |
| Customer feedback → business docs | Anecdotal voice ≠ strategy language |

### The key finding

Whole-file embeddings carry too much language-specific noise (imports, namespaces, framework patterns). A single function with **domain vocabulary in its name** concentrates enough semantic signal to cross the language barrier. `ConvertPersonCriteriaToCcbPersonSearch` found PHP `IndividualSearchGateway` at 0.57 because the function name IS the domain — "person", "criteria", "search", "CCB."

### Implications for development

1. **Symbol-level embeddings** may already be stronger than file-level for cross-language use. If they're computed separately, the cross-language path might work better when seeded with `#symbol=` URIs than whole files.

2. A **text-seed similar** mode (`=> similar: "validates email addresses"`) would bypass the embedding-space boundary entirely. The agent frequently knows what it's looking for in natural language but has no exemplar file to seed with.

3. The `find` modifier and `grep_matches` serve as fallback cross-language search — they use keywords, not embeddings, so they cross language boundaries freely. Agents should be guided to fall back to these when `similar` returns 0.00.

---

## Bugs and Issues

### `parse()` JSON escaping

JSON strings get escaped during MCP tool transport. `parse()` receives `{\"service\":\"Identity\"}` instead of `{"service":"Identity"}`. The JSON detection path exists — a single-line JSON array `[{...}]` returns "No results" (recognized as JSON, failed to materialize). NDJSON falls through to CSV detection and splits on commas inside JSON objects.

This is likely a serialization issue in the MCP transport layer, not in `parse()` itself.

### `grep_matches` OOM on wide scopes

`grep_matches('race condition', 'file:///**')` exhausted 11.1GB of memory. Needs either a result limit parameter or internal streaming/pagination. The `=> grep:` and `=> regex:` read modifiers handle the same workload fine — the SQL function may be materializing all results before filtering.

### `grep_matches` and `=> regex:` unavailable for imported repos

Text search (`=> grep:`, `=> regex:`, `grep_matches`) only works on `file:///` URIs. This is the most-requested missing capability — "find every `throw new.*Exception` in the PHP codebase" is a common investigation pattern that currently can't be done.

### `search_symbol` low recall

`search_symbol('Service', kind_filter := 'type', k := 20)` returned only 3 results across 10K+ PHP files. The PHP/TypeScript symbol index appears sparse compared to C#. Expected dozens of matches.

### `help://` not indexing

`help://**` returned "No files matched" throughout the session. The help documentation was either not indexed or not available in this configuration.

---

## Feature Requests (by impact)

### High impact

1. **Text search on imported repos.** `grep_matches` and `=> regex:` for `github://` URIs. This covers the #1 investigation use case across multi-repo setups.

2. **`help://` must never be down.** The entire discoverability problem in this session traces back to `help://` failing on the first call. The agent followed the recommended onboarding path, it broke, and the agent never tried again. If `help://` is guaranteed available, the discoverability problem largely solves itself — the documentation exists, the agent just couldn't reach it.

3. **Cross-language concept similarity.** A `similar` mode that embeds on normalized AST intent rather than raw tokens. Or a text-seed similar that accepts natural language instead of requiring a URI exemplar.

### Medium impact

4. **Graph traversal sugar.** A `reachable(uri, edge_type, max_depth)` table function replacing 12-line recursive CTEs. The 440K edges are powerful but verbose to query.

5. **Cross-service communication edges.** `USES_SYMBOL` finds nothing across microservices (they don't share code). A `PUBLISHES_TO` / `CONSUMES_FROM` edge derived from event names, Kinesis stream configuration, or shared NuGet package references would answer "what breaks if I change this event schema?"

6. **Fix `parse()` JSON path.** Unescape JSON before parsing, or accept a format hint parameter.

### Lower impact

7. **`grep_matches` memory management.** Streaming/pagination or an internal result cap to prevent OOM on wide scopes.

8. **`search_symbol` PHP/TS recall.** Investigate why the PHP/TypeScript symbol index yields so few results compared to C#.

---

## Patterns for Agent Documentation

The following patterns should be taught to agents explicitly, as they are non-obvious and high-value:

### 1. Start with views, not raw tables

```sql
-- WRONG: 12-line recursive CTE on node/edge
-- RIGHT:
SELECT extends, COUNT(*) FROM csharp_types WHERE extends IS NOT NULL GROUP BY extends ORDER BY 2 DESC
SELECT target_name, COUNT(*) FROM php_inheritance WHERE relationship = 'EXTENDS' GROUP BY 1 ORDER BY 2 DESC
```

### 2. `glob_files` is a symbol resolver, not just a file finder

```sql
-- All Handle* methods in Identity and Community, excluding tests
SELECT uri FROM glob_files(
  'github://pushpay/platform-services/services/Identity/**/*.cs#symbol=*.Handle*;'
  'github://pushpay/platform-services/services/Community/**/*.cs#symbol=*.Handle*;'
  '!**/*Test*;!**/*Spec*'
)
```

### 3. `parse()` for inline lookup JOINs

```sql
SELECT t.team, SUM(f.lines) as total_lines
FROM parse('pattern,team
%/Identity/%,Foundation
%/Community/%,Foundation
%/PaymentQuery/%,Giving') t
JOIN Files f ON f.uri LIKE t.pattern
GROUP BY t.team
```

### 4. `explore_structured` for composable search

```sql
SELECT e.uri, e.confidence, e.kind
FROM explore_structured('identity merge', 500, 'Locate', NULL, NULL, '(?i)test') e
WHERE e.confidence > 70
```

### 5. SQL comments steer the summarizer

```sql
-- No comment: raw data returned
-- "What patterns exist in auth code?": summarizer answers this question using query results
SELECT * FROM search('authentication', k := 20)
```

### 6. `changes_related_to` takes keywords, not URIs

```sql
SELECT * FROM changes_related_to('identity authentication login')
-- Returns commits ranked by semantic relevance, with related_files subset
```

### 7. For cross-language similarity, use symbol-level seeds

```sql
-- Whole file → 0.00 across languages
-- Symbol with domain vocabulary → 0.51-0.57
SELECT * FROM find_similar(
  'github://repo/path/File.cs#symbol=Namespace.Class.DomainRichMethodName',
  'github://other-repo/**/*.php',
  10
)
```

### 8. `csv()` for instant structured data from CSV files

```sql
SELECT domain, COUNT(*) as endpoints
FROM csv('file:///path/to/api-inventory.csv')
GROUP BY domain
ORDER BY endpoints DESC
```

---

## Operational Issues Found in Logs

Source: `.repoql/host_002.log`

### 1. Indexer errors on deleted files

The indexer threw `DirectoryNotFoundException` for files under `research/denominations/` that had been deleted (visible as `D` in `git status`). Affected paths include `lcms/`, `national-baptist/`, `global-methodist/`, `governance-comparison.md`, and others. The file watcher appears to have detected these paths but the indexer attempted to read them after they were already removed from disk.

```
[2026-02-13 16:12:14.469 PID 5407 ERR] file:///research/denominations/lcms/README.md failed during indexing
System.IO.DirectoryNotFoundException: Could not find a part of the path
  '/Users/.../research/denominations/lcms/README.md'.
  at RepoQL.Indexing.RawArtifact.HashAsync(IFileInfo file, CancellationToken ct)
  at RepoQL.Indexing.Indexing.IndexingEngine.IndexItemAsync(IndexItem item, CancellationToken cancellationToken)
```

At least 8 files hit this error at the same timestamp. Query results throughout the early session showed `index: N pending` (N ranged from 1–7). Whether the deleted files caused the pending state is unknown — it could have been semantic embedding generation, imported repo processing, or something else.

### 2. gRPC stream abort under parallel load

The `HoldClientLease` stream failed with "The HTTP/2 connection faulted" during a burst of parallel queries:

```
[2026-02-13 15:59:09.097 PID 5407 ERR] Error when executing service method 'HoldClientLease'.
System.IO.IOException: The request stream was aborted.
  ---> Microsoft.AspNetCore.Connections.ConnectionAbortedException: The HTTP/2 connection faulted.
  at RepoQL.ConsoleApp.Host.RepoQlServiceImpl.HoldClientLease(...)
```

Six clients disconnected within 4 seconds (`16:04:32–16:04:36`). The MCP client circuit breaker tripped, producing "gRPC call disposed" errors on sibling tool calls. The host process exited cleanly (exit code 0) but the circuit breaker prevented reconnection for the 5-minute window.

### 3. MemoryPool disposal crash

Later in the session, the host crashed with `ObjectDisposedException` on its Kestrel `MemoryPool`. Four HTTP/2 connections failed simultaneously at `22:11:28.231`:

```
[2026-02-13 22:11:28.231 PID 1291 WRN] Connection processing ended abnormally.
System.ObjectDisposedException: Cannot access a disposed object.
Object name: 'MemoryPool'.
  at Microsoft.AspNetCore.PinnedBlockMemoryPool.Rent(Int32 size)
  at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.Http2FrameWriter.WriteHeaderUnsynchronized()
  at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.Http2FrameWriter.WriteGoAwayAsync(...)
  at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.Http2Connection.ProcessRequestsAsync(...)
```

Two failure modes occurred: `WriteGoAwayAsync` (trying to send a GOAWAY frame with disposed pool) and `CopyPipeAsync` (trying to allocate pipe buffers with disposed pool). Both indicate the Kestrel server was shutting down while active connections were still being processed.

### 4. Zombie lock prevents host restart

After the MemoryPool crash, the host left behind an empty lock file at `.repoql/host.lock`. The file exists (holding the lock) but contains no PID. Subsequent host start attempts detect the lock, wait 10 seconds for a socket, find none, detect a "zombie" — but cannot evict because there's no PID to kill:

```
[2026-02-13 22:12:00.923 PID 2928 INF] Host lock held but no healthy socket; waiting up to 10s
  for lock holder to start (path=.../.repoql/host.lock).
[2026-02-13 22:12:10.948 PID 2928 WRN] Zombie detected (lock held, no socket after 10s)
  but host.pid is missing or empty. Cannot evict.
[2026-02-13 22:12:10.950 PID 2928 INF] Host lock held by another process; exiting implicit host start.
```

This repeated for PID 3904 at `22:37:57` with the identical pattern. The host is permanently stuck — every new process sees the zombie lock, can't evict it, and exits. The only recovery is manual deletion of `.repoql/host.lock`.

**Verified:** The lock file exists and is empty (0 bytes). No `host.pid` file exists. The host cannot self-recover from this state.

### 5. `help://` unavailable throughout entire session

**Observed:** `help://** => tree: headlines` returned "No files matched" on the very first query of the session. The agent was following the recommended onboarding instruction from the tool description, which explicitly says:

> **Best 3k tokens you'll ever spend.** Read the map first:
>   `read("help://** => tree: headlines", 3000)`

The agent followed this instruction. It failed. The agent moved on and never tried again until prompted by the user hours later, at which point the host was in the zombie lock state.

**Unknown:** Whether `help://` was ever available during the session, and what caused it to be unavailable. The `indexing_diagnostics()` function exists and would answer this, but the host is currently unrecoverable without manual lock file deletion.

**Impact regardless of cause:** The recommended onboarding path was broken. The agent gave up on documentation after one failed attempt and spent the entire session self-discovering features through `duckdb_functions()` and `information_schema.tables`. This is the most consequential operational issue — every discoverability problem documented in this report may trace back to `help://` being unavailable on the first call.

---

## Session Statistics

- **Duration:** ~2 hours of intensive exploration
- **Queries executed:** ~80
- **Tools discovered mid-session:** ~30 (most via `duckdb_functions()`)
- **Views discovered mid-session:** ~40 (via `information_schema.tables`)
- **Circuit breaker trips:** 2 (from parallel queries and OOM)
- **Features used that were documented in tool description:** ~30%
- **Features used that required self-discovery:** ~70%
