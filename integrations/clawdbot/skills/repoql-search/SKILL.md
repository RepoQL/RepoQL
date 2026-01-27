---
name: repoql-search
description: Search patterns for RepoQL - semantic search, scoping, boost/penalize.
---

# RepoQL Search Patterns

RepoQL provides multiple search approaches. Choose based on what you're looking for.

## Search Methods

### 1. Explore with Locate (Recommended Start)
Intent-based search with ranked results.
```
repoql_explore(
  intent="Locate",
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
Use `scope` parameter with glob patterns:
```
scope="file:///src/**/*.cs"          -- All C# in src
scope="file:///src/services/**"      -- Services folder
scope="file:///tests/**"             -- Tests only
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

## Boost and Penalize

Adjust result ranking with regex patterns.

### Boost (Prioritize Matches)
```
boost="(?i)auth|token"     -- Case-insensitive, boost auth/token
boost="Service$"           -- Boost files ending in Service
```

### Penalize (Demote Matches)
```
penalize="(?i)test|mock"   -- Demote test files
penalize="\.spec\."        -- Demote spec files
```

### Combined Example
```
repoql_explore(
  intent="Locate",
  keywords="error handling",
  boost="(?i)exception|error",
  penalize="(?i)test|mock|spec",
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
repoql_explore(intent="Locate", keywords="error handling", tokenBudget=1000)

# Read specific file with budget
repoql_read(uri="file:///src/ErrorHandler.cs", tokenBudget=3000)

# Or add question for synthesis
repoql_read(uri="file:///src/ErrorHandler.cs // What error types are handled?", tokenBudget=2000)
```
