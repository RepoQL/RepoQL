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
- parse() auto-detects columns from header
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
Combine graph traversal with metadata joins for dependency analysis.

**Example**
```sql
WITH RECURSIVE deps AS (
  SELECT destination_node_id as id, 1 as depth FROM edge
  WHERE source_node_id = @start AND type = 'IMPORTS'
  UNION ALL
  SELECT e.destination_node_id, d.depth + 1 FROM edge e
  JOIN deps d ON e.source_node_id = d.id WHERE d.depth < 5
)
SELECT n.uri, f.lines, MIN(d.depth) as distance
FROM deps d
JOIN node n ON d.id = n.id
JOIN Files f ON n.uri = f.uri
GROUP BY n.uri, f.lines ORDER BY distance
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
| Ad-hoc lookup | parse('csv...') | InlineLookup |
| Multi-step | CTE chain | MultiStepAnalysis |
| Dependencies | WITH RECURSIVE | GraphComposition |
| Comparative | Window functions | AggregateInsights |
| External data | MCP tools | ExternalEnrich |

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
