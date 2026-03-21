---
description: Rust format support — four incremental plans from tree-sitter foundation through macro honesty
tags: [format, rust, plan, overview]
audience: { human: 70, agent: 30 }
purpose: { plan: 60, gestalt: 40 }
---

# Rust Format Plans

Implements: [Rust Format Design](../../designs/future/rust-format.md)

Four increments, each independently buildable, testable, and valuable.

| Plan | What | Enables |
|------|------|---------|
| [01 — Tree-Sitter Foundation](01-tree-sitter-foundation.md) | Project scaffold, parser, thread safety | Retires riskiest technical choice |
| [02 — Core Format Loader](02-core-format-loader.md) | Classification, materialization, X-ray, DI, basic views | `SELECT * FROM Types WHERE lang = 'rs'` |
| [03 — Trait Graph](03-trait-graph.md) | IMPLEMENTS/EXTENDS/DERIVES edges, stub nodes, cross-file impls | `SELECT * FROM rust_impls WHERE trait_name = 'Storage'` |
| [04 — Imports, Macros, Honesty](04-imports-macros.md) | Use declarations, macro_rules!, macro annotations, honesty surface | `SELECT * FROM rust_macro_expansion` + honest graph |

## Dependency Chain

```
01 ──→ 02 ──→ 03
             ↗
       02 ──→ 04
```

Plans 03 and 04 are independent of each other — both depend on 02 and can be built in parallel. Plan 04 contributes to the same `rust_macro_expansion` view as Plan 03's derive annotations; both are additive.

**Parallel plan note:** Plans 03 and 04 both add views to `rust_views.sql`. If implemented in parallel, coordinate on the shared file to avoid merge conflicts.

## Shared View Changes

Plan 02 modifies the shared `functions.sql` to include Rust node kinds (`rs.member`, `rs.function`). This is the only cross-format change. All other changes are contained within `RepoQL.Formats.Rust` and its test project.
