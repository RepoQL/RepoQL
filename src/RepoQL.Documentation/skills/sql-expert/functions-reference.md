---
description: "Quick reference for RepoQL SQL functions: search, snippet, graph traversal, git, and diagnostics."
tags: ["skill", "sql-expert", "functions", "search", "snippet", "udf"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
---

# Functions Quick Reference

Key functions for common query patterns. For complete signatures, see `help:///repoql/tools/query/sql-reference.md`.

---

## Search

### `search(keywords, k)`
Hybrid search (BM25 + fuzzy + semantic). Returns URIs ranked by relevance.

```sql
SELECT uri, score FROM search('authentication middleware', k := 10);
```

### `search_symbol(keywords, scope, kind_filter, k)`
Find named symbols (types, methods, fields).

```sql
SELECT uri, name, kind FROM search_symbol('Validate', k := 10);
```

### `related(seed_uri, k)`
Find files/symbols similar to a seed.

```sql
SELECT uri, score FROM related('file:///src/Auth.cs', k := 5);
```

---

## Content

### `snippet(uri, context_lines)`
Extract lines around a fragment. Works with `#line=`, `#symbol=`, `#char=` fragments.

```sql
-- Search + show context
SELECT s.uri, sn.text
FROM search('config', k := 5) s,
LATERAL snippet(s.uri, 2) sn
WHERE sn.is_focus;
```

---

## Annotations

### `annotations_for(uri, kinds, min_severity)`
Get diagnostics for a specific file.

```sql
SELECT * FROM annotations_for('file:///src/Foo.cs');
SELECT * FROM annotations_for('file:///src/Foo.cs', kinds := 'lint', min_severity := 'warning');
```

---

## Git

### `git_log(limit)`
Recent commit history.

```sql
SELECT * FROM git_log(20);
```

Full git function reference: `help:///repoql/tools/query/functions/git.md`

---

## LLM (requires OPENROUTER_API_KEY)

### `ask(json_data, question, max_tokens)`
Ask an LLM to analyze query results.

```sql
SELECT ask(
  (SELECT json_group_array(json_object('uri', uri, 'headline', headline)) FROM Files WHERE lang = 'code.csharp'),
  'What are the main components?',
  500
);
```

---

## Utility

### `glob_match(path, pattern)`
Path pattern matching.

```sql
SELECT uri FROM Files WHERE glob_match(path, 'src/Services/**/*.cs');
```

---

## Composition with LATERAL

LATERAL is the key to powerful queries — it applies a function per row:

```sql
-- For each search result, get its snippet
SELECT s.uri, sn.text
FROM search('error handling', k := 5) s,
LATERAL snippet(s.uri, 3) sn;

-- For each file, get its annotations
SELECT f.uri, a.*
FROM Files f,
LATERAL annotations_for(f.uri) a
WHERE f.error_count > 0;
```

---

*Functions are the verbs. LATERAL is the glue.*
