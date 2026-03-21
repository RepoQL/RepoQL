---
description: Plan for Ruby format — mixin extraction, inheritance edges, reopening detection, and method resolution order views
tags: [format, ruby, plan, mixins, inheritance, mro]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Ruby — Mixin Graph and Inheritance

Implements: [Ruby Format Design](../../designs/current/ruby-format.md) — Graph Materialization (EXTENDS, INCLUDES, PREPENDS, EXTENDS_MODULE edges), Cross-Cutting Concerns (Distributed definitions, MRO), SQL Views (ruby_mixins, ruby_mro, ruby_inheritance)

## Scope

**Covers:**
- Surface model additions: `Mixins[]` on classes and modules, `has_superclass_declaration` on classes
- Mixin extraction: `include`, `extend`, `prepend` with module name and source-order ordinal
- Superclass extraction from class declarations
- `extend self` detection on modules
- Edge materialization: EXTENDS, INCLUDES, PREPENDS, EXTENDS_MODULE
- Reopening detection heuristic (`is_reopening` property on `rb.type`)
- `ruby_types` view replacement with full reopening-aware version (defined_in, file_uri, is_reopening-dependent ordering)
- SQL views: `ruby_mixins`, `ruby_mro`, `ruby_inheritance`
- Tests: mixin ordering, reopening detection, inheritance edges, MRO tier ordering

**Does not cover:**
- Full cross-file C3 linearization (extension point — requires multi-file analysis)
- Constants, requires, aliases (Plan: 04-namespace-graph)
- Metaprogramming patterns (Plan: 05-metaprogramming)

## Enables

Once this exists:
- **Mixin queries work** — `SELECT * FROM ruby_mixins WHERE type_name = 'User'` shows included/prepended modules
- **MRO is queryable** — `SELECT * FROM ruby_mro WHERE type_name = 'User'` shows method resolution order
- **Inheritance is traversable** — `SELECT * FROM ruby_inheritance WHERE class_name = 'Admin'` shows superclass chain
- **Open classes are unified** — `SELECT * FROM ruby_types WHERE qualified_name = 'User'` shows one row with definition_count, all files, and the origin file
- **Plan 05 benefits** — metaprogramming extraction (e.g., `has_many` associations) can attach to the same `rb.type` nodes that now carry mixin and inheritance context

This is what makes Ruby queryable as Ruby, not just "another language with classes and methods."

## Prerequisites

- Plan 02 complete — `rb.type` and `rb.member` nodes exist, `RubyDocumentSurface` populated, materialization pipeline operational
- `RubyTreeSitterClient` already extracts include/extend/prepend and superclass data (Plan 01 queries)

## North Star

When a module is mixed into nine models, the agent sees all nine relationships with the correct ordinals. When a class inherits from ActiveRecord::Base and includes three concerns, the method resolution order is one query away. When a class is reopened in three files, the agent sees one unified picture.

## Done Criteria

### Surface Model
- `RubyMixinInfo` shall carry: module_name, mechanism (include/extend/prepend), ordinal (source position within class/module)
- Classes and modules shall each carry a `Mixins[]` collection
- `RubyClassInfo.has_superclass_declaration` shall be true when the class has a `< SuperClass` clause
- `RubyClassInfo.superclass` shall carry the superclass name (including scope resolution, e.g., `ActiveRecord::Base`)

### Mixin Edge Materialization
- The materializer shall create INCLUDES edges from `rb.type` to included module name
- The materializer shall create PREPENDS edges from `rb.type` to prepended module name
- The materializer shall create EXTENDS_MODULE edges when `extend self` is detected
- INCLUDES and PREPENDS edges shall carry `target` (module name) and `ordinal` (source order) in props
- All mixin edges shall be reference edges: `IsComposition = false`, `DstId = null`

### Superclass Edge Materialization
- The materializer shall create EXTENDS edges from class `rb.type` to superclass name
- EXTENDS edges shall carry `target` (superclass name) in props
- EXTENDS edges shall be reference edges: `IsComposition = false`, `DstId = null`
- When class has no superclass, no EXTENDS edge shall be created

### Reopening Detection
- **Within a single file:** when the same class name appears twice in one file, one with superclass and one without, the materializer shall set `is_reopening: "true"` on the definition without a superclass clause
- **Cross-file reopening** is not detectable during single-file materialization (the materializer processes one DocumentModel at a time and must not query the database). Cross-file reopening is handled by the `ruby_types` view: `definition_count > 1` signals a distributed definition. The `file_uri` column uses `is_reopening` from within-file detection to find the primary definition
- When both definitions in the same file lack a superclass clause, `is_reopening` shall default to `"false"` for both
- When uncertain, `is_reopening` shall always default to `"false"` — false negatives preferred over false positives

### SQL Views
- `ruby_mixins` shall show: type_uri, type_name, type_qualified_name, type_kind, mechanism, module_name, mixin_order
- `ruby_mixins` shall be ordered by type then mixin_order
- `ruby_mro` shall order mixins by tier: PREPENDS (tier 0), INCLUDES (tier 1), EXTENDS_MODULE (tier 2), then by mixin_order within tier
- `ruby_inheritance` shall show: class_uri, class_name, qualified_name, superclass_name
- `ruby_inheritance` shall only include `rb.type` nodes with EXTENDS edges
- Plan 03 shall replace the Plan 02 `ruby_types` view with the full version: aggregated by `n.properties->>'qualified_name'`, showing definition_count, `defined_in` (file list ordered by is_reopening), `file_uri` (first non-reopening definition via MIN/CASE), extends (MAX across definitions), structure

### Ordinal Correctness
- When a class includes modules A, B, C in that order, ordinals shall be 0, 1, 2
- When a class prepends module X then includes module Y, the MRO view shall show X (tier 0) before Y (tier 1)
- Ordinals shall reflect source order within a single class/module body, not across files

## Constraints

- **Within-file MRO only** — ordinals are per-file. Full cross-file C3 linearization requires multi-file analysis to resolve mixin names to actual module node IDs. Deferred to extension point
- **Reopening heuristic is conservative** — false negatives (marking a reopening as original) are preferred over false positives. The `definition_count` column in `ruby_types` is a more reliable signal
- **Deferred reference pattern** — all reference edges use `DstId = null` with target name in props. Resolution to actual node IDs happens in multi-file analysis, same as PHP EXTENDS/IMPLEMENTS and C# symbol resolution
- **`extend SomeModule` vs `extend self`** — `extend self` (detected by `self` keyword as argument) produces an EXTENDS_MODULE edge with no target. `extend SomeModule` (constant as argument) produces an EXTENDS_MODULE edge with `target` prop set to the module name. The `ruby_mixins` view shows both; agents distinguish by checking whether `module_name` is null (self) or populated (external module)

## References

- [Ruby Format Design](../../designs/current/ruby-format.md) — Graph Materialization (Edges), Cross-Cutting Concerns (Distributed definitions, MRO, Deferred references)
- [PHP Trait/Inheritance Edges](../../../src/Formats/RepoQL.Formats.PHP/PHPLoader.cs) — reference for EXTENDS/USES_TRAIT deferred edge pattern
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

| Failure | Behavior |
|---------|----------|
| Module name in include/extend/prepend is a complex expression (not a constant) | Skip edge, log diagnostic. Pattern: `include SomeMethod()` is not extractable |
| Superclass expression too complex (e.g., computed) | Skip EXTENDS edge, log diagnostic. Set `extends` prop to null |
| Mixin ordinal computation fails | Assign ordinal -1, log diagnostic |
