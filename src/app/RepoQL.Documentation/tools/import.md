---
description: "Import SARIF 2.1.0 static analysis results as queryable lint annotations"
tags: ["sarif", "import", "annotations", "lint", "static-analysis"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Import[95%]", "Annotations[80%]"]
---

# SARIF Import

Import findings from any SARIF 2.1.0 producer as lint annotations. One command, any scanner.

## Quick Reference

```sql
-- Import
import("sarif:///path/to/results.sarif")
import("sarif:///./relative/to/repo.sarif")

-- What landed?
SELECT source, severity, count(*) FROM Annotations
WHERE kind = 'lint' GROUP BY source, severity ORDER BY source, severity_rank DESC;

-- Errors only
SELECT resolved_target_uri, source, rule_id, message
FROM Annotations WHERE kind = 'lint' AND severity = 'error';

-- Findings for one file
SELECT source, severity, rule_id, message
FROM annotations_for('file:///src/Auth/Service.cs', 'lint', 'info');
```

---

## Capsule: ImportBehavior

**Invariant**
`sarif://` URIs import synchronously. The response includes a summary with per-source counts. All findings land as `kind = 'lint'` annotations.

**Example**
```
import("sarif:///build/snyk-results.sarif")
→ "Imported 42 findings from snyk-code
   snyk-code: 42 findings
     38 resolved to indexed files, 4 unresolved
     42 new, 0 updated, 0 unchanged, 0 expired"
```

//BOUNDARY: SARIF imports are synchronous — no operation ID is returned. VFS imports (`github://`, `local://`) are async and return an operation ID.

**Depth**
- URI forms: `sarif:///absolute/path.sarif`, `sarif:///./relative/path.sarif`
- The file must be SARIF 2.1.0 (`version: "2.1.0"`)
- Each run must have `tool.driver.name` — runs without it are skipped
- Results missing `ruleId`, `message`, or `location` are skipped and counted in the summary
- `ruleId` is optional per the SARIF spec but required by RepoQL for semantic key stability

---

## Capsule: ReimportLifecycle

**Invariant**
Re-importing the same file replaces findings source-wide. Stale findings expire, unchanged findings are preserved (no duplication), new findings are added.

**Example**
```
-- First import: 5 findings from eslint
import("sarif:///build/eslint.sarif")
→ 5 new, 0 expired

-- Second import with one finding fixed:
import("sarif:///build/eslint.sarif")
→ 0 new, 4 unchanged, 1 expired
```

//BOUNDARY: Expiration is source-wide, not file-scoped. All annotations from a source are replaced in one transaction.

**Depth**
- Semantic keys ensure stable identity across imports: `{source}:{ruleId}:{path}:{startLine}:{fingerprint}`
- Fingerprint priority: `partialFingerprints` > `fingerprints` > SHA-256 content hash
- `created_at` is preserved on updates — only content changes trigger an update
- An import with zero findings expires all existing findings for that source (legitimate clean scan)

---

## Capsule: PathResolution

**Invariant**
SARIF paths resolve to indexed documents in two stages: exact match, then suffix match. Suffix matching handles scanners that emit paths relative to a subdirectory rather than the repo root.

**Example**
```
# Scanner scans src/ but emits paths without the src/ prefix:
#   "Formats/MyFile.cs" → no exact match for file:///Formats/MyFile.cs
#   Suffix match finds file:///src/Formats/MyFile.cs → resolved

# If multiple documents end with the same suffix → stays unresolved (ambiguous)
```

//BOUNDARY: Suffix match only applies when exactly one indexed document matches. Zero or multiple matches → unresolved. This prevents silent misresolution.

---

## Capsule: QueryPatterns

**Invariant**
Imported findings use the standard `Annotations` view and `annotations_for()` macro. No new query syntax needed.

**Example**
```sql
-- Severity summary by source
SELECT source, severity, count(*) AS findings
FROM Annotations WHERE kind = 'lint'
GROUP BY source, severity ORDER BY source, severity_rank DESC;

-- Error-level findings across all scanners
SELECT source, rule_id, message, resolved_target_uri
FROM Annotations WHERE kind = 'lint' AND severity = 'error'
ORDER BY source, resolved_target_uri;

-- Findings for a file (all sources, info and above)
SELECT source, severity, rule_id, message
FROM annotations_for('file:///src/Auth/Service.cs', 'lint', 'info')
ORDER BY severity_rank DESC;

-- Policy gate: fail if any errors exist
SELECT count(*) = 0 AS gate_passes
FROM Annotations WHERE kind = 'lint' AND severity = 'error';

-- Rule metadata from data payload
SELECT rule_id, data->>'$.rule.helpUri' AS help_url,
       data->>'$.rule.description' AS description
FROM Annotations WHERE kind = 'lint' AND rule_id = 'javascript/XSS';

-- Cross-scanner hotspots
SELECT split_part(resolved_target_uri, '#', 1) AS file_uri,
       count(DISTINCT source) AS scanners, count(*) AS total
FROM Annotations WHERE kind = 'lint'
GROUP BY file_uri HAVING count(DISTINCT source) > 1
ORDER BY scanners DESC;
```

//BOUNDARY: All findings have `kind = 'lint'`. Always include `WHERE kind = 'lint'` to isolate SARIF findings from other annotation types.

---

## Capsule: SupportedProducers

**Invariant**
Known producers map to stable source slugs. Unknown producers are auto-slugified (lowercase, non-alphanumeric collapsed to hyphens).

| Producer (`tool.driver.name`) | Source slug |
|-------------------------------|------------|
| SnykCode | `snyk-code` |
| Snyk Open Source | `snyk-oss` |
| QDJVM | `qodana-jvm` |
| QDJS | `qodana-js` |
| QDNET | `qodana-dotnet` |
| QDPY | `qodana-python` |
| QDGO | `qodana-go` |
| QDPHP | `qodana-php` |
| CodeQL command-line toolchain | `codeql` |
| Semgrep | `semgrep` |
| Semgrep OSS | `semgrep` |
| ESLint | `eslint` |
| DevSkim | `devskim` |
| Microsoft (R) Visual C# Compiler | `roslyn` |
| Trivy Vulnerability Scanner | `trivy` |
| SonarQube | `sonarqube` |

//BOUNDARY: Unknown producers are supported — they get auto-slugified names. Adding a known mapping is a code change, not a configuration change.

**Depth**
- Source slugs are used in semantic keys and `source` column — filter with `WHERE source = 'snyk-code'`
- Producer name matching is case-insensitive
- Example auto-slug: `"My Custom Linter v3.2"` → `my-custom-linter-v3-2`

---

## Capsule: SeverityMapping

**Invariant**
SARIF `level` maps to RepoQL `severity`. The cascade is: `result.level` > `rule.defaultConfiguration.level` > `"warning"`.

| SARIF level | RepoQL severity |
|-------------|----------------|
| `error` | `error` |
| `warning` | `warning` |
| `note` | `info` |
| `none` | `hint` |

//BOUNDARY: Tool-specific severity (ideaSeverity, CVSS, SonarQube severity) is preserved in the `data` payload under `toolSeverity`, not in the `severity` column.

---

## Error Scenarios

| Condition | Behavior |
|-----------|----------|
| File not found | Error: `"SARIF file not found at {path}"` |
| Invalid JSON | Error: `"Invalid JSON in SARIF file at {path}: {detail}"` |
| Wrong SARIF version | Error: `"SARIF version must be '2.1.0' but was '{version}'"` |
| No `runs` array | Error: `"SARIF envelope must contain a non-empty runs array"` |
| All runs missing `tool.driver.name` | Error: `"SARIF envelope did not contain any runs with tool.driver.name"` |
| Some runs missing `tool.driver.name` | Warning in summary; valid runs still imported |
| Zero findings across valid runs | Warning; existing findings for that source are expired (clean scan) |
| Unresolved file paths | Annotation created with `target_uri` pointing to the path; counted in summary |
