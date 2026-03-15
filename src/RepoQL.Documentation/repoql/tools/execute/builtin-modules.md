---
title: Built-in Modules
description: JavaScript libraries available in the sandbox via import
---

# Built-in Modules

The sandbox includes 20 built-in JavaScript libraries. Use dynamic `import()`:

```js
import("yaml").then(function(yaml) {
  var doc = yaml.default.load(repoql.read("file:///config.yml").content);
  return doc;
})
```

Both bare names (`"yaml"`) and prefixed (`"repoql:yaml"`) work.

> **Note:** The entry script runs in global mode. Use `import("name")` with `.then()`, not static `import`. CJS packages (yaml, semver, json5, ini, fuse, mustache, picomatch, toposort, front-matter, parse-diff) expose functions on `mod.default`. ESM packages (change-case, ohash, radash, diff, microdiff, ignore, base64, dayjs, toml, xml) have named exports directly.

## Format Parsers

| Module | Access pattern | What it parses |
|--------|---------------|----------------|
| yaml | `mod.default.load(str)` | YAML 1.2 — CI configs, k8s manifests, GitHub Actions |
| toml | `mod.parse(str)` | TOML — `pyproject.toml`, Cargo metadata |
| json5 | `mod.default.parse(str)` | JSON5 — tsconfig, babel configs with comments |
| xml | `mod.parse(str)` | XML — Maven POMs, MSBuild, SVG |
| ini | `mod.default.parse(str)` | INI — `.editorconfig`, `setup.cfg` |
| front-matter | `mod.default(str)` | YAML front matter in markdown files |

### Examples

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

## Analysis Tools

| Module | Access pattern | What it does |
|--------|---------------|--------------|
| semver | `mod.default.satisfies(v, range)` | Semantic version comparison and ranges |
| diff | `mod.diffLines(a, b)` | Text diffs — line, word, character |
| microdiff | `mod.default(oldObj, newObj)` | Structural object diffs |
| parse-diff | `mod.default(str)` | Parses unified diff into structured hunks |

### Examples

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

## Search & Matching

| Module | Access pattern | What it does |
|--------|---------------|--------------|
| fuse | `new mod.default(items, opts)` | Fuzzy search over arrays of objects |
| ignore | `mod.default().add(patterns)` | `.gitignore`-spec pattern matching |
| picomatch | `mod.default(glob)` | Glob matching against paths |

### Examples

```js
// Fuzzy search over files
import("fuse").then(function(mod) {
  var rows = repoql.query("SELECT uri, headline FROM Files");
  var fuse = new mod.default(rows, { keys: ["uri", "headline"] });
  return fuse.search("token refresh").slice(0, 5);
})
```

## Text Processing

| Module | Access pattern | What it does |
|--------|---------------|--------------|
| mustache | `mod.default.render(tpl, data)` | Logic-less templates with iteration |
| change-case | `mod.camelCase(str)`, `mod.snakeCase(str)` | Acronym-aware case conversion |
| dayjs | `mod.default(date)` | Parse, format, compare timestamps |

### Examples

```js
// Convert type names to different cases
import("change-case").then(function(mod) {
  var types = repoql.query("SELECT name FROM Types LIMIT 10");
  return types.map(function(t) {
    return { original: t.name, snake: mod.snakeCase(t.name), kebab: mod.kebabCase(t.name) };
  });
})
```

## Utilities

| Module | Access pattern | What it does |
|--------|---------------|--------------|
| ohash | `mod.hash(obj)`, `mod.isEqual(a, b)` | Deterministic object hashing and deep equality |
| base64 | `mod.Base64.encode(str)` | Base64 encode/decode without `atob`/`btoa` |
| radash | `mod.group(arr, fn)`, `mod.pick(obj, keys)` | Array/object shaping utilities |
| toposort | `mod.default(edges)` | Topological sort with cycle detection |

### Examples

```js
// Topological sort of call graph
import("toposort").then(function(mod) {
  var edges = repoql.query("SELECT source_node_id, destination_node_id FROM edge WHERE type = 'CALLS' LIMIT 50");
  var pairs = edges.map(function(e) { return [e.source_node_id, e.destination_node_id]; });
  return mod.default(pairs);
})
```
