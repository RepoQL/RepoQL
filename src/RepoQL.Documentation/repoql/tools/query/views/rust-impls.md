---
description: "rust_impls(type_uri, target_type, target_qualified_name, trait_name, is_unsafe, file_uri)"
tags: ["query", "views", "rust", "impls", "traits", "implements"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Rust Impls View

Trait implementations (`IMPLEMENTS` edges) — which types implement which traits, with unsafe tracking and source location.

## Quick Reference

```sql
-- What traits does a type implement?
SELECT trait_name FROM rust_impls WHERE target_type = 'MyType';

-- Who implements a trait?
SELECT target_type FROM rust_impls WHERE trait_name = 'Iterator';

-- Most-implemented traits
SELECT trait_name, COUNT(*) AS count
FROM rust_impls
GROUP BY trait_name
ORDER BY count DESC;
```

---

## Capsule: RustImplsScope

**Invariant**
`rust_impls` shows only TRAIT implementations. Inherent impl blocks (methods without a trait) are NOT in this view.

**Example**
```sql
-- Trait implementations for a type
SELECT trait_name, is_unsafe, file_uri
FROM rust_impls
WHERE target_type = 'RegexMatcher';

-- For inherent methods, use rust_methods instead
SELECT name, self_kind, return_type
FROM rust_methods
WHERE parent_name = 'RegexMatcher' AND impl_trait IS NULL;
```
//BOUNDARY: A common mistake is expecting `rust_impls` to list inherent impls. It does not. Inherent impl blocks are dissolved — their methods appear directly in `rust_methods` with `impl_trait IS NULL`. Only `impl Trait for Type` produces rows here.

**Depth**
- Each row is an IMPLEMENTS edge from an `rs.type` node
- `trait_name` is a deferred reference string — may reference external crate traits
- The trait node may or may not exist in the graph (external crate traits won't)
- For the methods within a trait impl, use `rust_methods WHERE impl_trait = 'TraitName'`
- SeeAlso: `rust_derives` for `#[derive()]` traits, `rust_methods` for method-level detail

---

## Capsule: RustImplsUnsafe

**Invariant**
`is_unsafe` identifies `unsafe impl Trait for Type` — implementations of unsafe traits.

**Example**
```sql
-- All unsafe trait implementations
SELECT target_type, trait_name, file_uri
FROM rust_impls
WHERE is_unsafe;
```
//BOUNDARY: `is_unsafe` marks the impl declaration itself (`unsafe impl`), not the trait definition. For unsafe trait definitions, check `rust_types WHERE type_kind = 'trait' AND is_unsafe`.

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `type_uri` | string | URI of the implementing type node |
| `target_type` | string | Simple name of the implementing type |
| `target_qualified_name` | string | Qualified name of the implementing type |
| `trait_name` | string | Name of the implemented trait (deferred reference) |
| `is_unsafe` | boolean | True for `unsafe impl` declarations |
| `file_uri` | string | Document where the impl block appears |
