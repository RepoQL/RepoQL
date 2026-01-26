---
description: "tree(uris_json, headlines_json, foldersOnly) → ASCII directory tree. Format URI lists as visual hierarchy with optional headlines and folder-only mode showing file counts by extension."
tags: ["tree", "directory", "structure", "visualization", "folders", "hierarchy"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Functions[95%]"]
---

# Tree Function

Format URI lists as ASCII directory trees for quick codebase orientation.

## Quick Reference

```sql
-- Full tree with headlines
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE uri LIKE 'file:///src/%';

-- Folders only with file counts
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    true
)
FROM Files
WHERE uri LIKE 'file:///src/%';

-- From search results
SELECT tree(
    json_group_array(s.uri ORDER BY s.uri),
    json_group_array(f.headline ORDER BY s.uri),
    false
)
FROM search('auth', k := 20) s
JOIN Files f ON lower(f.uri) = lower(s.uri);
```

---

## Capsule: TreeBasic

**Invariant**
`tree(uris_json, headlines_json, foldersOnly)` formats URI + headline arrays as an ASCII directory tree.

**Example**
```sql
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE uri LIKE 'file:///src/RepoQL.ConsoleApp/%';
```
Output:
```
file:///
└── src/
    └── RepoQL.ConsoleApp/
        ├── Commands/
        │   ├── InstallCommand.cs
        │   └── ServeCommands.cs
        ├── Tools/
        │   ├── QueryTool.cs
        │   └── XrayTool.cs
        └── Program.cs
```
//BOUNDARY: Inputs must be JSON arrays aligned by index. Empty array returns empty string.

**Depth**
- Groups URIs by scheme (file:///, repoql-docs:///, etc.)
- Sorts alphabetically, directories before files
- Uses box-drawing characters for tree structure
- Headlines are appended when any non-empty headline is provided (pass `[]` to suppress)

---

## Capsule: FoldersOnly

**Invariant**
`tree(uris_json, headlines_json, foldersOnly := true)` shows directories with aggregated file counts by extension.

**Example**
```sql
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    true
)
FROM Files
WHERE uri LIKE 'file:///src/%';
```
Output:
```
file:///
└── src/
    ├── RepoQL.ConsoleApp/ (3 files)
    │   ├── Commands/ (5 cs)
    │   ├── Tools/ (5 cs)
    │   └── Host/ (14 cs)
    ├── RepoQL.Contracts/ (29 cs, 1 csproj)
    └── RepoQL.Core/ (12 cs, 1 csproj)
```
//BOUNDARY: File counts grouped by extension. Mixed extensions shown as comma-separated list.

**Depth**
- Compact view for large codebases
- Shows extension distribution per folder
- Use when full tree would be too large

---

## Capsule: WithGlobFiles

**Invariant**
Combine `tree()` with `glob_files()` for pattern-based tree views.

**Example**
```sql
-- All C# files
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE matches_glob(uri, 'file:///src/**/*.cs');

-- Exclude tests
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE matches_glob(uri, 'file:///src/**;!**/tests/**');

-- Multiple directories
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE matches_glob(uri, 'file:///src/**;file:///docs/**');
```
//BOUNDARY: glob_files returns URIs; tree formats them. Compose via subquery.

---

## Capsule: WithSearch

**Invariant**
Visualize search results as a tree to understand their distribution.

**Example**
```sql
-- Where are auth-related files?
SELECT tree(
    json_group_array(s.uri ORDER BY s.uri),
    json_group_array(f.headline ORDER BY s.uri),
    false
)
FROM search('authentication', k := 30) s
JOIN Files f ON lower(f.uri) = lower(s.uri);

-- Error handling locations
SELECT tree(
    json_group_array(s.uri ORDER BY s.uri),
    json_group_array(f.headline ORDER BY s.uri),
    false
)
FROM search('error handling', k := 50) s
JOIN Files f ON lower(f.uri) = lower(s.uri)
WHERE s.scope = 'document';
```
//BOUNDARY: Helps understand search result clustering by directory.

---

## Capsule: WithViews

**Invariant**
Use with Files, Functions, Types views for filtered trees.

**Example**
```sql
-- Files with errors
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE error_count > 0;

-- Async function locations
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE uri IN (SELECT file_uri FROM Functions WHERE is_async);

-- Interface definitions
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE uri IN (SELECT file_uri FROM Types WHERE type_kind = 'interface');
```
//BOUNDARY: Views provide rich filtering; tree provides spatial context.

---

## Capsule: MultiScheme

**Invariant**
Trees with multiple URI schemes show each scheme as a top-level branch.

**Example**
```sql
SELECT tree(
    json_group_array(uri ORDER BY uri),
    json_group_array(headline ORDER BY uri),
    false
)
FROM Files
WHERE uri LIKE 'file://%' OR uri LIKE 'repoql-docs://%';
```
Output:
```
repoql-docs:///
├── quickstart.md
└── repoql/
    └── tools/
        └── query/

file:///
└── src/
    └── RepoQL.ConsoleApp/
```
//BOUNDARY: Schemes sorted alphabetically. Each scheme is a separate tree root.

---

## Common Patterns

| Goal | Query |
|------|-------|
| Full repo tree | `SELECT tree(json_group_array(uri ORDER BY uri), json_group_array(headline ORDER BY uri), false) FROM Files` |
| Source folders only | `SELECT tree(json_group_array(uri ORDER BY uri), json_group_array(headline ORDER BY uri), true) FROM Files WHERE matches_glob(uri, 'file:///src/**')` |
| Specific extension | `SELECT tree(json_group_array(uri ORDER BY uri), json_group_array(headline ORDER BY uri), false) FROM Files WHERE matches_glob(uri, 'file:///docs/**/*.md')` |
| Files with errors | `SELECT tree(json_group_array(uri ORDER BY uri), json_group_array(headline ORDER BY uri), false) FROM Files WHERE error_count > 0` |
| Search result map | `SELECT tree(json_group_array(s.uri ORDER BY s.uri), json_group_array(f.headline ORDER BY s.uri), false) FROM search('config', k := 20) s JOIN Files f ON lower(f.uri)=lower(s.uri)` |
| Exclude tests | `SELECT tree(json_group_array(uri ORDER BY uri), json_group_array(headline ORDER BY uri), false) FROM Files WHERE matches_glob(uri, 'file:///src/**;!**/test*')` |

---

## When to Use

| Scenario | Recommendation |
|----------|----------------|
| Codebase orientation | `tree` with `foldersOnly := true` |
| Finding file locations | `tree` with `glob_files` filter |
| Understanding search results | `tree` with search subquery |
| Documenting structure | `tree` full output |
| Large repos (1000+ files) | Always use `foldersOnly := true` |

---

## Comparison with read => tree

| Feature | `tree()` SQL function | `read("pattern => tree")` |
|---------|----------------------|---------------------------|
| Input | JSON arrays of URIs + headlines | Glob pattern string |
| Composability | Works with any query | Pattern only |
| Budget control | No | Yes (progressive disclosure) |
| Use case | Flexible SQL composition | Quick directory view |

Use `tree()` when you need to filter URIs with SQL before visualization.
Use `read("=> tree")` for quick glob-based directory views with token budgeting.

