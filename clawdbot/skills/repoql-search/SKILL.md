---
name: repoql-search
description: Search patterns for RepoQL - semantic search, scoping, and question reranking.
---

# RepoQL Search Patterns

RepoQL provides multiple search approaches. Choose based on what you're looking for.

## Search Methods

### 1. Explore (Recommended Start)
Hybrid lexical + semantic search with ranked results.
```
repoql_explore(
  keywords="authentication flow",
  tokenBudget=1500
)
```

### 2. SQL search() Function
Semantic + lexical hybrid search.
```sql
SELECT * FROM search('config validation', k := 10)
```

### 3. Symbol Search
Find by function/type name.
```sql
SELECT * FROM search_symbol('ValidateToken')
SELECT * FROM search_symbol('Auth%')  -- Wildcards
```

## Scoping Searches

### By Path (Explore)
Use `uriGlob` parameter with glob patterns:
```
uriGlob="file:///src/**/*.cs"          -- All C# in src
uriGlob="file:///src/services/**"      -- Services folder
uriGlob="file:///tests/**"             -- Tests only
```

### By Path (SQL)
Filter in WHERE clause:
```sql
SELECT * FROM search('error', k := 20)
WHERE uri LIKE 'file:///src/%'
```

### By Language
```sql
SELECT * FROM search('validation', k := 10)
WHERE lang = 'typescript'
```

## Questions and Reranking

Use `keywords` to retrieve candidates and `question` to rerank them toward the specific answer you need.
```
repoql_explore(
  keywords="cache invalidation",
  question="When does the read cache get evicted after writes?",
  tokenBudget=3000
)
```

For a broad survey, omit `question`. For a precise investigation, include it.

## Breadth

Use `breadth` when you want more or fewer results. `0` or omitted lets RepoQL choose.
```
repoql_explore(
  keywords="handler dispatcher",
  breadth=8,
  tokenBudget=3000
)
```

## Combined Example
```
repoql_explore(
  keywords="error handling",
  question="Where are user-facing errors normalized before returning responses?",
  uriGlob="file:///src/**",
  tokenBudget=2000
)
```

## Search Tips

**Questions work well as keywords:**
- "how does authentication work"
- "where are errors logged"
- "what validates user input"

**Be specific when possible:**
- "JWT refresh token validation" > "auth"
- "database connection retry" > "retry"

**Use multiple searches:**
1. Broad search to understand landscape
2. Narrow search once you know the area
3. Symbol search for specific names

## Read with Search

Combine search with read for targeted content:
```
# Find relevant files
repoql_explore(keywords="error handling", tokenBudget=1000)

# Read specific file with budget
repoql_read(uri="file:///src/ErrorHandler.cs", tokenBudget=3000)

# Or add question for synthesis
repoql_explain(
  question="What error types are handled?",
  uriGlob="file:///src/ErrorHandler.cs",
  tokenBudget=2000
)
```
