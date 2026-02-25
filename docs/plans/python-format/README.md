---
description: Python format support — three incremental plans from tree-sitter foundation through annotations and documentation
tags: [format, python, plan, overview]
audience: { human: 70, agent: 30 }
purpose: { plan: 60, gestalt: 40 }
---

# Python Format Plans

Implements: [Python Format Design](../../designs/future/python-format.md)

Three increments, each independently buildable, testable, and valuable.

| Plan | What | Enables |
|------|------|---------|
| [01 — Tree-Sitter Foundation](01-tree-sitter-foundation.md) | Project scaffold, parser, surface model, thread safety | Retires riskiest technical choice |
| [02 — Core Format Loader](02-core-format-loader.md) | Classification, materialization, X-ray, DI, views | `SELECT * FROM python_types WHERE type_kind = 'dataclass'` |
| [03 — Annotations + Documentation](03-annotations-documentation.md) | Metaprogramming honesty, framework patterns, help:// docs | Honest graph + self-documenting format |

## Dependency Chain

```
01 ──→ 02 ──→ 03
```

Linear. Each depends on the previous. No parallel plans — each increment builds directly on the previous one.

## Shared View Changes

Plan 02 modifies the shared `functions.sql` to include Python node kinds (`py.member`, `py.function`). This is the only cross-format change. All other changes are contained within `RepoQL.Formats.Python` and its test project.

## Comparison with Ruby Plans

Ruby required 5 plans due to language-specific complexity: open classes (reopening detection), mixins (include/extend/prepend with MRO), namespace graph (constants, requires, aliases), and Rails-scale metaprogramming. Python's equivalent features are either simpler (conventions-based visibility vs. state machine, multiple inheritance vs. mixins) or integral to core value (decorators, type annotations). Three plans cover the full design.
