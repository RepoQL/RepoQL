# RepoQL Open Vocabulary Registry

**Scope:** `node.kind`, `edge.type`, `annotation.kind`, `annotation.severity`, selected SemType `kind` values

> **Why this exists.** RepoQL’s core schema uses **open vocabularies** (string tags) for kinds and relationship types. Pinning them here ensures **stable queries**, **agent behavior**, and **policy gates** across time—while keeping the base tables unchanged per our extension philosophy. 

---

## 1) Contracts & non‑goals (normative)

* **Source of truth:** This document + the **machine‑readable registry** in `Docs/vocab/registry.json`. Producers **must** emit only values defined here (or values marked *provisional* under a recorded proposal). Consumers **must not** hard‑code anything outside this registry.
* **No schema churn:** Vocab evolution happens here (and in views/macros), **not** by altering base tables.
* **URI & SemType are separate contracts:** RepoURI is the **locator**; SemType routes **parsing/semantics**. Vocab values cross‑reference both but do not replace them. 

---

## 2) Naming rules (normative)

**`node.kind`**

* **Format:** lowercase; segments `a–z 0–9 _ . -` (`^[a-z][a-z0-9._-]*$`)
* **Examples:** `document`, `md_heading`, `csharp.type`, `openapi.operation` (maps 1:1 with modeled items in documents). 

**`edge.type`**

* **Format:** UPPER\_SNAKE\_CASE (`^[A-Z][A-Z0-9_]*$`)
* **Composition:** `HAS_PART` (the only composition type in this registry); single parent enforced at schema level.
* **References:** curated list (see §4).

**`annotation.kind`**

* **Format:** lowercase; dot optional; same charset as `node.kind`
* **Examples:** `lint`, `outline`, `metric`, `metadata`, `change`, `trace.failure`. 

**`annotation.severity`**

* **Allowed values:** `hint | info | warning | error` (mapped by `_severity_rank` UDF).

**`annotation.source`**

* **Format:** lowercase slug; suggest `producer-name` (e.g., `markdown-parser`, `repoql-docs`, `eslint`).

**SemType `kind` parameter**

* **Format:** dot‑path, lowercase (`^[a-z][a-z0-9._-]*$`)
* **Examples:** `markdown.doc`, `openapi`, `playwright.trace`, `cs.class`.

---

## 4) Registered values (authoritative)

### 4.1 `edge.type`

| Type                | Status      | Kind | Semantics                                                                  | Notes                              |
| ------------------- | ----------- | ---- | -------------------------------------------------------------------------- | ---------------------------------- |
| `HAS_PART`          | **stable**  | comp | Ownership/containment; enforces **single parent** (composition tree).      | Composition order via `ordinal`.   |
| `REFERS_TO`         | **stable**  | ref  | Source references destination (e.g., md\_link → md\_heading, symbol refs). | Markdown anchor links model this.  |
| `CALLS`             | provisional | ref  | Source callable invokes destination callable.                              | Used in examples/patterns.         |
| `IMPORTS`           | provisional | ref  | Source module/file imports destination module/symbol.                      | Patterns/queries use this.         |
| `IMPLEMENTS`        | provisional | ref  | Class implements interface.                                                | Example queries.                   |
| `OVERRIDES`         | provisional | ref  | Method overrides base declaration.                                         | Example queries.                   |
| `TESTS`             | provisional | ref  | Test case/suite exercises destination code.                                | For test/result ingestion.         |
| `DOCUMENTS`         | provisional | ref  | Document (or section) documents a code entity or spec node.                | API coherence tranche.             |
| `MATCHES_OPERATION` | provisional | ref  | HTTP example/trace matches an OpenAPI operation.                           | API coherence + HAR.               |
| `DEPENDS_ON`        | provisional | ref  | Source artifact depends on destination package/module/service.             | For SBOM/lockfiles.                |
| `USES_SECRET`       | provisional | ref  | Workflow/deployment consumes a secret.                                     | CI/k8s tranche.                    |
| `RUNS_IMAGE`        | provisional | ref  | Workflow step/container uses a container image.                            | CI tranche.                        |
| `DEPLOYS`           | provisional | ref  | Manifest deploys component.                                                | Infra tranche.                     |
| `EXPOSES_PORT`      | provisional | ref  | Manifest/compose exposes port.                                             | Infra tranche.                     |

> **Guidance:** Add new reference types sparingly; prefer data in `annotation.data` when semantics are not graph‑wide. Composition **must** remain a single, universal type (`HAS_PART`).

---

### 4.2 `node.kind`

| Kind                | Status      | Class       | Typical parent doc SemType                  | Notes                                              |
| ------------------- | ----------- | ----------- | ------------------------------------------- | -------------------------------------------------- |
| `document`          | **stable**  | container   | varies                                      | Only nodes with a persistent `uri`.                |
| `md_heading`        | **stable**  | md item     | `text/markdown;kind=markdown.doc`           | Includes `level`, `text`, `slug` props; has span.  |
| `md_code_block`     | **stable**  | md item     | `text/markdown;kind=markdown.doc`           | Includes `language`, `lines`.                      |
| `md_link`           | **stable**  | md item     | `text/markdown;kind=markdown.doc`           | `properties.href/text` set; may have `REFERS_TO`.  |
| `csharp.namespace`  | **stable**  | code symbol | `text/plain;kind=code.csharp`               | C# namespace declaration with qualified name.      |
| `csharp.type`       | **stable**  | code symbol | `text/plain;kind=code.csharp`               | C# type (class/struct/interface/record/enum).      |
| `csharp.member`     | **stable**  | code symbol | `text/plain;kind=code.csharp`               | C# member (method/property/field/event).           |
| `py_function`       | provisional | code symbol | `text/x-python;kind=py.module`              | From patterns.                                     |
| `openapi.operation` | provisional | spec node   | `application/yaml;kind=openapi;version=3.1` | For API coherence tranche.                         |
| `openapi.schema`    | provisional | spec node   | `application/yaml;kind=openapi;version=3.1` | For joining to code/docs.                          |
| `trace.step`        | provisional | trace node  | `application/zip;kind=playwright.trace`     | For Playwright traces.                             |
| `trace.screenshot`  | provisional | trace node  | `application/zip;kind=playwright.trace`     | Used by `trace.failure` annotations.               |
| `ci.workflow`       | provisional | CI node     | `application/yaml;kind=ci.github.actions`   | CI tranche.                                        |
| `ci.job`            | provisional | CI node     | same                                        | —                                                  |
| `ci.step`           | provisional | CI node     | same                                        | —                                                  |

---

### 4.3 `annotation.kind` and severities

| Kind                     | Status      | Typical severity | Purpose / notes                                               |                                                                        |                                                                               |
| ------------------------ | ----------- | ---------------- | ------------------------------------------------------------- | ---------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| `lint`                   | **stable**  | \`warning        | error                                                         | info\`                                                                 | Generic diagnostics incl. SARIF ingestion/export. Respects `_severity_rank`.  |
| `outline`                | **stable**  | `info`           | Writer‑emitted doc outlines (e.g., Markdown).                 |                                                                        |                                                                               |
| `metric`                 | **stable**  | `info`           | Numeric/structured code metrics (complexity, length).         |                                                                        |                                                                               |
| `todo`                   | provisional | \`info           | hint\`                                                        | TODO/FIXME/HACK signals. Patterns reference.                           |                                                                               |
| `test.result`            | provisional | \`error          | info\`                                                        | Case/suite results (`status`, `duration`). Joins to code via `TESTS`.  |                                                                               |
| `coverage`               | provisional | `info`           | Coverage segments/percent; used in gates.                     |                                                                        |                                                                               |
| `trace.failure`          | provisional | `error`          | Playwright step failure (with `screenshot_uri`, `selector`).  |                                                                        |                                                                               |
| `architecture_violation` | provisional | \`warning        | error\`                                                       | Enforce rules like “controller must not touch DB”.                     |                                                                               |
| `change`                 | provisional | `info`           | Rename/move/change facts (`data.old_uri/new_uri`).            |                                                                        |                                                                               |
| `policy`                 | provisional | \`warning        | error\`                                                       | Explicit policy checks (infra/timeouts, pins, etc.).                   |                                                                               |

**Severity mapping (interop)**

* **RepoQL → SARIF**: `error→error`, `warning→warning`, `info|hint→note`.
* **RepoQL → GH Actions logs**: `error→::error`, `warning→::warning`, `info|hint→::notice`.
  (Keep `_severity_rank()` as single source of truth in gates.)

---

### 4.4 SemType `kind` registry (selected)

| SemType (base)                | `;kind=`           | Status      | Notes                                                   |
| ----------------------------- | ------------------ | ----------- | ------------------------------------------------------- |
| `text/markdown`               | `markdown.doc`     | **stable**  | Markdown modeling present today.                        |
| `text/plain`                  | `code.csharp`      | **stable**  | C# source files with full Roslyn analysis.              |
| `application/yaml` or `+json` | `openapi`          | provisional | For API coherence tranche.                              |
| `application/zip`             | `playwright.trace` | provisional | Treat archive entries as children via `jar:` RepoURIs.  |
| `text/x-python`               | `py.module`        | provisional | Examples.                                               |

> **Normalization:** All SemType strings **must** follow the rendering rules (lowercase keys; sorted params; quote non‑tokens).

## 6) Authoring guidance (for producers)

- Emit **composition** as `HAS_PART` only; enforce one parent via schema. Reference edges may be cyclic. 
- Prefer **structure first** (node/edge/span) and add **annotations** (lint/outline/metric/trace/change) early; it powers `xray_*`, `snippet()`, and gates. 
- Address **everything** with **RepoURIs** (files, JSON Pointers, ranges, anchors, archive entries). Keep fragments canonical. 
- Use **SemType** to route parsing/linters; normalize strings per spec.
