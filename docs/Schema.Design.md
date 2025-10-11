# RepoQL Schema — Design Concepts & Rules

**Authoritative guide for contributors, implementers, and agent/tool authors**

> This document defines the **mental model**, **invariants**, **contracts**, and **extension rules** for RepoQL’s schema and query layer. It is intentionally precise and testable, so changes can be validated with SQL alone.

---

## 0) Purpose & non‑goals

**Purpose.** Give one stable, local representation of a repository as a **queryable graph**:

* **Vertices** are `node`s (documents & code/doc elements).
* **Relationships** are `edge`s.
* **Locations** are `span`s.
* **Findings/facts** are `annotation`s.
* **Bytes** live in `artifact`.&#x20;

**Non‑goals.** Avoid per‑feature table churn; evolve through **views/macros/UDFs**, not base table edits. Single‑writer model is a guardrail, not a suggestion.&#x20;

---

## 1) Core object model (what each table means)

### 1.1 `artifact` — content, media type, and x‑ray summaries

* The **only** place that stores bytes/text (`text_content`, `byte_content`, `media_type` as **SemType**) and **x‑ray summaries** (`headline`, `summary`, `structure`). Join through `node.artifact_id` to read text or summary fields.&#x20;
* **SemType** encodes both **wire format** and **representation** (e.g., `text/markdown;kind=markdown.doc;charset=utf-8`). Routing & parsing use this string.&#x20;
* **X‑ray summary fields** enable token-efficient repo exploration. Each field is independent; do not embed lower levels inside higher ones (consumers compose as needed):
  * `headline` (single line): Essential identity, always present for documents.
  * `summary` (~5 lines, max 10): Key information for understanding without reading.
  * `structure` (~15 lines, max 25): Detailed outline for navigation.

### 1.2 `node` — all logical entities

* Every conceptual thing is a node: `document`, `cs.class`, `md_heading`, `openapi.operation`, etc. Identity is `id`, not path. `properties` is JSON for domain metadata; avoid duplicating fields that can be derived.&#x20;
* Document nodes **may** have `uri` (canonical **RepoURI**). Non‑document nodes *don’t*; they’re addressed via their document + span.&#x20;

### 1.3 `edge` — composition vs reference

* `is_composition=true` encodes containment (document → item, class → method) with optional `ordinal` for stable file order.
* `is_composition=false` encodes **arbitrary relations** (`REFERS_TO`, `CALLS`, `IMPLEMENTS`, `MATCHES_OPERATION`, `TESTS`, etc.).&#x20;

### 1.4 `span` — precise localization

* 1‑based `start_line/end_line` and columns; byte offsets also stored for exact slicing. Spans can be line‑only, byte‑only, or both.&#x20;
* Fragments in URIs map to spans: `line=a[,b]` is inclusive lines; `char=a[,b]` is **0‑based, half‑open** `[a,b)`.&#x20;

### 1.5 `annotation` — enrichment layer

* Uniform way to attach **lint**, **outline**, **metric**, **todo**, **trace**, **change** facts to documents/nodes/edges/spans/URIs.
* Minimal canon: `kind`, `severity (hint|info|warning|error)`, `source`, `message`, `data{…}`, plus **target** (node/edge/span/uri) and **scope\_document\_id**. Selection is via stable views/macros like `annotations_*`.&#x20;

* X‑ray structured model: When you want to persist the structured inputs used to generate x‑ray text (for re-rendering or analytics), prefer an annotation rather than new artifact columns. Use `annotation.kind='metadata'`, `severity='info'`, `source='metadata-generator'` (or your producer name), and store the model under `data.model` with optional `data.generator_version` and `data.templates` (template identifiers). Keep `artifact.headline/summary/structure` as compact text only.

---

## 2) Addressability with **RepoURI** (canonical, universal)

**Rule A1 — one locator everywhere.** Use **RepoURI** in all external surfaces (CLI, MCP, SARIF export, patches) and internal joins. It addresses:

* Files (`file:///…`), **archives** (`jar:file:///trace.zip!/entry`),
* **JSON Pointers** (`#/components/schemas/User`),
* **line/char ranges** (`#line=10,20`, `#char=100,150`),
* **anchors** (markdown slugs).&#x20;

**Rule A2 — locator ≠ identity.** URIs are **locators**; canonical identity is `id` (and optionally a content digest). Don’t use URIs as primary keys.&#x20;

**Rule A3 — normalization is normative.**

* Container URIs are absolute, fragment‑free, with dot‑segments removed and percent‑encoding normalized.
* Fragment precedence: **JSON Pointer** > **k=v params** > **simple line/char** > **anchor**. Parsers must ignore unknown params and preserve them on round‑trip.&#x20;

---

## 3) Typing & routing with **SemType**

**Rule T1 — SemType tells both “how” and “what.”**

* Use real MIME (`type/subtype[+suffix]`) for wire format, and `;kind=` for semantic role (`openapi`, `markdown.doc`, `cs.class`, `playwright.trace`).
* Add `;version=`, `;profile=`, `;schema=` when useful; keys lowercase; preserve unknown keys.&#x20;

**Rule T2 — render normalized.** Lowercase keys, sort parameters, quote non‑token values. SemType strings must be deterministic.&#x20;

---

## 4) Composition vs reference — graph semantics

**Rule G1 — single parent for composition.** Each node has **at most one** incoming `is_composition=true` edge. The composition graph is acyclic. Use `ordinal` for natural order. Enforce with a unique `(destination_node_id) WHERE is_composition`.&#x20;

**Rule G2 — composition forms trees of ownership.** Typical path: `document` → (`md_heading` | `cs.class` | `md_link` …). Query these with file‑order using `ordinal` or spans where present.&#x20;

**Rule G3 — references are open‑world.** Reference edges can be cyclic and cross‑document. Keep `type` taxonomy tight and documented (`REFERS_TO`, `CALLS`, `IMPORTS`, `IMPLEMENTS`, `TESTS`, `DOCUMENTS`, `MATCHES_OPERATION`, etc.).&#x20;

---

## 5) Coordinates & snippets — line vs char

**Rule S1 — lines are 1‑based inclusive.** `#line=10,20` spans lines 10 through 20 (inclusive). Columns are 1‑based when known.&#x20;

**Rule S2 — chars are 0‑based half‑open.** `#char=100,150` means `[100,150)`. This aligns with the RepoURI spec and avoids off‑by‑one snares when slicing bytes.&#x20;

**Rule S3 — snippet is the default UX.** Always prefer `snippet(uri, ctx)` over whole‑file reads; it works for **line**, **char**, and even **edge** fragments, and it preserves language hints.&#x20;

---

## 6) Annotations — diagnostics & facts as rows

**Rule N1 — annotations are the universal interchange.** Everything that isn’t structural is an `annotation`: lints, outlines, metrics, test results, coverage, change facts, traces. Keep `data` compact and structured. Selection happens through `annotations_for(...)` and `annotations_all(...)`.&#x20;

**Rule N2 — target resolution is mandatory.** Every annotation must resolve to an actionable target: set `target_span_id` when possible; else `target_node_id`/`target_edge_id`; else **RepoURI** in `target_uri`. A view computes `resolved_target_uri` for downstream consumers.&#x20;

**Rule N3 — severity taxonomy is stable.** Use `hint|info|warning|error` and a single `_severity_rank(text)` UDF for all gates. Don’t invent per‑tool severities in the schema.&#x20;

**Rule N4 — idempotency by `semantic_key`.** Producers must set a stable `semantic_key` (e.g., hash of `(source, rule_id, container, start_line..end_line, normalized message)`), enabling **upsert** and re‑index without duplicates.&#x20;

**Rule N5 — outlines are annotations.** Markdown “outline” is a first‑class annotation (`kind='outline'`, `source='markdown-parser'`), not a separate table; use it for “see without opening files.”&#x20;

---

## 7) Documented example — Markdown modeling (reference)

Markdown shows the pattern:

* `document` node with `artifact.text_content` and SemType `text/markdown;kind=markdown.doc`.
* Composition children: `md_heading`, `md_code_block`, `md_link`; each with **spans**.
* `REFERS_TO` edges from `md_link` to heading targets (for `#anchor`).
* Outline emitted as an `annotation`. All directly queryable via table‑first queries and macros.&#x20;

This is the template for other formats (OpenAPI, traces, CI YAML): **nodes + edges + spans + annotations**, plus a few macros.&#x20;

---

## 8) Macros & UDFs — the stable query surface

**Macos (table‑valued):**

* `xray_documents()` → inventory (file name, media\_kind, byte\_size).
* `xray_items(kinds, max_per_document)` → per‑doc “items”.
* `xray_lines(lod, include_kinds, max_per_document)` → printable outline.
* `annotations_for(uri, kinds, min_severity)` / `annotations_all(kinds, min)` → diagnostics.
* `entities_by_uri(uri)` → resolve any RepoURI to the entities at that location.&#x20;

**UDFs (scalar):**

* `repository_uri_*` helpers (join, split, container, fragment ops), including JSON Pointer handling and range composition.
* `node_display_label(kind, properties)` for friendly labels.
* `_severity_rank(text)` for gates.&#x20;

> Principle: add features by adding **macros/UDFs**; keep base tables intact. This is explicit in the contributor vision.&#x20;

---

## 9) Integrity constraints & recommended indexes

**Identity & uniqueness**

* **U1**: `node(uri)` is unique **only** for `kind='document'`. (Other nodes do not carry `uri`.)
* **U2**: `edge(destination_node_id) WHERE is_composition = TRUE` is unique → single parent.
* **U3**: `annotation(semantic_key)` unique (or partial unique on `(source, rule_id, target_span_id|target_uri, scope_document_id)`) → idempotent ingestion.&#x20;

**Referential integrity**

* **R1**: `node.artifact_id` FK → `artifact.id` (nullable).
* **R2**: `span.document_id` FK → `artifact.id`; `node.span_id` FK → `span.id` (nullable).
* **R3**: `edge.(source_node_id, destination_node_id)` FK → `node.id`.
* **R4**: `annotation.target_*` FKs are nullable; at least one target or `target_uri` must be set.&#x20;

**Indexes (minimum viable)**

* `node(kind)`, GIN/GIST on `node.properties`.
* `edge(source_node_id)`, `edge(destination_node_id)`, `(is_composition, type)`.
* `span(document_id)`.
* `annotation(kind, severity)`, GIN on `annotation.data`.
* `artifact(media_type)` for routing.&#x20;

---

## 10) Producer rules (how to add new content safely)

**P1 — emit the core, not custom tables.** Produce `node`, `edge`, `span`, `annotation` rows; route by **SemType**; use RepoURI in targets. Never propose new base tables for one format.&#x20;

**P2 — composition first, references later.** Start by building the document→items tree with spans, then add reference edges (`REFERS_TO`, `MATCHES_OPERATION`, …). This ensures `xray_*` works on day one.&#x20;

**P3 — annotations for value.** Emit `kind='lint'|'metric'|'outline'|'change'|'trace'|…` as soon as you can. Selection, UX, CI gates, and exports (SARIF/GitHub) all flow from annotations uniformly.&#x20;

**P4 — idempotent upserts.** Always set `semantic_key` on edges and annotations to dedupe re‑ingestion; prefer deterministic ordering (use `ordinal` for children).&#x20;

---

## 11) Consumption rules (how agents/CLIs/CI should use it)

**C1 — prefer structure over bytes.** Use `xray_*`, `entities_by_uri`, `annotations_*`; call `snippet()` when bytes are needed. This preserves token budgets and avoids brittle parsing.&#x20;

**C2 — policy as SQL.** All gates are queries over `annotations` (e.g., “until empty” on `kind='lint' AND severity≥warning`) — no special APIs.&#x20;

**C3 — RepoURI everywhere.** Surface `resolved_target_uri` in UIs, logs, SARIF, and GitHub workflow commands; it round‑trips through archives and JSON Pointers.&#x20;

---

## 12) Concurrency & lifecycle

**L1 — single writer, many readers.** All writes go through the writer process; watchers keep the DuckDB index fresh. Readers (CLI, agents) issue concurrent queries. This is a hard requirement for correctness.&#x20;

**L2 — re‑ingest is safe.** With `semantic_key` idempotency and uniqueness constraints, producers can re‑scan without duplicating rows.&#x20;

---

## 13) Validation queries (conformance checks)

Use these to assert schema invariants in CI:

**Single‑parent composition (no orphans with 2+ parents)**

```sql
SELECT destination_node_id, COUNT(*) AS parents
FROM edge
WHERE is_composition = TRUE
GROUP BY destination_node_id
HAVING COUNT(*) > 1;
```

**Dangling spans**

```sql
SELECT s.id
FROM span s
LEFT JOIN artifact a ON a.id = s.document_id
WHERE a.id IS NULL;
```

**Annotations without resolvable targets**

```sql
SELECT id, kind, message
FROM annotation
WHERE target_span_id IS NULL
  AND target_node_id IS NULL
  AND target_edge_id IS NULL
  AND (target_uri IS NULL OR target_uri = '');
```

**Non‑normalized SemTypes** (example heuristic)

```sql
SELECT id, media_type
FROM artifact
WHERE lower(media_type) != media_type;
```

(All of these patterns are consistent with the schema and contributor guidance.) &#x20;

---

## 14) Example: table‑first Markdown queries (worked, reference)

* Items in file order with levels and spans.
* Intra‑document link resolution with `REFERS_TO`.
* Outline annotation retrieval for “see without opening files.”
  These concrete recipes demonstrate the schema in action and should be used as fixtures in tests.&#x20;

---

## 15) Interop rules (exports & imports)

* **Export diagnostics**: project `annotations` to **SARIF 2.1.0** (one run per `source` or a single aggregator), and to **GitHub Actions workflow commands** for streaming logs; always carry the **RepoURI** in a property for exactness (archives & JSON Pointers). &#x20;
* **Import diagnostics**: ingest SARIF into `annotation(kind='lint')` with fingerprints and `resolved_target_uri`. The schema already expects this pattern.&#x20;

---

## 16) Anti‑patterns to avoid

* **Adding tables for features.** Resist; prefer producers + views/macros/UDFs + annotations. The **vision** is explicit: the core is stable.&#x20;
* **Embedding bytes in nodes.** Bytes belong in `artifact`; nodes carry identity and metadata.&#x20;
* **Path‑as‑identity.** URIs are locators, not keys; use `id` and/or digest.&#x20;
* **Whole‑file reads for UX.** Use `snippet()` and `xray_*` instead; it’s faster, safer, and more agent‑friendly.&#x20;

---

## 17) Roadmap for schema layer (unchanged core; richer views)

* Grow **macros** (`references_of(...)`, `owners_of(...)`, `impacted_tests(...)`) and **UDFs** (YAML pointer, URI helpers).
* Add **content producers** (OpenAPI, traces, CI YAML) and emit **annotations** for policy gates & autofix.
* Keep the **base tables** unchanged; evolve interfaces additively (more fields are OK; renaming is not). This trajectory is consistent with the vision document.&#x20;

---

## 18) Schema TL;DR (for new contributors)

1. **Everything is a node; relationships are edges; locations are spans; insights are annotations.** Query first, not parse first.&#x20;
2. **RepoURI** is the one true locator (files, archives, JSON Pointers, ranges, anchors). **SemType** is the one true routing hint. &#x20;
3. Keep **base tables stable**; add **macros/UDFs/views** and **producers** to extend. Guard with uniqueness, FK, and severity rank. &#x20;
4. **Prefer structure over bytes** and **annotations over ad‑hoc tables**. Ship gates as queries and use `snippet()` for UX.&#x20;
