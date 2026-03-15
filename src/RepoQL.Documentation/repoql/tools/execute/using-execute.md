---
description: "execute(intent, code, tokenBudget, timeout) → sandboxed JavaScript with repoql.query(sql) access to indexed repository data."
tags: ["execute", "sandbox", "javascript", "wasm"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# execute Tool

Sandboxed JavaScript for computations that are awkward in SQL. Use `query` when SQL already expresses the answer cleanly.

## Quick Reference

```text
execute(
  intent,        // Why you are running it; guides overflow summarization
  code,          // JavaScript source
  tokenBudget,   // Response budget, default 15000
  timeout        // Milliseconds, default 5000
)
```

---

## Capsule: ExecuteVsQuery

**Invariant**
Use `query` for set-based retrieval and joins. Use `execute` when you need JavaScript control flow, reshaping, or post-processing on top of repository data.

**Example**
```text
query   -> SELECT uri, lines FROM Files WHERE lines > 500
execute -> repoql.query(...) then group, reshape, diff, or compute custom metrics
```
//BOUNDARY: If pure SQL is clear and short, prefer `query`. `execute` adds a sandbox, timeout, and an extra translation layer.

**Depth**
- `query`: best for filtering, joins, aggregation, graph traversal
- `execute`: best for branching logic, custom transforms, object/array manipulation
- Bridge: `repoql.query(sql)` pulls SQL results into JavaScript as arrays/objects

---

## Capsule: RuntimeSurface

**Invariant**
The sandbox exposes ES2020+ JavaScript, console diagnostics, and `repoql.query(sql)`. It does not expose filesystem or network access.

**Example**
```js
const rows = repoql.query("SELECT name, extends FROM Types WHERE extends IS NOT NULL");
rows.filter(r => r.extends === "IDisposable").map(r => r.name)
```
//BOUNDARY: `repoql.query(sql)` can read indexed repository data only. It is not a general host escape hatch.

**Depth**
- Engine: QuickJS-NG running in a WASM sandbox
- Optional `input` global may exist when the caller provides it
- `console.log()`, `console.warn()`, `console.error()` emit diagnostics
- Return values are mapped like query results:
  - object -> key/value rows
  - array of objects -> table
  - array of scalars -> single `value` column

---

## Examples

### Simple computation

```js
const data = [3, 1, 4, 1, 5, 9, 2, 6];
data.filter(x => x > 3).sort((a, b) => b - a);
```

### Query the graph from JavaScript

```js
const types = repoql.query("SELECT name, extends FROM Types WHERE extends IS NOT NULL");
types.filter(t => t.extends === "IDisposable").map(t => t.name);
```

### Cross-reference multiple queries

```js
const files = repoql.query("SELECT uri, lang, lines FROM Files WHERE lang = 'code.csharp'");
const big = files.filter(f => f.lines > 500);
({
  totalCSharpFiles: files.length,
  bigFiles: big.length,
  avgLines: Math.round(files.reduce((sum, f) => sum + f.lines, 0) / files.length)
});
```

### Object analysis

```js
const stats = { files: 42, errors: 3 };
({ errorRate: (stats.errors / stats.files * 100).toFixed(1) + "%", healthy: stats.errors < 5 });
```

### Array processing

```js
const rows = repoql.query("SELECT lang, lines FROM Files WHERE lines IS NOT NULL");
rows
  .filter(r => r.lang === "code.csharp")
  .map(r => r.lines)
  .sort((a, b) => b - a)
  .slice(0, 10);
```

---

## Security Model

- Isolation: QuickJS-NG runs inside WASM with host-controlled callbacks only.
- Filesystem: unavailable.
- Network: unavailable.
- Memory: bounded by sandbox limits.
- Time: execution is interrupted on timeout via epoch-based cancellation.
- Capabilities: if the host does not provide `repoql.query`, query access is unavailable.

---

## Error Handling

| Error | Meaning | Typical fix |
|------|---------|-------------|
| `syntax:` | JavaScript parse error | Fix code syntax |
| `runtime:` | Exception during execution | Guard nulls, shape assumptions, or bad SQL handling |
| `timeout:` | Script exceeded timeout | Reduce work or increase `timeout` |
| `memory:` | Script exceeded sandbox memory | Process less data at once |
| no-capabilities | Caller/runtime did not provide a capability such as `repoql.query` | Fall back to pure JS or use `query` directly |

Budget overflow follows the same contract as `query`: when output exceeds `tokenBudget`, the tool uses `intent` to guide LLM summarization. Repeating the exact request bypasses the overflow check and returns the full result.
