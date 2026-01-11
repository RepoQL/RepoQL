---
description: "tree(uris_json, foldersOnly) → ASCII directory tree. Format URI lists as visual hierarchy with optional folder-only mode showing file counts by extension."
tags: ["tree", "directory", "structure", "visualization", "folders", "hierarchy"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Functions[95%]"]
---

# Tree Function

Format URI lists as ASCII directory trees for quick codebase orientation.

## Quick Reference

```sql
-- Full tree from glob
SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**')));

-- Folders only with file counts
SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**')), foldersOnly := true);

-- From search results
SELECT tree((SELECT json_group_array(uri) FROM search('auth', k := 20)));
```

---

## Capsule: TreeBasic

**Invariant**
`tree(uris_json)` formats a JSON array of URIs as an ASCII directory tree.

**Example**
```sql
SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/RepoQL.ConsoleApp/**')));
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
//BOUNDARY: Input must be JSON array of URI strings. Empty array returns empty string.

**Depth**
- Groups URIs by scheme (file:///, docs:///, etc.)
- Sorts alphabetically, directories before files
- Uses box-drawing characters for tree structure

---

## Capsule: FoldersOnly

**Invariant**
`tree(uris_json, foldersOnly := true)` shows directories with aggregated file counts by extension.

**Example**
```sql
SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**')), foldersOnly := true);
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
SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**/*.cs')));

-- Exclude tests
SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**;!**/tests/**')));

-- Multiple directories
SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**;docs/**')));
```
//BOUNDARY: glob_files returns URIs; tree formats them. Compose via subquery.

---

## Capsule: WithSearch

**Invariant**
Visualize search results as a tree to understand their distribution.

**Example**
```sql
-- Where are auth-related files?
SELECT tree((SELECT json_group_array(uri) FROM search('authentication', k := 30)));

-- Error handling locations
SELECT tree((
    SELECT json_group_array(uri) 
    FROM search('error handling', k := 50) 
    WHERE scope = 'document'
));
```
//BOUNDARY: Helps understand search result clustering by directory.

---

## Capsule: WithViews

**Invariant**
Use with Files, Functions, Types views for filtered trees.

**Example**
```sql
-- Files with errors
SELECT tree((SELECT json_group_array(uri) FROM Files WHERE error_count > 0));

-- Async function locations
SELECT tree((SELECT json_group_array(file_uri) FROM Functions WHERE is_async));

-- Interface definitions
SELECT tree((SELECT json_group_array(file_uri) FROM Types WHERE type_kind = 'interface'));
```
//BOUNDARY: Views provide rich filtering; tree provides spatial context.

---

## Capsule: MultiScheme

**Invariant**
Trees with multiple URI schemes show each scheme as a top-level branch.

**Example**
```sql
SELECT tree((SELECT json_group_array(uri) FROM Files WHERE uri LIKE 'file://%' OR uri LIKE 'docs://%'));
```
Output:
```
docs:///
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
| Full repo tree | `SELECT tree((SELECT json_group_array(uri) FROM glob_files('**')))` |
| Source folders only | `SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**')), foldersOnly := true)` |
| Specific extension | `SELECT tree((SELECT json_group_array(uri) FROM glob_files('**/*.md')))` |
| Files with errors | `SELECT tree((SELECT json_group_array(uri) FROM Files WHERE error_count > 0))` |
| Search result map | `SELECT tree((SELECT json_group_array(uri) FROM search('config', k := 20)))` |
| Exclude tests | `SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**;!**/test*')))` |

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
| Input | JSON array of URIs | Glob pattern string |
| Composability | Works with any query | Pattern only |
| Budget control | No | Yes (progressive disclosure) |
| Use case | Flexible SQL composition | Quick directory view |

Use `tree()` when you need to filter URIs with SQL before visualization.
Use `read("=> tree")` for quick glob-based directory views with token budgeting.
