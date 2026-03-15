---
description: Research into preloaded JavaScript libraries for RepoQL's JS sandbox, filtered against DuckDB native capabilities.
tags: [sandbox, javascript, libraries, udf, jint, duckdb]
audience: { human: 40, agent: 60 }
purpose: { research: 90, design: 10 }
---

# JavaScript Sandbox Libraries

Research for selecting preloaded libraries for RepoQL's JS sandbox (Jint). Builds on the runtime research in `js-sandbox-runtimes.md`.

*Research date: March 13, 2026*

## Context

RepoQL's JS sandbox has two entry points: **UDFs** (`js()`, `js_test()`, `js_each()`) called per-row in SQL, and a **tool** agents call for safe code execution. Libraries must justify their inclusion against what DuckDB and RepoQL already provide natively.

The filter: **Does this fill a gap that DuckDB/SQL/existing C# UDFs cannot cover?**

Secondary filter for tool-context libraries: **Would an agent naturally reach for this, and is it non-trivial to implement from scratch?**

---

## What DuckDB Already Covers (Do Not Duplicate)

This section catalogs DuckDB native capabilities that eliminate the need for JS library equivalents.

### String Distance & Similarity

DuckDB provides these **built-in, no extension needed**:

| Function | What it computes |
|----------|-----------------|
| `levenshtein(s1, s2)` | Edit distance |
| `damerau_levenshtein(s1, s2)` | Edit distance + transpositions |
| `jaro_similarity(s1, s2)` | Jaro similarity (0-1) |
| `jaro_winkler_similarity(s1, s2)` | Jaro-Winkler similarity (0-1) |
| `hamming(s1, s2)` | Positional mismatches |
| `jaccard(s1, s2)` | Jaccard similarity (0-1) |

> [DuckDB Text Functions](https://duckdb.org/docs/stable/sql/functions/text) — full string function reference

**Eliminated JS candidates**: fastest-levenshtein, fuzzball, fast-dice-coefficient, jaro-winkler, talisman distance functions.

### Hashing & Encoding

| Function | What it does |
|----------|-------------|
| `md5(value)` | MD5 hash |
| `sha1(value)` | SHA-1 hash |
| `sha256(value)` | SHA-256 hash |
| `hash(value)` | Fast internal hash |
| `to_base64(blob)` / `from_base64(string)` | Base64 |
| `hex(value)` / `unhex(string)` | Hex |
| `url_encode(s)` / `url_decode(s)` | URL percent-encoding |
| `uuid()` / `uuidv7()` | UUID generation |

Community `crypto` extension adds HMAC, SHA-512, SHA-3, BLAKE3.

> [DuckDB Blob Functions](https://duckdb.org/docs/stable/sql/functions/blob) — encoding functions

**Eliminated JS candidates**: crypto-es, jshashes, js-sha256, nanoid, uuid.

### Statistics & Aggregation

DuckDB provides 57+ aggregate functions including:

- `avg`, `stddev_pop`, `stddev_samp`, `var_pop`, `var_samp`
- `median`, `mode`, `mad` (median absolute deviation)
- `quantile_cont`, `quantile_disc`, `percentile_cont`, `percentile_disc`
- `corr`, `covar_pop`, `covar_samp`
- `regr_slope`, `regr_intercept`, `regr_r2` (linear regression)
- `skewness`, `kurtosis`, `entropy`, `sem`
- `approx_count_distinct` (HyperLogLog), `approx_quantile` (T-Digest)
- `weighted_avg(x, weight)`

> [DuckDB Aggregate Functions](https://duckdb.org/docs/stable/sql/functions/aggregates) — full reference

**Eliminated JS candidates**: simple-statistics, jstat, mathjs statistics.

### Array/List Operations

DuckDB list functions include lambda-based transforms:

- `list_transform(list, x -> expr)` — map
- `list_filter(list, x -> expr)` — filter
- `list_reduce(list, (x, y) -> expr)` — reduce
- `list_sort()`, `list_distinct()`, `list_intersect()`
- `list_contains()`, `list_position()`, `list_has_any()`
- Per-list stats: `list_avg()`, `list_sum()`, `list_median()`, `list_stddev_pop()`, etc.

> [DuckDB List Functions](https://duckdb.org/docs/stable/sql/functions/list) — full reference

**Eliminated JS candidates**: lodash/radash collection functions (in UDF context — SQL handles grouping, sorting, uniquing, filtering).

### Date/Time

DuckDB provides 60+ date/time functions:

- `strptime(text, format)` / `strftime(ts, format)` — parse and format
- `date_diff(part, start, end)`, `date_add()`, `date_trunc()`
- `age(ts1, ts2)`, `ago(interval)` — relative time
- `time_bucket(width, value)` — time bucketing
- `AT TIME ZONE` / `timezone()` — timezone conversion
- Full ICU timezone support

> [DuckDB Date Functions](https://duckdb.org/docs/stable/sql/functions/date) — full reference

**Eliminated JS candidates**: dayjs, date-fns, luxon (for UDF context — SQL handles dates).

### CSV

DuckDB's `read_csv` / `read_csv_auto` is ranked #1 on the Pollock CSV Robustness Benchmark. Supports auto-detection, multi-byte delimiters, error handling, parallel reading.

**Eliminated JS candidates**: papaparse, csv-parse, d3-dsv.

### JSON

DuckDB provides comprehensive JSON support:

- `json_extract(json, path)` / `->` / `->>` — extraction (supports JSONPath `$.key[0]` notation)
- `json_keys()`, `json_array_length()`, `json_structure()`, `json_valid()`
- `json_each()`, `json_tree()` — table-valued traversal
- `json_group_array()`, `json_group_object()` — aggregation
- `json_transform()` — convert to DuckDB nested types

Limitation: No JSONPath filter expressions (`$..book[?(@.price<10)]`). SQL handles the filtering instead.

**Eliminated JS candidates**: jsonpath-plus, json-pointer (mostly — DuckDB's json_extract covers most cases).

### Pattern Matching

- `LIKE` / `ILIKE`, `GLOB`, `SIMILAR TO`
- Full RE2 regex: `regexp_matches`, `regexp_extract`, `regexp_extract_all`, `regexp_replace`, `regexp_split_to_array`
- Named capture extraction: `regexp_extract(s, pattern, name_list)` → STRUCT
- FTS extension: `match_bm25()` for full-text search with BM25 scoring

> [DuckDB Pattern Matching](https://duckdb.org/docs/stable/sql/functions/pattern_matching) — regex functions

### Path Parsing

- `parse_dirname(path)`, `parse_dirpath(path)`, `parse_filename(path)`, `parse_path(path)`

**Eliminated JS candidates**: pathe, path-browserify (partially — DuckDB covers basics).

### Existing C# UDFs

RepoQL already has `GlobMatchUdf` in C# (`repoql_glob_match`, `repoql_matches_glob`, `symbol_matches`).

**Eliminated JS candidates**: picomatch/minimatch (for UDF context — existing C# UDF handles glob matching in SQL).

---

## Genuine Gaps: What DuckDB Cannot Do

These are the operations where JS libraries add real value.

### Format Parsing

DuckDB has no parsers for YAML, TOML, XML, INI, JSON5, or Markdown. These formats are ubiquitous in repositories.

| Library | Size (min) | Deps | Format | Prevalence in repos |
|---------|-----------|------|--------|---------------------|
| **js-yaml** | 38 KB | 1 (argparse, bundleable) | YAML 1.2 | Very high — CI, k8s, docker-compose, GitHub Actions, configs |
| **smol-toml** | 11 KB | 0 | TOML 1.1 | High — Cargo.toml, pyproject.toml, Hugo, Go-adjacent |
| **json5** | 31 KB | 0 | JSON5 | High — tsconfig.json, .babelrc, VS Code settings (JSON with comments) |
| **txml** | 5.8 KB | 0 | XML | Medium-high — pom.xml, .csproj, web.config, SVG, RSS |
| **ini** | 3 KB | 0 | INI | Medium — .gitconfig, .editorconfig, .npmrc, php.ini |
| **front-matter** | 1 KB | 1 (js-yaml, already loaded) | YAML frontmatter | Medium — Jekyll, Hugo, Docusaurus markdown files |

> [Bundlephobia](https://bundlephobia.com) — sizes verified per package

**No viable pure-JS parsers exist for**: HCL/Terraform (only a 3.2 MB GopherJS transpile), Dhall, Nix expressions, Jenkinsfile (Groovy DSL), reStructuredText.

### Semantic Versioning

DuckDB has no concept of semver. Version comparison and range matching are essential for dependency analysis.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **semver** | 25 KB | 0 | `satisfies('1.2.3', '^1.0.0')`, range parsing, coercion, prerelease comparison |

`compare-versions` (2.3 KB) is a lightweight alternative for comparison-only, but `semver` adds range matching (`^`, `~`, `>=`, `||`) which is what dependency analysis actually needs.

> [semver npm](https://www.npmjs.com/package/semver) — npm's own semver library

### Text & Object Diffing

DuckDB has zero diffing capability.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **diff** (jsdiff) | 17 KB | 0 | Line diff, word diff, character diff, unified patch creation/application |
| **microdiff** | ~1 KB | 0 | Structural object diff: `[{type:'CHANGE', path:['spec','replicas'], oldValue:3, value:5}]` |

Text diff and structural diff are complementary — one compares strings line-by-line, the other compares object shapes.

> [diff npm](https://www.npmjs.com/package/diff) — 75M weekly downloads
> [microdiff GitHub](https://github.com/AsyncBanana/microdiff) — ~1 KB minified

### Object Hashing & Deep Equality

DuckDB's `hash()` works on SQL values, not JS objects. Deterministic hashing of complex JS objects enables dedup, caching, and equality checks.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **ohash** | 6 KB | 0 | `hash(obj)` deterministic, `isEqual(a, b)` deep equality, `diff(a, b)` change detection. MurmurHash-based. |

> [ohash GitHub](https://github.com/unjs/ohash) — unjs ecosystem

### Fuzzy Multi-Key Search

DuckDB has per-pair string distance (`jaro_winkler_similarity`), but not weighted multi-key fuzzy search with scoring across document fields.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **fuse.js** | 17 KB | 0 | Bitap algorithm, configurable key weights, threshold scoring, result ranking |

The distinction: `jaro_winkler_similarity(a, b)` compares two strings. `fuse.js` searches a list of objects across multiple weighted fields and returns ranked results. This is the difference between a string function and a search engine. For the tool context (agent builds a search index from data, queries it), fuse.js fills a real gap. For UDFs, DuckDB's native distance functions may suffice for most per-row comparisons.

> [fuse.js](https://www.fusejs.io/) — lightweight fuzzy-search library

### Templating

DuckDB has `format()` and `printf()` but no iteration, conditionals, or nested data traversal.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **mustache** | 6.5 KB | 0 | `{{#items}}...{{/items}}` iteration, conditionals, partials. String-based parsing (no `new Function()`, safe in sandbox). |

Agents generating reports, formatted output, or structured text from query data reach for this.

> [mustache.js GitHub](https://github.com/janl/mustache.js) — logic-less templates

### .gitignore Pattern Matching

Different from glob matching — `.gitignore` has specific rules (negation with `!`, directory-only patterns with trailing `/`, anchoring, `**` semantics).

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **ignore** | 3.7 KB | 0 | Full .gitignore spec compliance. Apply ignore rules to file lists. |

> [ignore npm](https://www.npmjs.com/package/ignore) — spec-compliant .gitignore matching

### Git Diff Parsing

Parsing unified diff format into structured hunks. Different from generating diffs — this parses existing git diff output.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **parse-diff** | 5.5 KB | 0 | Parse unified diffs into structured `{from, to, chunks: [{changes}]}` |

> [parse-diff npm](https://www.npmjs.com/package/parse-diff)

### Base64 in JS Context

DuckDB has `to_base64`/`from_base64` for SQL-side use. But JS code inside the sandbox needs base64 for JWT decoding, parsing embedded data, and data URIs. Jint does not provide `atob`/`btoa` (Web APIs, not ECMAScript).

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **js-base64** | 5 KB | 0 | `Base64.encode()`, `Base64.decode()`, `Base64.atob()`, `Base64.btoa()`. No TextEncoder required. ES5 compatible. |

> [js-base64 npm](https://www.npmjs.com/package/js-base64) — pure JS, no Web API deps

---

## Tool-Context Libraries

These are less about filling SQL gaps and more about making the agent sandbox a productive environment. Without `Intl` (ECMA-402, not in Jint), some operations that would be trivial in a browser need library support.

### Date/Time Formatting (No Intl)

Jint has no `Intl.RelativeTimeFormat` or `Intl.DateTimeFormat`. DuckDB handles dates in SQL, but when agents work with date strings in JS (from MCP responses, parsed configs, etc.), they need formatting capabilities.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **dayjs** | 7.1 KB | 0 | `.format('YYYY-MM-DD')`, `.fromNow()` (with relativeTime plugin), `.diff()`, `.isBefore()`. Moment.js-compatible API. |

> [dayjs](https://day.js.org/) — 2KB core, plugins for extended functionality

### Case Conversion

Correctly splitting compound words and handling acronyms is non-trivial. `"XMLParser"` → `"xml-parser"`, not `"xmlparser"` or `"x-m-l-parser"`. Each case conversion with edge cases is 30+ lines.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **change-case** | ~6 KB | 0 | `camelCase`, `snakeCase`, `kebabCase`, `pascalCase`, `constantCase`, `dotCase`, `pathCase` |

> [change-case npm](https://www.npmjs.com/package/change-case) — pure ESM, zero deps

### Object/Array Utilities

In the tool context, agents process objects and arrays outside of SQL. Native JS covers map/filter/reduce, but grouped operations, deep path access, and set operations on arrays are non-trivial.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **radash** | 11.8 KB | 0 | `group()`, `unique()` with key fn, `pick()`, `omit()`, `get()`/`set()` (dot-path), `objectify()`, `range()`, `cluster()`, `diff()`, `intersect()`, `tryit()` |

In the UDF context, SQL handles these operations. In the tool context, radash's utility density justifies inclusion.

> [radash GitHub](https://github.com/rayepps/radash) — modern lodash alternative

### Glob Matching in JS

RepoQL has `GlobMatchUdf` in C# for SQL, but JS code in the sandbox tool can't call C# UDFs. When an agent has a list of paths in JS and wants to filter by glob, it needs picomatch.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **picomatch** | 20 KB | 0 | Programmatic glob matching: `picomatch('src/**/*.ts')(path)`. Braces, negation, extglobs. |

> [picomatch GitHub](https://github.com/micromatch/picomatch) — used by 5M+ projects

### Dependency Ordering

DuckDB has recursive CTEs for graph traversal but topological sort with cycle detection is non-trivial to express in SQL.

| Library | Size (min) | Deps | What it adds |
|---------|-----------|------|-------------|
| **toposort** | 1.2 KB | 0 | Topological sort with cycle detection on edge lists |

> [toposort npm](https://www.npmjs.com/package/toposort)

---

## Recommended Library Set

### Tier 1 — Fill genuine DuckDB gaps

Every library here enables operations that are impossible in SQL.

| # | Library | Size | Category | DuckDB gap |
|---|---------|------|----------|------------|
| 1 | **js-yaml** | 38 KB | Format | YAML parsing |
| 2 | **smol-toml** | 11 KB | Format | TOML parsing |
| 3 | **json5** | 31 KB | Format | JSON-with-comments parsing |
| 4 | **txml** | 5.8 KB | Format | XML parsing |
| 5 | **ini** | 3 KB | Format | INI parsing |
| 6 | **semver** | 25 KB | Analysis | Semantic version ranges |
| 7 | **diff** | 17 KB | Comparison | Text diffing (line, word, char) |
| 8 | **microdiff** | ~1 KB | Comparison | Structural object diffing |
| 9 | **ohash** | 6 KB | Comparison | Deterministic object hashing, deep equality |
| 10 | **fuse.js** | 17 KB | Search | Weighted multi-key fuzzy search |
| 11 | **ignore** | 3.7 KB | Matching | .gitignore-spec pattern matching |

### Tier 2 — Compensate for missing platform APIs

These compensate for Jint lacking `Intl`, `atob`/`btoa`, and other Web/Node APIs.

| # | Library | Size | Category | What's missing |
|---|---------|------|----------|---------------|
| 12 | **js-base64** | 5 KB | Encoding | No `atob`/`btoa` in Jint |
| 13 | **dayjs** | 7.1 KB | Date/Time | No `Intl.DateTimeFormat` or `Intl.RelativeTimeFormat` |
| 14 | **change-case** | ~6 KB | String | Correct acronym-aware case conversion |
| 15 | **mustache** | 6.5 KB | Templating | No iteration/conditional templating in DuckDB's `format()` |

### Tier 3 — Tool-context productivity

These primarily serve agents using the sandbox tool for data processing.

| # | Library | Size | Category | Why |
|---|---------|------|----------|-----|
| 16 | **radash** | 11.8 KB | Utilities | group, pick, omit, get/set, objectify — non-trivial in native JS |
| 17 | **picomatch** | 20 KB | Matching | Glob matching in JS code (C# UDF unavailable from sandbox) |
| 18 | **toposort** | 1.2 KB | Graph | Topological sort with cycle detection |
| 19 | **front-matter** | 1 KB | Format | YAML frontmatter extraction (free since js-yaml loaded) |
| 20 | **parse-diff** | 5.5 KB | Analysis | Parse unified diff output into structured hunks |

**Total: ~222 KB minified across 20 libraries.**

---

## Eliminated Candidates (With Reasons)

| Library | Why eliminated |
|---------|--------------|
| fastest-levenshtein | DuckDB: `levenshtein()`, `damerau_levenshtein()` |
| papaparse | DuckDB: `read_csv` (best-in-class CSV) |
| simple-statistics | DuckDB: stddev, median, percentile, regression, kurtosis, skewness, entropy, etc. |
| compare-versions | Replaced by full `semver` which adds range matching |
| pathe / path-browserify | DuckDB: `parse_dirname()`, `parse_filename()`, `parse_path()` |
| crypto-es / jshashes | DuckDB: `md5()`, `sha1()`, `sha256()`, community crypto extension |
| lodash | Replaced by radash (smaller, modern, covers agent-relevant subset) |
| fuzzball | DuckDB: `jaro_winkler_similarity()`, `levenshtein()` |
| date-fns / luxon | Replaced by dayjs (smaller, sufficient for Intl-less environment) |
| jsonpath-plus | DuckDB: `json_extract()` with JSONPath notation covers most cases |
| ajv | Too large (112 KB + deps), schema validation better at index time |
| graphql | Too large (164 KB) for preloading |
| acorn | Parsing at query time conflicts with RepoQL's index-once architecture |
| natural / compromise | NLP libraries too large (100+ KB) |

---

## Gaps

- **Library compatibility with Jint**: No published test results for any of these libraries running in Jint v4.6.x. Each needs a smoke test. Specific risks: js-yaml uses `argparse` (needs bundling), some libraries may use `TextEncoder` (not in Jint)
- **Jint `Intl` support**: Jint v4.6.0 release notes mention `Intl` and `Temporal` additions. If `Intl.DateTimeFormat` is actually available, dayjs becomes less necessary. Needs verification.
- **DuckDB `crypto` extension availability**: Whether the community crypto extension is auto-loaded or requires explicit installation affects whether JS-side hashing is needed
- **smol-toml gzip anomaly**: Bundlephobia reports 347 bytes gzipped for 11 KB minified — likely an artifact. Minified size is reliable.
- **`TextEncoder`/`TextDecoder` polyfill**: A ~1 KB polyfill (fast-text-encoding) would unlock modern libraries that depend on these APIs. Whether to include it as a built-in polyfill is a design decision.
- **fuse.js vs DuckDB distance functions**: For simple per-row fuzzy comparison, `jaro_winkler_similarity()` in SQL may be sufficient. Fuse.js's value is for multi-field ranked search, which is primarily a tool-context operation.

## Leads Not Pursued

- **graphology** (71-159 KB) — Full graph library with centrality, SCC, shortest path. Heavy but covers real DuckDB gaps (betweenness centrality, strongly connected components). Worth evaluating as an opt-in library.
- **double-metaphone** (3 KB) — Phonetic matching not in DuckDB. Niche but unique capability.
- **stemmer** (1 KB) — Porter stemming. DuckDB FTS has `stem()` but only in FTS context.
- **spdx-expression-parse** (13 KB) — License compliance analysis on compound SPDX expressions.
- **croner** (5 KB) — Cron expression parsing. Niche but irreplaceable when needed.
- **docker-file-parser** (3.1 KB) — Dockerfile structure extraction. Tiny and specific.
- **superstruct** (10.5 KB) — Schema validation. Borderline — native JS shape checks work for simple cases.
- **jsonata** (74 KB) — JSON query/transform language. Powerful but large; better as opt-in.
- **marked** (39 KB) — Markdown to token stream. Useful for analyzing markdown files but large.
- **wink-nlp modules** — Standalone TF-IDF scoring without FTS index. Jint compatibility unknown.
