---
description: "Composable data analysis workflows combining search, SQL, and external tools"
tags: ["ComposableQuery", "SearchEnrich", "LateralExpand", "InlineLookup", "MultiStepAnalysis", "DataAnalysis"]
audience: ["LLMs"]
categories: ["Guide[100%]", "Patterns[80%]"]
---

# Data Analysis with RepoQL

> **Core insight**: SQL is the composition layer. Search, indexed data, external tools, and inline references all return tables that combine with standard SQL.

## Capsule: ComposableQuery

**Invariant**
Every RepoQL operation returns a table; SQL joins and CTEs compose them.

**Example**
```sql
SELECT s.uri, f.lines, t.team
FROM search('auth', k:=10) s
JOIN Files f ON s.uri = f.uri
JOIN parse('project,team\nRepoQL.Core,Platform') t ON f.uri LIKE '%/' || t.project || '/%'
```

**Depth**
- search() returns table with uri, score
- Files is a view over indexed artifacts
- parse() creates inline lookup table
- Standard JOIN composes all three
- SeeAlso: SearchEnrich, InlineLookup

---

## Capsule: SearchEnrich

**Invariant**
Join search results with indexed metadata for enriched analysis.

**Example**
```sql
WITH matches AS (SELECT uri, score FROM search('error handling', k:=20))
SELECT m.uri, f.lang, f.lines, f.error_count, round(m.score,3) as relevance
FROM matches m
JOIN Files f ON m.uri = f.uri
ORDER BY m.score DESC
```

**Depth**
- Search finds relevant documents by content
- JOIN adds structured metadata (lang, lines, errors)
- Filter or aggregate the enriched results
- SeeAlso: ComposableQuery, LateralExpand

---

## Capsule: LateralExpand

**Invariant**
Use LATERAL to expand each row with correlated subquery results.

**Example**
```sql
SELECT s.uri, sn.line_number, sn.text
FROM search('TODO', k:=5) s, LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus
```

**Depth**
- LATERAL references columns from preceding tables
- snippet() returns context lines for each URI
- Think of LATERAL as a for-loop in SQL
- SeeAlso: SearchEnrich, Unnest

---

## Capsule: InlineLookup

**Invariant**
Use parse() to create ad-hoc reference tables without external files.

**Example**
```sql
WITH owners AS (
  SELECT * FROM parse('pattern,team,oncall
**/Auth/**,Security,alice
**/Payment/**,Payments,bob
**/Core/**,Platform,charlie')
)
SELECT f.uri, o.team, o.oncall
FROM Files f
JOIN owners o ON f.uri LIKE o.pattern
```

**Depth**
- parse() auto-detects structure (JSON/JSONL/CSV/TSV/YAML/embedded/plain text fallback)
- Joins work normally with inline data
- No file I/O for small lookup tables
- Alternative: VALUES clause for typed data
- SeeAlso: ComposableQuery, ParseInline

---

## Capsule: MultiStepAnalysis

**Invariant**
Chain CTEs or temp tables for complex multi-step analysis.

**Example**
```sql
WITH
  step1 AS (SELECT regexp_extract(uri, '/src/([^/]+)/', 1) as project, uri, lines FROM Files),
  step2 AS (SELECT project, COUNT(*) as files, SUM(lines) as loc FROM step1 GROUP BY project),
  step3 AS (SELECT *, ROUND(100.0 * loc / SUM(loc) OVER (), 1) as pct FROM step2)
SELECT * FROM step3 ORDER BY loc DESC
```

**Depth**
- Each CTE is a named intermediate result
- Later CTEs can reference earlier ones
- Alternative: CREATE TEMP TABLE for reuse
- Easier to debug than nested subqueries
- SeeAlso: RecursiveCTE, CumulativeCalc

---

## Capsule: GraphComposition

**Invariant**
Combine graph traversal with metadata joins for composition analysis.

**Example**
```sql
WITH RECURSIVE parts AS (
  SELECT destination_node_id as id, 1 as depth FROM edge
  WHERE source_node_id = (SELECT id FROM node WHERE uri = 'file:///src/Auth.cs')
  AND type = 'HAS_PART'
  UNION ALL
  SELECT e.destination_node_id, p.depth + 1 FROM edge e
  JOIN parts p ON e.source_node_id = p.id
  WHERE e.type = 'HAS_PART' AND p.depth < 5
)
SELECT n.kind, n.name, MIN(p.depth) as depth
FROM parts p
JOIN node n ON p.id = n.id
GROUP BY n.kind, n.name ORDER BY depth
```

**Depth**
- Recursive CTE traverses relationships
- JOIN with node gets URIs
- JOIN with Files adds metadata
- GROUP BY collapses multiple paths
- SeeAlso: RecursiveCTE, CycleDetection

---

## Capsule: AggregateInsights

**Invariant**
Combine grouping with window functions for comparative analysis.

**Example**
```sql
SELECT
  project, files, loc,
  ROUND(100.0 * loc / SUM(loc) OVER (), 1) as pct,
  RANK() OVER (ORDER BY loc DESC) as rank
FROM (
  SELECT regexp_extract(uri, '/src/([^/]+)/', 1) as project,
         COUNT(*) as files, SUM(lines) as loc
  FROM Files WHERE uri LIKE '%/src/%' GROUP BY 1
)
ORDER BY loc DESC
```

**Depth**
- Inner query: raw aggregation
- Outer query: comparative metrics
- Window functions add context without grouping
- SeeAlso: QualifyTopN, CumulativeCalc, Percentile

---

## Capsule: ExternalEnrich

**Invariant**
Call MCP tools from SQL to enrich with external data.

**Example**
```sql
SELECT * FROM mcp_tools() WHERE macro_name LIKE 'context7%';
SELECT json_extract_string(value, '$.id') as lib_id
FROM context7_resolve_library_id(libraryname:='react', query:='hooks');
```

**Depth**
- mcp_tools() lists available external tools
- Tools return JSON; extract fields with json_extract
- Combine with local data via JOIN
- SeeAlso: ComposableQuery

---

# Analysis Workflow

**1. Explore** - Understand what exists
```sql
SELECT lang, COUNT(*), SUM(lines) FROM Files GROUP BY lang ORDER BY 2 DESC;
```

**2. Find** - Locate relevant code
```sql
SELECT uri, score FROM search('your topic', k:=20);
```

**3. Enrich** - Add context and metadata
```sql
SELECT s.uri, f.lang, f.lines FROM search(...) s JOIN Files f ON s.uri = f.uri;
```

**4. Analyze** - Compute insights
```sql
WITH data AS (...) SELECT project, COUNT(*), percentile_cont(0.5)... GROUP BY project;
```

**5. Traverse** - Follow relationships
```sql
WITH RECURSIVE deps AS (...) SELECT ... FROM deps JOIN node JOIN Files;
```

---

# Quick Reference

| Need | Pattern | Capsule |
|------|---------|---------|
| Find + metadata | search() JOIN Files | SearchEnrich |
| Per-row expansion | LATERAL snippet() | LateralExpand |
| Ad-hoc lookup | parse('structured text...') | InlineLookup |
| Multi-step | CTE chain | MultiStepAnalysis |
| Dependencies | WITH RECURSIVE | GraphComposition |
| Comparative | Window functions | AggregateInsights |
| External data | MCP tools | ExternalEnrich |
| Steer summary | SQL comment | CommentAsPrompt |
| Topic neighborhood | search() → related() | ConceptExpansion |
| Dev process | git_commit + window funcs | GitAnalytics |
| Cross-repo metrics | CASE + PIVOT | CrossRepoComparison |

---

## Capsule: CommentAsPrompt

**Invariant**
SQL comments become the summarizer's prompt; use deliberately, never for debugging notes.

**Example**
```sql
-- What authentication patterns exist across services?
SELECT uri, headline, structure FROM search('authentication', k := 20)
```

**Depth**
- When query results exceed the token budget and the SQL contains comments, the summarizer uses comments as its guiding question
- A comment like `-- What patterns exist?` produces a focused synthesis; no comment returns raw data
- **Trap**: debugging comments (`-- Fix: column is document_uri`) pollute the synthesis with irrelevant context
- Strip scratch comments before the final query; keep only the question you want answered
- SeeAlso: SearchEnrich, MultiStepAnalysis

---

## Capsule: ConceptExpansion

**Invariant**
Chain `search()` → `related()` to walk through embedding space: find a seed by keyword, then expand to its semantic neighborhood.

**Example**
```sql
WITH seed AS (
  SELECT uri, score FROM search('authentication token', k := 1)
)
SELECT 'seed' as source, s.uri, s.score FROM seed s
UNION ALL
SELECT 'neighbor', r.uri, r.score
FROM seed s, LATERAL (SELECT * FROM related(s.uri, 5)) r
WHERE r.uri != s.uri
ORDER BY source, score DESC
```

**Depth**
- search() finds the best match for a concept
- related() expands to semantically similar files
- One hop covers the full topic neighborhood (docs, implementation, tests)
- Chain further: JOIN with Types or Functions to see what's inside each neighbor
- SeeAlso: SearchEnrich, LateralExpand

---

## Capsule: GitAnalytics

**Invariant**
`git_commit` + `git_file_change` + window functions enable development process analysis.

**Example**
```sql
-- Commit velocity with burst detection
WITH daily AS (
  SELECT DATE_TRUNC('day', c.author_date::TIMESTAMP) as day,
    COUNT(DISTINCT c.hash) as commits
  FROM git_commit c JOIN git_file_change fc ON c.hash = fc.commit_hash
  GROUP BY 1
)
SELECT day, commits,
  AVG(commits) OVER (ORDER BY day ROWS BETWEEN 6 PRECEDING AND CURRENT ROW) as rolling_7d,
  CASE WHEN commits > 3 * AVG(commits) OVER (ORDER BY day ROWS BETWEEN 13 PRECEDING AND 7 PRECEDING)
    THEN 'BURST' ELSE '' END as flag
FROM daily ORDER BY day DESC

-- Co-change coupling: files that always change together
WITH pairs AS (
  SELECT a.uri as file_a, b.uri as file_b, COUNT(*) as co_changes
  FROM git_file_change a
  JOIN git_file_change b ON a.commit_hash = b.commit_hash AND a.uri < b.uri
  GROUP BY 1, 2 HAVING COUNT(*) >= 3
)
SELECT file_a, file_b, co_changes FROM pairs ORDER BY co_changes DESC
```

**Depth**
- `git_commit`: hash, author_name, author_email, author_date, message, insertions, deletions
- `git_file_change`: commit_hash, uri, insertions, deletions
- `git_hotspots`: pre-computed churn analysis (commits, authors, churn per file)
- `git_recent`: recent commits with LLM summarization when results exceed budget
- Self-join `git_file_change` on `commit_hash` for co-change coupling
- Window functions (LAG, rolling AVG) for trend analysis
- SeeAlso: AggregateInsights, MultiStepAnalysis

---

## Capsule: CrossRepoComparison

**Invariant**
CASE expressions on URIs + GROUP BY + PIVOT compare metrics across imported repositories.

**Example**
```sql
-- Type system comparison via PIVOT
PIVOT (
  SELECT
    CASE WHEN file_uri LIKE 'github://owner/repo%' THEN 'repo-a'
         WHEN file_uri LIKE 'file://%' THEN 'local' END as repo,
    type_kind, COUNT(*) as cnt
  FROM Types GROUP BY 1, 2
) ON repo USING SUM(cnt) ORDER BY type_kind
```

**Depth**
- `CASE WHEN uri LIKE 'github://owner/repo%'` classifies by source repo
- PIVOT turns repo names into columns for side-by-side comparison
- Works with Files, Types, Functions, git_commit — any indexed data
- VALUES cross join creates inline rubrics for multi-dimensional scoring
- SeeAlso: AggregateInsights, InlineLookup

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
