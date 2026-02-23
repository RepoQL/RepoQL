---
description: Go format support — five incremental plans from tree-sitter foundation through interface satisfaction
tags: [format, go, golang, plan, overview]
audience: { human: 70, agent: 30 }
purpose: { plan: 60, gestalt: 40 }
---

# Go Format Plans

Implements: [Go Format Design](../../designs/future/go-format.md)

Five increments, each independently buildable, testable, and valuable.

| Plan | What | Enables |
|------|------|---------|
| [01 — Tree-Sitter Foundation](01-tree-sitter-foundation.md) | Project scaffold, parser, queries, surface model, thread safety | Retires riskiest technical choice |
| [02 — Core Format Loader](02-core-format-loader.md) | Classification, materialization, X-ray, DI, core views | `SELECT * FROM Types WHERE lang = 'go'` |
| [03 — Extended Structure](03-extended-structure.md) | Type defs, constants/iota, variables, directives, test detection | `SELECT * FROM go_constants`, `go_tests`, `go_directives` |
| [04 — Module Metadata](04-module-metadata.md) | go.mod/go.work parsing, dependency edges | `SELECT * FROM go_dependencies` |
| [05 — Interface Satisfaction](05-interface-satisfaction.md) | Cross-file method set computation, IMPLEMENTS edges | `SELECT * FROM go_implements WHERE interface_name = 'Handler'` |

## Dependency Chain

```
01 ──→ 02 ──→ 03
              ↗
        02 ──→ 04
              ↗
        02 + (03) ──→ 05
```

Plans 03 and 04 are independent of each other — both depend on 02 and can be built in parallel. Plan 05 depends on 02 (required) and benefits from 03 (embedding data from struct fields improves interface satisfaction accuracy, but is not strictly required).

**Parallel plan note:** Plans 03 and 04 both add views to `go_views.sql`. If implemented in parallel, coordinate on the shared file to avoid merge conflicts.

## Shared View Changes

Plan 02 modifies the shared `functions.sql` to include Go node kinds (`go.member`, `go.function`). This is the only cross-format change. All other changes are contained within `RepoQL.Formats.Go` and its test project.

## Differences from Ruby Plans

Go is a simpler language than Ruby. The plan series reflects this:

- **No mixin/inheritance plan** — Go has no mixins, no inheritance. Struct embedding is handled in Plan 02 (core) as `EMBEDS` edges
- **No metaprogramming plan** — Go has no metaprogramming. No `define_method`, no `eval`, no generated methods. The graph is complete for what the parser sees
- **Interface satisfaction is new** — no Ruby equivalent. Implicit interfaces require cross-file method set computation during idle processing. This is Go's unique challenge
- **go.mod is a separate file format** — unlike Ruby's Gemfile (a Ruby DSL), go.mod has its own syntax and gets its own plan
