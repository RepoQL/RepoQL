---
description: "URI patterns for addressing files and symbols — globs, fragments, combining with ;, excluding with !"
tags: ["patterns", "glob", "wildcards", "scope", "symbols"]
audience: ["LLMs"]
categories: ["Reference[100%]"]
---

# URI Patterns

Glob-style patterns for selecting files and symbols. Works in `read`, `explore`, scope parameters, and SQL.

## Patterns

| Pattern | Meaning | Example |
|---------|---------|---------|
| `*` | Any chars, one segment | `src/*.cs` → `src/File.cs` (not subdirs) |
| `**` | Any path depth | `src/**/*.cs` → `src/a/b/File.cs` |
| `?` | One character | `src/???.cs` → `src/Foo.cs` |
| `;` | OR (combine) | `src/**;lib/**` → either directory |
| `!` | Exclude | `src/**;!**/test*` → skip tests |
| `#symbol=` | Symbol pattern | `**/*.cs#symbol=*Handler` |
| `#line=` | Line range | `File.cs#line=10,50` |

## Symbol Patterns

| Pattern | Matches |
|---------|---------|
| `MyClass` | Exact name only |
| `*Handler` | Anything ending in Handler |
| `MyClass.*` | Direct children (one level) |
| `MyClass.**` | All descendants (any depth) |

## Examples

```
**/*.cs                              All C# files
src/**;lib/**                        Multiple directories
src/**;!**/tests/**                  Exclude tests
**/*.cs#symbol=*Service              All *Service symbols
**/*.cs#symbol=*;!#line=1,30         Symbols outside file header
File.cs#symbol=MyClass.*             Direct members of MyClass
```

## Multi-Fetch

Use `;` with specific URIs to fetch multiple locations in one call:

```
Auth.cs;Config.cs;Startup.cs                     Three specific files
Auth.cs#line=10,20;Token.cs#line=50,60           Two line ranges
Auth.cs#symbol=Login;Token.cs#symbol=Refresh     Two symbols
Auth.cs#symbol=Login;config.json;README.md       Mixed: symbol + files
```

Powerful for gathering context from known locations without multiple round-trips.

## Gotchas

| Wrong | Right | Why |
|-------|-------|-----|
| `src/**test` | `src/**/test*` | `**` must be complete segment |
| `File.cs#MyClass` | `File.cs#symbol=MyClass` | Need `#symbol=` key |
| `*.cs` for recursive | `**/*.cs` | `*` is single-level only |
| `search(scope='%.cs')` | `search(scope='**/*.cs')` | `search()` uses glob patterns, not SQL LIKE |

## Defaults

- Bare paths assume `file:///` scheme
- Case insensitive matching
- Exclusions apply globally across all includes

## Pattern Library

Query `glob-patterns.csv` for copy-paste patterns:

```sql
SELECT label, pattern, notes FROM 'help:///repoql/tools/query/functions/glob-patterns.csv'
WHERE tags LIKE '%testing%'
```
