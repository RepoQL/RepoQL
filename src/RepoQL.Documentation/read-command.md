---
description: "read: prefix - fetch file content or LLM-summarize with guidance"
tags: [read, fetch, file, content, summarize, llm, uri, glob]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# read: Prefix

Fetch raw file content or get LLM-guided summary.

## Capsule: ReadPrefix

**Invariant**
`read:<uri>` returns content; `read:<uri> // <question>` summarizes via LLM.

**Example**
```
query("read:file:///src/App.cs")
query("read:docs:///quickstart.md#line=1,50")
query("read:file:///src/**/*.cs")
query("read:file:///src/Auth.cs // what oauth flows are supported?")
query("read:docs:///quickstart.md // list all SQL macros mentioned")
```

**Depth**

- All URI schemes: `file:///`, `docs:///`, `github://`
- Fragments: `#line=10,20`, `#symbol=Name`, `#char=0,500`
- Globs expand and concatenate matches
- `// question` triggers LLM analysis with citations in `#line=X,Y` format
- LLM surfaces nuances and tangential context; flags unanswerable queries
- Requires `OPENROUTER_API_KEY` for LLM features
- SeeAlso: `snippet()`, `search()`, `llm_summarize()`
