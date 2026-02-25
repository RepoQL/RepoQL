---
description: "rust_types(qualified_name, name, type_kind, visibility, generics, derives, supertraits, is_unsafe, definition_count, defined_in, structure)"
tags: ["query", "views", "rust", "types", "structs", "enums", "traits", "unions"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Rust Types View

Rust type declarations (`rs.type`) aggregated by qualified name — structs, enums, traits, unions, and type aliases with derives, generics, and stub deduplication.

## Quick Reference

```sql
-- All Rust types
SELECT qualified_name, type_kind, visibility, derives FROM rust_types;

-- Structs with derives
SELECT name, derives
FROM rust_types
WHERE type_kind = 'struct' AND derives IS NOT NULL;

-- Traits with supertraits
SELECT name, supertraits
FROM rust_types
WHERE type_kind = 'trait';
```

---

## Capsule: RustTypesKind

**Invariant**
`rust_types` exposes one row per unique qualified name with a `type_kind` discriminator.

**Example**
```sql
SELECT type_kind, COUNT(*) AS count
FROM rust_types
GROUP BY type_kind;

SELECT name, generics
FROM rust_types
WHERE type_kind = 'union';
```
//BOUNDARY: `type_kind` is one of: `struct`, `enum`, `trait`, `union`, `type_alias`. All are `rs.type` nodes — there is no `rs.struct` or `rs.enum` node kind.

**Depth**
- `type_alias` includes `type Alias = Target;` declarations
- `union` includes `union Name { ... }` — Rust's unsafe union types
- `generics` is raw text (e.g., `<'a, T: Clone + Send>`) — not parsed into individual parameters
- SeeAlso: `rust_methods`, `rust_impls`, shared `Types` view

---

## Capsule: RustTypesDeduplication

**Invariant**
The view groups by `qualified_name`, preferring non-stub definitions for all column values. Stub types are created when an impl block references a type not defined in the same file.

**Example**
```sql
-- Types with multiple definitions (stub + real)
SELECT qualified_name, definition_count, defined_in
FROM rust_types
WHERE definition_count > 1;

-- Check if a type is stub-only (no real definition found)
SELECT qualified_name, type_kind
FROM rust_types
WHERE definition_count = 1
  AND structure IS NULL;
```
//BOUNDARY: `defined_in` is a LIST (array) of document URIs. Use `UNNEST(defined_in)` to expand, `list_contains(defined_in, 'uri')` to filter, or `array_to_string(defined_in, ', ')` to display.

**Depth**
- Stubs carry minimal metadata — name and kind only
- When a real definition exists, all columns (visibility, generics, derives, structure) come from it
- `definition_count` counts distinct documents containing an `rs.type` node with this qualified name
- SeeAlso: `rust_impls` for where trait implementations are declared

---

## Capsule: RustTypesDerives

**Invariant**
The `derives` column is a comma-separated string summary. For per-trait filtering, use the `rust_derives` view instead.

**Example**
```sql
-- Types with Debug
SELECT name
FROM rust_types
WHERE derives LIKE '%Debug%';

-- Better: per-derive querying via rust_derives
SELECT type_name
FROM rust_derives
WHERE derived_trait = 'Debug';
```
//BOUNDARY: `derives` is a display string (e.g., `"Debug, Clone, PartialEq"`). It is NOT an array — `LIKE` works but array functions do not. `rust_derives` has one row per type x trait combination for precise querying.

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `qualified_name` | string | `module::TypeName` — primary key, grouping identity |
| `name` | string | Simple type name |
| `type_kind` | string | `struct`, `enum`, `trait`, `union`, or `type_alias` |
| `visibility` | string | `public`, `pub_crate`, `pub_super`, `pub_in:path`, or `private` |
| `generics` | string | Raw generic parameters text (e.g., `<'a, T: Clone>`) |
| `derives` | string | Comma-separated derived traits (e.g., `Debug, Clone`) |
| `supertraits` | string | Trait supertrait bounds (e.g., `Send + Sync`) — traits only |
| `is_unsafe` | boolean | True for `unsafe trait` declarations |
| `definition_count` | integer | Number of documents containing this qualified name |
| `defined_in` | list | Array of document URIs — use `UNNEST()` or `list_contains()` |
| `structure` | string | X-ray structure text |
