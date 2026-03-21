---
description: "snippet_glob(pattern, max_results) → uri, snippet"
tags: ["snippet_glob", "content", "glob", "fragments"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# snippet_glob

Return file content for URIs matched by a glob pattern.

## Capsule: SnippetGlob

**Invariant**
`snippet_glob(pattern, max_results)` reads live file content and supports `#line=` and `#symbol=` fragments.

**Example**
```sql
SELECT uri, snippet
FROM snippet_glob('file:///src/demo.cs#symbol=Demo.Foo');
```
//BOUNDARY: Reads live file content via URI registry. Use `#line=` or `#symbol=` fragments for targeted extraction instead of fetching whole files.

**Depth**
- Supports exclusions such as `;!#symbol=Demo.Bar` in the same pattern expression
- Use `snippet()` instead when you want structured line output with focus markers
