# RepoQL — Local Repo Graph & Automation

**Vision, Strategy, and Implementation Blueprint**

> **Purpose.** This report codifies the product vision and a concrete plan for RepoQL: a **local, queryable knowledge graph** for repositories with **precise addressing** and a **tiny, stable surface area** that powers agents, CLIs, CI, and editor hooks. It covers the **interchange strategy**, **format/content tranches**, **MCP & CLI use cases**, **hooking & protocols**, and a pragmatic rollout path.

---

## 0) Executive summary

* **What it is.** A **local** (no remote server) repo graph in **DuckDB** with a **single writer + watchers** that keeps the index fresh. Exposed via **gRPC over UDS** and a **CLI**, with a **SQL‑first** query model, stable **macros/UDFs**, and **URI‑precise** navigation.
* **Core model.** Everything in the repo is a **node**; relationships are **edges**; exact locations are **spans**; lint, metrics, and “facts” are **annotations**. A small macro set (e.g., `explore_*`, `snippet`, `annotations_*`) is the default entry point for agents and tools.
* **Why it matters.** Unifies **docs, code, config, traces** under one query plane; enables **policy‑as‑SQL** gates; supports **auto‑fix** for the safe class (e.g., doc link hygiene); provides **deterministic** agent loops (select → fix → verify “until empty”). 
* **How it evolves.** Keep base tables stable; add capabilities via **views/macros/UDFs** and new **annotation producers**. Ship **interchange formats** so external systems consume findings/patches without coupling.

---

## 1) Architectural overview (what’s fixed; what’s flexible)

**Fixed (contracts not to break):**

* **Schema tables**: `artifact`, `node`, `edge`, `span`, `annotation`. Idempotent upserts via `semantic_key`. **URIs live only on document nodes;** fragments resolved at query time.
* **Addressing**: **RepoURI** (files, JSON Pointers, line/char ranges, anchors, archive entries including nested). Round‑trip rules and normalization are defined.
* **Typing**: **Semantic Media Type (SemType)**; MIME + parameters (e.g., `kind`, `version`). Routing is data‑driven.
* **Query surface**: small, stable views/macros/UDFs (`Files`, `Functions`, `Types`, `Annotations`, `explore()`, `search()`, `snippet`, `annotations_*`, `repository_uri_*`).

**Flexible (how we extend safely):**

* Add **producers** that ingest/parse bytes (including archives, composite file systems) and emit nodes/edges/spans/annotations.
* Add **views/macros** for new projections; add **UDFs** for deterministic scalar logic.
* Evolve **CLI/gRPC** outputs additively (fields, formats), keeping them AOT‑friendly and portable.

---

## 2) Interchange strategy (make complexity disappear at the edges)

Standardize **four** small interchanges so every producer and consumer speaks the same dialects.

### 2.1 Addressing: **RepoURI (canonical)**

* Single locator for everything (files, JSON Pointer, anchors, line/char ranges, archive entries).
* Used **everywhere**: annotations, CLI output, GUI links, patches, SARIF export.
* Guarantees deterministic `snippet(uri, k)` previews and precise patch anchoring. 

### 2.2 Typing: **SemType (routing)**

* Media type + parameters; especially `kind` and `version`.
* Enables adding new content (e.g., `application/zip;kind=playwright.trace`) **without schema churn**; parsers and linters choose behavior from this string.

### 2.3 Diagnostics: **RepoDiagnostic (internal) → SARIF & GH workflow (external)**

* **Internal**: use the `annotations` view shape as the canonical diagnostic (source, rule\_id, severity, message, **resolved\_target\_uri**, data, fingerprints).
* **Emitters**:

    * **SARIF 2.1.0**: one run per source *or* an aggregated run; map `severity→level`; carry fingerprints; translate auto‑fixes to `fixes[]`.
    * **GitHub Actions workflow commands** (`::warning` / `::error`) for streaming logs.
    * *(Optional)* LSP `PublishDiagnostics` for editors.
* This keeps policy and agent loops **query‑based** while making outputs **tool‑compatible**.

### 2.4 Patches: **RepoPatch (span‑anchored)**

A tiny JSON we can project into SARIF fixes, unified diff, or “GitHub suggested changes”.

```json
{
  "patches": [{
    "id": "…",
    "uri": "file:///…#line=42,42",               // or #char=…
    "precondition": {"sha256": "…"},             // optional safety
    "edit": {"type": "text-replace", "text": "…"}  // or {"type":"json-set","pointer":"/a/b","value":…}
  }]
}
```

* **RepoURI** anchors edits; **JSON Pointer** supports structured updates (OpenAPI/config) without brittle text ops.
* DB stays **read‑only**; CLI applies patches; watchers re‑lint; **verify** re‑queries “until empty”. 

### 2.5 Results & bulk export

* **Tests** (JUnit/xUnit/Jest), **Coverage** (LCOV/Istanbul/Cobertura), **SBOM/Deps** (SPDX/CycloneDX), **Scanners** (ingest **SARIF**) → normalized to **annotations** for gating/UX.
* **NDJSON** for streaming; **CSV/Parquet** for reports. Keep outputs AOT‑friendly and small.

---

## 3) Content & feature tranches (compounding value)

Ship in **tranches**—each bundle stands alone but compounds when combined.

### Tranche 1 — **Docs Hygiene & Navigation** (foundation)

* Model **Markdown**: `md_heading`, `md_link`, `md_code_block` nodes; `HAS_PART` and `REFERS_TO` edges; outline annotation; spans everywhere; SemType `text/markdown;kind=markdown.doc`.
* Lints: `broken-link`, `heading-slug-mismatch`, `missing-code-fence-language`, `no-final-newline`.
* **Autofix**: repair links, normalize slugs/fences/newlines.
* Why: gives agents & humans “**see without opening files**” via `explore_*` + precise `snippet()`. 

### Tranche 2 — **API Contract Coherence**

* OpenAPI (`application/yaml;kind=openapi;version=3.1`) + code symbols (controllers/routes) + docs.
* Edges: `MATCHES_OPERATION` (code↔spec), `DOCUMENTS` (spec↔docs), `USES_EXAMPLE` (spec↔HAR/Postman).
* Lints: `missing-operationId`, `undocumented-endpoint`, `example-drift`.
* **Autofix**: synthesize `operationId`, update examples, repair doc anchors (`#/…`). 

### Tranche 3 — **Test Triage, Coverage & Traces**

* Ingest **JUnit/xUnit/Jest** → `annotation(kind='test.result')`; **Coverage** → `annotation(kind='coverage')`.
* Treat **Playwright traces** as archives: `application/zip;kind=playwright.trace` with steps/screenshots/logs as children; annotate `trace.failure`.
* Edges: `TESTS` (test→code), `REFERS_TO` (trace step → selector/screenshot).
* Why: unlock **impacted‑tests only** after fixes/refactors; **join traces to code/docs**.

### Tranche 4 — **CI/Infra Policy**

* GitHub Actions/GitLab/Azure YAML; Docker Compose; Kubernetes/Helm.
* Lints: `no-timeout`, `unpinned-action`, `latest-tag`, `missing-resource-limits`, `unscoped-secret`.
* **Autofix** (safe class): add timeouts/concurrency/resource requests; suggest pins.

### Tranche 5 — **Security & Supply Chain (Unified)**

* **Ingest SARIF** (any scanner) → lint annotations with fingerprints; **SBOM/lockfiles** → `DEPENDS_ON` graph + license/vuln annotations.
* One **policy‑as‑SQL** gate across all scanners; optional safe bump PRs. 

### Tranche 6 — **Architecture & Change Intelligence**

* Architecture rules as annotations (e.g., “controller must not touch DB directly”), powered by `edge.type` patterns; “change facts” as annotations (`data.old_uri/new_uri`).
* Why: **impact‑aware** gating and prioritization without schema churn. 

*(A “Build/Perf” tranche can follow—Bazel/MSBuild/Webpack + OTel traces for size/time budgets—as capacity allows.)*

---

## 4) CLI design (tiny surface; big leverage)

**Principles**: single, consistent UX; **RepoURI everywhere**; deterministic JSON/NDJSON for machines; human‑friendly text for local use.

### Core commands

* `repoql query [--format {table,json,ndjson,csv,parquet}] <SQL…>`
  Raw SQL with schema in JSON header; supports streaming.
* `repoql explore [--intent Find|Explore|Understand] [--tokens N] [--scope <glob>]`
  Uses `explore()` UDF for token-budgeted exploration. Great as *SessionStart* context for agents.
* `repoql lint [--min-severity warning] [--format {table,json,sarif,gha}] [--group-by source] [--include-fixes]`
  One SELECT over `annotations_all('lint', …)`; render as **SARIF** or GitHub **workflow commands**.
* `repoql fix [--rule <id>] [--uris …] [--dry-run|--apply] [--commit "msg"] [--export {sarif,diff,json}]`
  Applies **RepoPatch** safely (descending byte order; preconditions); can export as SARIF fixes.
* `repoql verify`
  Runs your **policy gates** (just two or three SQL queries); non‑zero exit if blockers remain.
* `repoql ingest <format> <path>`
  Import **SARIF**, **JUnit/Jest**, **LCOV**, **SBOM**; translate to annotations and nodes/edges as needed.
* `repoql snippet <RepoURI> [--ctx 5]`
  Human‑friendly focused window via `snippet()`.

**Flags to standardize**: `--min-severity`, `--relative-to $REPO_ROOT`, `--uri-base-id REPOROOT`, `--timeout-ms`, `--limit`, `--json`.

---

## 5) MCP tools & agent posture (minimal instructions, maximal outcomes)

**Agent goal:** Learn 4 primitives and do everything else by composition.

### Tools (server‑side)

* `repoql.query(sql, params?, max_rows?, timeout_ms?)` → rows.
* `repoql.explore(include_kinds?, max_per_doc?, lod?)` → outlines & items (LLM‑friendly).
* `repoql.annotations.list(kinds?, min_severity?)` → the work queue.
* `repoql.fix.generate(rule_ids?, uris?, limit?)` → **RepoPatch** JSON.
* `repoql.verify()` → boolean / rows that violate policy.

**Default cold‑start plan for agents**

1. `SELECT * FROM Files` to map the repo; use `explore()` for token-budgeted exploration.
2. Pull `annotations_all('lint','warning')` to select work; preview with `snippet(resolved_target_uri, 3)`.
3. Call `fix.generate` for safe rules, apply via CLI; **verify** until **empty**.

> This exactly matches your guardrails: keep base tables stable; evolve with macros/UDFs; rely on URIs and spans for determinism.

---

## 6) Hooks & protocols (determinism locally; portability across agents)

**Claude Code hooks** (or analogous IDE hooks)

* *SessionStart*: inject `Files` view summary + top `lint` rows into context (keep token‑light).
* *PreToolUse (Write)*: run `repoql verify`; **block** on violations; print human‑readable rows.
* *PostToolUse (Write)*: run `repoql fix` for safe rules (broken links, MD hygiene), then `verify` again.
* *UserPromptSubmit*: add a short policy summary (counts, most severe) so the model plans with guardrails.
  Hooks give you **local determinism** regardless of the agent’s internal plans.

**A2A (agent‑to‑agent) shim**

* Publish an **Agent Card** with capabilities: `repoql.query`, `repoql.explore`, `repoql.lint.queue`, `repoql.fix.generate`.
* Accept artifacts tagged by **SemType**; return rows/patches/diagnostics as **JSON** using RepoURI/RepoPatch conventions.
  This enables orchestration across ecosystems while keeping RepoQL local and sovereign.

---

## 7) Policy‑as‑SQL gates (universal, explainable)

**Examples** (CLI `repoql verify` just runs these):

* **Block on errors or high‑priority warnings**

  ```sql
  SELECT COUNT(*)=0 AS pass
  FROM annotations_all('lint','warning')
  WHERE severity='error' OR (severity='warning' AND source IN ('help'));
  ```

  *(deterministic queue; re‑run “until empty” after fixes)*

* **API hygiene (Tranche 2)**

  ```sql
  SELECT COUNT(*)=0 AS pass
  FROM annotations_all('lint','warning')
  WHERE rule_id IN ('missing-operationId','undocumented-endpoint','example-drift');
  ```



* **Coverage + test blockers (Tranche 3)**

  ```sql
  SELECT COUNT(*)=0 AS pass
  FROM annotations_all('coverage,test.result','info')
  WHERE (kind='coverage' AND (data->>'pct')::int < 70)
     OR (kind='test.result' AND data->>'status'='failed');
  ```



---

## 8) Documentation & navigation UX (leverage what you have)

* Markdown modeling already includes headings, links, code blocks, spans, and an **outline annotation**; combine with `explore_*` for structured discoverability. This is your **agent‑ready** context surface.
* Use `entities_by_uri()` and `snippet()` for exact jumps and previews everywhere (CLI, editor, PR bots).

---

## 9) Operational concerns (performance, correctness, safety)

* **Single‑writer + watchers**: maintain DuckDB integrity; readers are concurrent; re‑lint after write to stabilize annotations before `verify`.
* **Idempotency**: use `semantic_key` on `annotation`/`edge` for upserts; ensure **RepoPatch** edits are idempotent (re‑apply is no‑op).
* **Normalization**: one RepoURI parser/renderer; no hand‑rolled paths; archive and JSON Pointer forms must round‑trip.
* **Spans**: 1‑based lines; 0‑based chars; CRLF policy documented; apply multiple edits per file in **descending byte order**.
* **AOT‑friendly IO**: Spectre for text, `Utf8JsonWriter` for JSON; stream NDJSON for big outputs.

---

## 10) Example queries (ready to paste)

* **Repo inventory**

  ```sql
  SELECT name, lang, headline FROM Files ORDER BY lower(name);
  ```



* **Outline + lint for a file**

  ```sql
  SELECT severity, source, rule_id, message, resolved_target_uri
  FROM annotations_for('file:///repo/README.md', 'lint,outline', 'info');
  ```



* **Jump to a location**

  ```sql
  SELECT * FROM entities_by_uri('file:///src/app.cs#line=42');
  SELECT line_number, text, is_focus FROM snippet('file:///src/app.cs#line=42', 4);
  ```



* **Docs: headings & links**

  ```sql
  SELECT s.start_line, h.properties->>'level' AS lvl, h.properties->>'text' AS heading
  FROM node doc
  JOIN edge e ON e.source_node_id=doc.id AND e.is_composition AND e.type='HAS_PART'
  JOIN node h ON h.id=e.destination_node_id AND h.kind='md_heading'
  JOIN span s ON s.id=h.span_id
  WHERE doc.uri='file:///repo/README.md'
  ORDER BY s.start_line;
  ```



---

## 11) Adoption blueprint (phased, pragmatic)

**Phase A (4–6 weeks):**

* Ship **Tranche 1** (Markdown hygiene + outline) and **SARIF/GHA emitters**.
* CLI: `lint`, `fix` (safe rules), `verify`, `explore`, `snippet`.
* Hook **pre‑write verify** and **post‑write autofix** in Claude Code / editor tasks.
* KPI: % of broken links auto‑fixed; mean time to green (lint).

**Phase B (6–10 weeks):**

* **Tranche 2** (OpenAPI coherence) + **Tranche 3** (tests/coverage/traces ingestion).
* Impacted‑tests selection via edges; add `repoql ingest junit|lcov|trace`.
* KPI: test time reduction via impacted runs; # of example‑drift fixes.

**Phase C (10–16 weeks):**

* **Tranche 4 & 5** (CI/infra policy, SARIF ingest, SBOM).
* Add **A2A shim** to expose capabilities to external agent hubs; polish **verify** gates for CI.
* KPI: decrease in CI red causes from policy/security; SARIF interoperability wins.

---

## 12) Vision fit (why this is the right shape)

* **Local & sovereign.** All analysis and fixes happen inside the repo boundary; DuckDB in `.repoql/` with a UDS gRPC service; easy to spin up, easy to trust.
* **Relational + addressable.** SQL over a graph (nodes/edges/spans/annotations) + RepoURI gives you **precise**, **explainable** automation. 
* **Composable.** New capability? Add a **producer** and a **view**; no schema churn; agents and CLIs keep working because the **interchange** stays the same.
* **Agent‑ready.** The macro set is intentionally **LLM‑friendly**; “**see without opening files**” + snippets keeps plans focused and token‑light. 

---

## 13) Risks & mitigations

* **Span drift / line ending differences** → Normalize line endings; apply patches in **descending byte order**; preconditions (file hash or sentinel window).
* **Path normalization (OS variance)** → Only use RepoURI; keep one parser/renderer; never guess paths.
* **Annotation bloat** → Keep `data` compact; dedupe via `semantic_key`; expire low‑value hints via `expires_at`.
* **Scope creep** → Hold the line on **base schema stability**; all growth via macros/UDFs/views and producers.

---

## 14) Appendix — one‑page agent primer (drop‑in)

> *You have four calls:*
>
> 1. `Files` view → list of docs with lang/size/headline/summary
> 2. `explore(keywords, intent, tokens)` → token-budgeted exploration
> 3. `annotations_all(kinds,min)` → the work queue (e.g., `'lint','warning'`)
> 4. `snippet(uri, ctx)` → focused preview for any **RepoURI**
>    Use URIs as canonical keys; never fuzzy‑match paths. Prefer structure first; fetch bytes only via `snippet`. “Done” means the selection query turns **empty**. 

---

**Bottom line:** This plan keeps the **core** small and unchanging while pushing power to the **edges** through a handful of **interchange formats** and **LLM‑friendly macros**. It lets you **detect, fix, and verify** across docs, code, configs, and traces—all **locally**, all **addressable**, all **explainable**.
