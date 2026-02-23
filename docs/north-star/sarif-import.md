---
description: Vision for importing SARIF analysis results into the graph as queryable annotations
tags: [sarif, import, annotations, lint, security, static-analysis, snyk, sonarcloud, qodana]
audience: { human: 50, agent: 50 }
purpose: { north-star: 100 }
---

# SARIF Import: What Great Looks Like

> An agent should be able to query every finding from every scanner through one surface — without knowing which tool produced it.

An agent runs `import("sarif:///path/to/snyk-results.sarif")` and the findings land in the graph as annotations — scoped to the right files, targeting the right lines, carrying severity, rule IDs, and fix suggestions. The agent doesn't parse JSON, doesn't map file paths, doesn't reconcile coordinate systems. It says "import this" and then asks "what's critical?" with SQL. Later, SonarCloud results arrive. Then Qodana. Then CodeQL. Each import takes one call. The agent queries all of them uniformly: `SELECT * FROM annotations WHERE kind = 'lint' AND severity = 'error'` — and gets every critical finding from every scanner, deduplicated, with locations that resolve to the actual code. It writes a policy gate in three lines of SQL that blocks on any unresolved high-severity finding regardless of which tool found it. The scanners disagree on severity? The agent sees both assessments side by side and decides. A finding was fixed? The next import knows — the old annotation expires, the graph stays clean.

---

## Ingestion

- An agent should be able to import a SARIF file with a single call and have all results become queryable annotations
- An agent should be able to import SARIF from any compliant producer — Snyk, SonarCloud, Qodana, CodeQL, Semgrep, ESLint, Roslyn, or any tool that writes SARIF 2.1.0
- An agent should be able to import multiple SARIF files and have their results coexist without collision
- An agent should be able to re-import updated results from the same scanner and have the graph reflect the latest state — old findings replaced, new findings added, fixed findings gone

```
import("sarif:///build/snyk-results.sarif")
import("sarif:///build/sonarcloud.sarif")
import("sarif:///build/qodana.sarif")

-- All findings from all scanners, one query
SELECT source, severity, rule_id, message, resolved_target_uri
FROM annotations WHERE kind = 'lint'
ORDER BY severity_rank DESC
```

---

## Location Resolution

- An agent should be able to trust that SARIF locations resolve to the correct documents and line ranges in the graph
- An agent should be able to see findings anchored to symbols when the SARIF result targets a known symbol's span
- An agent should be able to query findings for a specific file and get results from all scanners that reported on it
- An agent should be able to trust that unresolvable locations are reported honestly — a finding without a valid target is still ingested, with the resolution failure visible

```sql
-- "What did scanners find in AuthService?"
SELECT source, severity, rule_id, message, resolved_target_uri
FROM annotations_for('file:///src/Auth/AuthService.cs', 'lint', 'info')
```

---

## Provenance

- An agent should be able to tell which scanner produced each finding
- An agent should be able to see when results were imported and which SARIF run they came from
- An agent should be able to distinguish findings from different scanners that flag the same location
- An agent should be able to filter findings by scanner, by run, or by import batch

```sql
-- "What does each scanner think about this codebase?"
SELECT source, severity, count(*) as findings
FROM annotations WHERE kind = 'lint'
GROUP BY source, severity
ORDER BY source, severity_rank DESC
```

---

## Deduplication

- An agent should be able to trust that re-importing the same SARIF file doesn't create duplicate findings
- An agent should be able to trust that findings carry stable identities — SARIF fingerprints become semantic keys
- An agent should be able to see when the same code location is flagged by multiple scanners and compare their assessments
- An agent should be able to correlate findings across scanners without manual matching

---

## Rich Data

- An agent should be able to access the full SARIF result data — not just message and severity, but properties, tags, code flows, and related locations
- An agent should be able to see fix suggestions when the SARIF result includes them
- An agent should be able to access help URIs and rule descriptions to understand what a finding means
- An agent should be able to query structured properties from the SARIF data payload via SQL

```sql
-- "Which findings have auto-fix suggestions?"
SELECT rule_id, message, resolved_target_uri, data->'$.fixes' as fixes
FROM annotations
WHERE kind = 'lint' AND json_array_length(data->'$.fixes') > 0
```

---

## Lifecycle

- An agent should be able to import fresh results and have stale findings from the same scanner automatically expire
- An agent should be able to see the age of findings — which are new this scan, which persisted from before
- An agent should be able to clear all findings from a specific scanner without affecting others
- An agent should be able to trust that the graph never shows findings from a scanner whose results have been fully superseded

---

## Policy

- An agent should be able to write a quality gate as a SQL query over annotations — no special API, no configuration language
- An agent should be able to enforce thresholds that span all scanners uniformly
- An agent should be able to build gates that combine scanner findings with other graph data — "no critical vulnerabilities in files changed this week"

```sql
-- Policy gate: fail if any error-severity finding exists
SELECT count(*) = 0 AS passes
FROM annotations
WHERE kind = 'lint' AND severity = 'error'

-- Richer: fail if critical findings exist in recently changed files
SELECT count(*) = 0 AS passes
FROM annotations a
JOIN git_diff('HEAD~5') g ON lower(a.scope_document_uri) = lower(g.uri)
WHERE a.kind = 'lint' AND a.severity = 'error'
```

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Import any SARIF with one call | Zero friction between scanner output and queryable findings |
| Query all scanners through one surface | Policy doesn't care which tool found the problem |
| Findings resolve to graph locations | Navigate from finding to code to fix without path manipulation |
| Re-import replaces stale findings | The graph always reflects current reality |
| Fingerprints become semantic keys | Idempotent imports, stable identities, no duplicates |
| Policy gates are SQL queries | No configuration language to learn — same surface agents already know |
| Rich data preserved in payload | Fix suggestions, code flows, help URIs — all queryable |
| Provenance always visible | Compare scanners, track trends, audit what ran |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Parse SARIF manually to extract findings | An agent should import with one call |
| Build scanner-specific query APIs | An agent should query all findings through annotations |
| Require path mapping configuration | An agent should trust that locations resolve automatically |
| Accumulate stale findings forever | An agent should see only current results after re-import |
| Flatten SARIF to just message and severity | An agent should access the full richness of each result |
| Build policy in a custom DSL | An agent should write gates in SQL |

---

*An agent should be able to point any scanner's SARIF output at the graph and immediately query, correlate, and gate on every finding — through the same SQL surface it already knows.*
