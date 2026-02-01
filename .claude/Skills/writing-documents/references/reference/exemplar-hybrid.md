# Glob Patterns Reference

Common file and symbol patterns. Data in `glob-patterns.csv`; guidance here.

## Finding Patterns

Query the CSV by tag or return type:

```sql
SELECT label, pattern, notes FROM 'glob-patterns.csv' WHERE tags LIKE '%testing%'
SELECT label, pattern, notes FROM 'glob-patterns.csv' WHERE returns = 'symbols'
```

## Pattern Syntax

| Syntax | Meaning |
|--------|---------|
| `**` | Any path depth (recursive) |
| `*` | Any characters in one segment |
| `;` | OR (combine patterns) |
| `!` | Exclude |
| `#symbol=` | Symbol fragment |
| `#line=` | Line range fragment |

## Gotchas

| Wrong | Right | Why |
|-------|-------|-----|
| `src/**test` | `src/**/test*` | `**` must be complete segment |
| `*.cs` for recursive | `**/*.cs` | `*` is single-level only |
| `#MyClass` | `#symbol=MyClass` | Fragment needs key |

## Defaults

- Patterns are case-insensitive
- Exclusions (`!`) apply globally across all includes
- Bare paths assume `file:///` scheme
