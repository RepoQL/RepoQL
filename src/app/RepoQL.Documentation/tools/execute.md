---
description: "execute(intent, code, tokenBudget, timeout) → sandboxed JavaScript with repoql.query(sql) access to indexed repository data and 20 built-in modules."
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

## Built-in Modules

The sandbox includes 20 built-in JavaScript libraries. Use dynamic `import()`:

```js
import("yaml").then(function(yaml) {
  var doc = yaml.default.load(repoql.read("file:///config.yml").content);
  return doc;
})
```

Both bare names (`"yaml"`) and prefixed (`"repoql:yaml"`) work.

> **Note:** The entry script runs in global mode. Use `import("name")` with `.then()`, not static `import`. CJS packages (yaml, semver, json5, ini, fuse, mustache, picomatch, toposort, front-matter, parse-diff) expose functions on `mod.default`. ESM packages (change-case, ohash, radash, diff, microdiff, ignore, base64, dayjs, toml, xml) have named exports directly.

### Format Parsers

| Module | Access pattern | What it parses |
|--------|---------------|----------------|
| yaml | `mod.default.load(str)` | YAML 1.2 — CI configs, k8s manifests, GitHub Actions |
| toml | `mod.parse(str)` | TOML — `pyproject.toml`, Cargo metadata |
| json5 | `mod.default.parse(str)` | JSON5 — tsconfig, babel configs with comments |
| xml | `mod.parse(str)` | XML — Maven POMs, MSBuild, SVG |
| ini | `mod.default.parse(str)` | INI — `.editorconfig`, `setup.cfg` |
| front-matter | `mod.default(str)` | YAML front matter in markdown files |

#### Examples

```js
// Parse a YAML config
import("yaml").then(function(mod) {
  var content = repoql.read("file:///docker-compose.yml", {budget: 5000}).content;
  return mod.default.load(content);
})

// Parse TOML
import("toml").then(function(mod) {
  var content = repoql.read("file:///pyproject.toml", {budget: 5000}).content;
  return mod.parse(content);
})
```

### Analysis Tools

| Module | Access pattern | What it does |
|--------|---------------|--------------|
| semver | `mod.default.satisfies(v, range)` | Semantic version comparison and ranges |
| diff | `mod.diffLines(a, b)` | Text diffs — line, word, character |
| microdiff | `mod.default(oldObj, newObj)` | Structural object diffs |
| parse-diff | `mod.default(str)` | Parses unified diff into structured hunks |

#### Examples

```js
// Check semver ranges
import("semver").then(function(mod) {
  var s = mod.default;
  return { valid: s.valid("1.4.1"), inRange: s.satisfies("1.4.1", "^1.0.0") };
})

// Diff two strings
import("diff").then(function(mod) {
  var a = "line one\nline two\n";
  var b = "line one\nline TWO\n";
  return mod.diffLines(a, b);
})
```

### Search & Matching

| Module | Access pattern | What it does |
|--------|---------------|--------------|
| fuse | `new mod.default(items, opts)` | Fuzzy search over arrays of objects |
| ignore | `mod.default().add(patterns)` | `.gitignore`-spec pattern matching |
| picomatch | `mod.default(glob)` | Glob matching against paths |

#### Examples

```js
// Fuzzy search over files
import("fuse").then(function(mod) {
  var rows = repoql.query("SELECT uri, headline FROM Files");
  var fuse = new mod.default(rows, { keys: ["uri", "headline"] });
  return fuse.search("token refresh").slice(0, 5);
})
```

### Text Processing

| Module | Access pattern | What it does |
|--------|---------------|--------------|
| mustache | `mod.default.render(tpl, data)` | Logic-less templates with iteration |
| change-case | `mod.camelCase(str)`, `mod.snakeCase(str)` | Acronym-aware case conversion |
| dayjs | `mod.default(date)` | Parse, format, compare timestamps |

#### Examples

```js
// Convert type names to different cases
import("change-case").then(function(mod) {
  var types = repoql.query("SELECT name FROM Types LIMIT 10");
  return types.map(function(t) {
    return { original: t.name, snake: mod.snakeCase(t.name), kebab: mod.kebabCase(t.name) };
  });
})
```

### Utilities

| Module | Access pattern | What it does |
|--------|---------------|--------------|
| ohash | `mod.hash(obj)`, `mod.isEqual(a, b)` | Deterministic object hashing and deep equality |
| base64 | `mod.Base64.encode(str)` | Base64 encode/decode without `atob`/`btoa` |
| radash | `mod.group(arr, fn)`, `mod.pick(obj, keys)` | Array/object shaping utilities |
| toposort | `mod.default(edges)` | Topological sort with cycle detection |

#### Examples

```js
// Topological sort of call graph
import("toposort").then(function(mod) {
  var edges = repoql.query("SELECT source_node_id, destination_node_id FROM edge WHERE type = 'CALLS' LIMIT 50");
  var pairs = edges.map(function(e) { return [e.source_node_id, e.destination_node_id]; });
  return mod.default(pairs);
})
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
