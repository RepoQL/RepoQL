---
description: "rust_methods(document_uri, parent_uri, parent_name, parent_qualified_name, method_uri, headline, name, qualified_name, declaring_type, visibility, is_async, is_unsafe, is_const, is_static, self_kind, parameters, return_type, impl_trait)"
tags: ["query", "views", "rust", "methods", "impl", "self", "async"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Rust Methods View

Method-level Rust members (`rs.member`) within impl blocks — with parent type, self receiver kind, and trait implementation context.

## Quick Reference

```sql
-- All methods on a type
SELECT name, self_kind, return_type, impl_trait
FROM rust_methods
WHERE parent_name = 'MyType';

-- Async methods
SELECT parent_name, name
FROM rust_methods
WHERE is_async;

-- Constructors (no self)
SELECT parent_name, name, return_type
FROM rust_methods
WHERE self_kind = 'none';
```

---

## Capsule: RustMethodsSelfKind

**Invariant**
`self_kind` encodes how the method receives its receiver, distinguishing instance methods from associated functions.

**Example**
```sql
-- Methods that mutate
SELECT parent_name, name
FROM rust_methods
WHERE self_kind = '&mut self';

-- Associated functions (constructors, static methods)
SELECT parent_name, name, return_type
FROM rust_methods
WHERE self_kind = 'none';

-- Self kind distribution
SELECT self_kind, COUNT(*) AS count
FROM rust_methods
GROUP BY self_kind
ORDER BY count DESC;
```
//BOUNDARY: `self_kind` is one of: `self` (owned), `&self` (shared borrow), `&mut self` (mutable borrow), `none` (associated function). `is_static` is true when `self_kind = 'none'`.

**Depth**
- `self` (owned): method consumes the receiver — often indicates a builder `.build()` or conversion
- `&self`: immutable access — the dominant pattern
- `&mut self`: mutable access — modifying methods
- `none`: no receiver — constructors (`new`), conversion functions, type-level operations
- SeeAlso: `rust_functions` for top-level functions (not inside impl blocks)

---

## Capsule: RustMethodsTraitContext

**Invariant**
`impl_trait` distinguishes methods from trait implementations vs inherent impl blocks.

**Example**
```sql
-- Trait implementation methods
SELECT parent_name, name, impl_trait
FROM rust_methods
WHERE impl_trait IS NOT NULL;

-- Inherent methods only (no trait)
SELECT parent_name, name, self_kind
FROM rust_methods
WHERE impl_trait IS NULL;

-- Methods implementing a specific trait
SELECT parent_name, name
FROM rust_methods
WHERE impl_trait = 'Display';
```
//BOUNDARY: `impl_trait` is the trait name when the method comes from `impl Trait for Type { ... }`. It is null for methods in `impl Type { ... }` (inherent impls). The `rust_impls` view provides the type-level perspective of the same relationship.

**Depth**
- `impl_trait` is a string — may reference external crate traits not in the graph
- `declaring_type` is the type name the method was declared on
- `parent_name` / `parent_qualified_name` reference the parent `rs.type` node
- For cross-referencing: `rust_methods.impl_trait` corresponds to `rust_impls.trait_name`

---

## Capsule: RustMethodsFlags

**Invariant**
Boolean flags surface async, unsafe, and const qualifiers on methods.

**Example**
```sql
-- Unsafe methods
SELECT parent_name, name, document_uri
FROM rust_methods
WHERE is_unsafe;

-- Const methods
SELECT parent_name, name
FROM rust_methods
WHERE is_const;
```
//BOUNDARY: These flags are syntactic — they reflect the `async`, `unsafe`, and `const` keywords on the method signature. They do not propagate from callees.

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `document_uri` | string | Document containing this method |
| `parent_uri` | string | URI of the parent type node |
| `parent_name` | string | Simple name of the parent type |
| `parent_qualified_name` | string | Qualified name of the parent type |
| `method_uri` | string | Method symbol URI — use with `read()` or `snippet()` |
| `headline` | string | X-ray headline for the method |
| `name` | string | Method name |
| `qualified_name` | string | Fully qualified method name |
| `declaring_type` | string | Type this method was declared on |
| `visibility` | string | `public`, `pub_crate`, `pub_super`, `pub_in:path`, or `private` |
| `is_async` | boolean | Method defined with `async fn` |
| `is_unsafe` | boolean | Method defined with `unsafe fn` |
| `is_const` | boolean | Method defined with `const fn` |
| `is_static` | boolean | True when `self_kind = 'none'` |
| `self_kind` | string | `self`, `&self`, `&mut self`, or `none` |
| `parameters` | string | Raw parameter text (e.g., `key: &str, value: T`) |
| `return_type` | string | Return type text (e.g., `Result<T, Error>`) |
| `impl_trait` | string | Trait name if from a trait impl, null for inherent impls |
