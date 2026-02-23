---
description: How agents discover, filter, correlate, and gate on imported SARIF findings through SQL
tags: [sarif, annotations, query, policy, agent, sql]
audience: { human: 30, agent: 70 }
purpose: { flow: 70, reference: 30 }
---

# SARIF Query Patterns Flow

How an agent goes from "what did the scanners find?" to actionable decisions — using the same SQL surface it already knows.

## Why This Matters

| Without query patterns | With query patterns |
|------------------------|---------------------|
| Agent reads raw SARIF JSON | Agent queries structured annotations |
| Agent writes ad-hoc parsing logic | Agent uses `WHERE`, `GROUP BY`, `ORDER BY` |
| Each scanner needs different code | One query surface for all scanners |
| Policy is imperative code | Policy is a SQL predicate |

## Trigger

Agent has a question about code quality, security, or compliance — and scanner results have been imported.

## Flow 1: What Exists?

**Goal**: Discover what scanners have reported, at a glance.

### Stage 1: Overview

**Actor**: Agent
**Action**: Query annotation counts by source and severity

```sql
SELECT source, severity, count(*) AS findings
FROM annotations
WHERE kind = 'lint'
GROUP BY source, severity
ORDER BY source, severity_rank DESC
```

**Output**: Table showing which scanners reported, and how many findings at each severity.

```
source      | severity | findings
------------|----------|----------
codeql      | error    | 3
codeql      | warning  | 12
snyk-code   | error    | 5
snyk-code   | warning  | 18
qodana-jvm  | warning  | 42
qodana-jvm  | info     | 87
```

### Stage 2: Drill Down by Source

**Actor**: Agent
**Action**: See all findings from one scanner

```sql
SELECT severity, rule_id, message, resolved_target_uri
FROM annotations
WHERE kind = 'lint' AND source = 'snyk-code'
ORDER BY severity_rank DESC, rule_id
```

### Stage 3: Drill Down by File

**Actor**: Agent
**Action**: See all findings for a specific file, from all scanners

```sql
SELECT source, severity, rule_id, message
FROM annotations_for('file:///src/Auth/AuthService.cs', 'lint', 'info')
ORDER BY severity_rank DESC
```

## Flow 2: What's Critical?

**Goal**: Find the most severe findings that need attention.

### Stage 1: Severity Filter

**Actor**: Agent
**Action**: Query error-level findings across all scanners

```sql
SELECT source, rule_id, message, resolved_target_uri
FROM annotations
WHERE kind = 'lint' AND severity = 'error'
ORDER BY source, resolved_target_uri
```

### Stage 2: Navigate to Code

**Actor**: Agent
**Action**: Read the code at the finding's location

```
read("file:///src/Auth/AuthService.cs#line=42,60", 2000)
```

The `resolved_target_uri` from the annotation query gives the exact URI with line fragment — directly usable with the `read` tool.

## Flow 3: Cross-Scanner Correlation

**Goal**: See where multiple scanners flag the same code.

### Stage 1: Files Flagged by Multiple Scanners

**Actor**: Agent
**Action**: Find files with findings from more than one source

```sql
SELECT scope_document_uri, count(DISTINCT source) AS scanner_count, count(*) AS total_findings
FROM annotations
WHERE kind = 'lint'
GROUP BY scope_document_uri
HAVING count(DISTINCT source) > 1
ORDER BY scanner_count DESC, total_findings DESC
```

### Stage 2: Compare Scanner Opinions

**Actor**: Agent
**Action**: For a specific file, see what each scanner says

```sql
SELECT source, severity, rule_id, message
FROM annotations_for('file:///src/Auth/TokenService.cs', 'lint', 'hint')
ORDER BY source, severity_rank DESC
```

When multiple scanners flag the same location, the agent can compare their assessments — one might call it `error`, another `warning`. The `data` payload carries tool-specific severity for deeper comparison.

## Flow 4: Rule-Based Analysis

**Goal**: Understand the distribution of specific rules.

### Stage 1: Most Common Rules

**Actor**: Agent
**Action**: Find which rules produce the most findings

```sql
SELECT source, rule_id, count(*) AS occurrences, severity
FROM annotations
WHERE kind = 'lint'
GROUP BY source, rule_id, severity
ORDER BY occurrences DESC
LIMIT 20
```

### Stage 2: Rule Details from Data Payload

**Actor**: Agent
**Action**: Access rule metadata (description, help, CWE) from the data payload

```sql
SELECT rule_id, message, data->>'$.rule.helpUri' AS help_url,
       data->>'$.rule.shortDescription' AS description,
       data->'$.rule.tags' AS tags
FROM annotations
WHERE kind = 'lint' AND rule_id = 'javascript/XSS'
```

## Flow 5: Policy Gate

**Goal**: Enforce a quality/security threshold as a pass/fail check.

### Stage 1: Simple Severity Gate

**Actor**: Agent (or CI pipeline)
**Action**: Check if any error-severity findings exist

```sql
SELECT count(*) = 0 AS gate_passes
FROM annotations
WHERE kind = 'lint' AND severity = 'error'
```

### Stage 2: Scoped Gate (Recent Changes Only)

**Actor**: Agent
**Action**: Check for critical findings only in recently changed files

```sql
SELECT count(*) = 0 AS gate_passes
FROM annotations a
JOIN git_diff('HEAD~5') g ON lower(a.scope_document_uri) = lower(g.uri)
WHERE a.kind = 'lint' AND a.severity = 'error'
```

### Stage 3: Multi-Source Gate

**Actor**: Agent
**Action**: Require that no scanner reports critical issues, with per-scanner thresholds

```sql
SELECT
  source,
  sum(CASE WHEN severity = 'error' THEN 1 ELSE 0 END) AS errors,
  sum(CASE WHEN severity = 'warning' THEN 1 ELSE 0 END) AS warnings
FROM annotations
WHERE kind = 'lint'
GROUP BY source
HAVING errors > 0
```

If this returns rows, the gate fails — showing which scanners have unresolved errors.

### Stage 4: Rule-Specific Gate

**Actor**: Agent
**Action**: Block on specific rules (e.g., no SQL injection findings)

```sql
SELECT count(*) = 0 AS gate_passes
FROM annotations
WHERE kind = 'lint'
  AND rule_id IN ('javascript/Sqli', 'python/sql-injection', 'java:S3649')
```

## Flow 6: Trend Tracking

**Goal**: Understand how findings change over time (requires re-import history).

### Stage 1: Current Snapshot

**Actor**: Agent
**Action**: Count findings by source as a baseline

```sql
SELECT source, count(*) AS total,
  sum(CASE WHEN severity = 'error' THEN 1 ELSE 0 END) AS errors
FROM annotations
WHERE kind = 'lint'
GROUP BY source
```

Trend tracking across re-imports depends on annotations being replaced, not accumulated. Each import reflects a point-in-time snapshot. Historical tracking would require external logging of these snapshots — RepoQL's annotation table reflects the latest state, not history.

## Flow 7: Combining Findings with Code Structure

**Goal**: Use the graph to add context to findings.

### Stage 1: Findings on Public API Surface

**Actor**: Agent
**Action**: Find security findings in public-facing code

```sql
SELECT a.source, a.rule_id, a.message, a.resolved_target_uri,
       f.headline
FROM annotations a
JOIN Files f ON f.uri = a.scope_document_uri
WHERE a.kind = 'lint' AND a.severity = 'error'
  AND f.uri LIKE '%Controller%'
ORDER BY a.severity_rank DESC
```

### Stage 2: Findings by Code Area

**Actor**: Agent
**Action**: Group findings by directory to find hotspots

```sql
SELECT
  regexp_extract(scope_document_uri, 'file:///([^/]+/[^/]+)/', 1) AS area,
  count(*) AS findings,
  count(DISTINCT source) AS scanners
FROM annotations
WHERE kind = 'lint'
GROUP BY area
ORDER BY findings DESC
```

## Termination

These flows don't have a single termination — they're patterns an agent composes as needed. The agent's question is answered when they have enough information to act.

## Key Insight: No New Tools

None of these patterns require new tools, new APIs, or new query syntax. They use:
- `annotations` view (already exists)
- `annotations_for()` macro (already exists)
- `annotations_all()` macro (already exists)
- Standard SQL aggregation, filtering, joining
- `read` tool for navigating to code

The SARIF import flow's job is to get findings into the annotation table correctly. After that, the existing query surface handles everything.

## Related

- `docs/flows/future/sarif/sarif-import.md` — how findings get into the graph
- `docs/flows/future/sarif/sarif-reimport.md` — how findings stay current
- `docs/Schema.md` — `annotations` view, `annotations_for()`, `annotations_all()` macros
