---
description: Ruby format support — five incremental plans from tree-sitter foundation through metaprogramming extraction
tags: [format, ruby, plan, overview]
audience: { human: 70, agent: 30 }
purpose: { plan: 60, gestalt: 40 }
---

# Ruby Format Plans

Implements: [Ruby Format Design](../../designs/current/ruby-format.md)

Five increments, each independently buildable, testable, and valuable.

| Plan | What | Enables |
|------|------|---------|
| [01 — Tree-Sitter Foundation](01-tree-sitter-foundation.md) | Project scaffold, parser, thread safety | Retires riskiest technical choice |
| [02 — Core Format Loader](02-core-format-loader.md) | Classification, materialization, X-ray, DI, basic views | `SELECT * FROM Types WHERE lang = 'rb'` |
| [03 — Mixin Graph](03-mixin-graph.md) | include/extend/prepend, inheritance, reopening, MRO | `SELECT * FROM ruby_mixins` + unified open class view |
| [04 — Namespace Graph](04-namespace-graph.md) | Constants, requires, aliases | Dependency graph + constant discovery |
| [05 — Metaprogramming](05-metaprogramming.md) | attr_accessor, Rails patterns, honesty annotations | `SELECT * FROM ruby_associations` + honest graph |

## Dependency Chain

```
01 ──→ 02 ──→ 03
              ↗
        02 ──→ 04
              ↗
        02 + 03 ──→ 05
```

Plans 03 and 04 are independent of each other — both depend on 02 and can be built in parallel. Plan 05 depends on 02 (required) and 03 (recommended — mixin context reduces rework).

**Parallel plan note:** Plans 03 and 04 both add views to `ruby_views.sql`. If implemented in parallel, coordinate on the shared file to avoid merge conflicts.

## Shared View Changes

Plan 02 modifies the shared `functions.sql` to include Ruby node kinds. This is the only cross-format change. All other changes are contained within `RepoQL.Formats.Ruby` and its test project.
