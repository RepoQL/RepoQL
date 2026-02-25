---
description: "rust_types → structs, enums, traits, unions, type aliases with derives and generics. rust_functions → top-level functions. rust_methods → methods with self receivers and trait context. rust_impls → trait implementations. rust_derives → derive macro applications. rust_unsafe → unsafe audit surface. rust_macro_expansion → honesty annotations for macro boundaries."
tags: ["rust", "code", "traits", "impls", "derives", "macros", "unsafe", "modules", "generics"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Rust Format

Query Rust structs, enums, traits, functions, methods, impl blocks, derives, imports, modules, macros, and unsafe items with SQL views. Syntactic extraction via tree-sitter — no Rust toolchain required.

---

## Capsule: RustTypes

**Invariant**
`rust_types` aggregates struct, enum, trait, union, and type alias declarations by qualified name, preferring non-stub definitions over stubs.

**Example**
```sql
-- All types with kind and visibility
SELECT qualified_name, type_kind, visibility, derives
FROM rust_types;

-- Traits with supertraits
SELECT name, supertraits
FROM rust_types
WHERE type_kind = 'trait' AND supertraits IS NOT NULL;

-- Types defined across multiple files (stub + real definition)
SELECT qualified_name, definition_count, defined_in
FROM rust_types
WHERE definition_count > 1;

-- Enum structure
SELECT name, structure
FROM rust_types
WHERE type_kind = 'enum';
```
//BOUNDARY: `type_kind` is one of: `struct`, `enum`, `trait`, `union`, `type_alias`. `derives` is a comma-separated string (e.g., `"Debug, Clone, PartialEq"`). For per-derive querying, use `rust_derives`. The view groups by `qualified_name` — for per-file type nodes, query `node` directly with `kind = 'rs.type'`.

**Depth**
- `qualified_name`: `module::TypeName` format — the preferred key for cross-view joins
- `generics`: Raw text including lifetimes and bounds (e.g., `<'a, T: Clone>`) — not parsed into individual parameters
- `supertraits`: Trait supertrait bounds as text (e.g., `Send + Sync`) — traits only
- `is_unsafe`: True for `unsafe trait` declarations
- `defined_in`: Array of document URIs — use `UNNEST()` or `list_contains()` to query
- `structure`: X-ray structure text — use `read()` for full content
- Stub types are created when an impl block's target type isn't defined in the same file
- Also participates in shared `Types` view via `WHERE n.kind LIKE '%.type'`

---

## Capsule: RustFunctionsAndMethods

**Invariant**
`rust_functions` shows top-level functions (outside impl blocks). `rust_methods` shows methods within impl blocks, with their parent type and trait context.

**Example**
```sql
-- Public API: top-level functions
SELECT name, return_type, is_async
FROM rust_functions
WHERE visibility = 'public';

-- Methods on a type
SELECT name, self_kind, return_type, impl_trait
FROM rust_methods
WHERE parent_name = 'Searcher';

-- Async methods
SELECT parent_name, name, self_kind
FROM rust_methods
WHERE is_async;

-- Trait implementation methods (vs inherent methods)
SELECT parent_name, name, impl_trait
FROM rust_methods
WHERE impl_trait IS NOT NULL;

-- Constructors and associated functions (no self)
SELECT parent_name, name, return_type
FROM rust_methods
WHERE self_kind = 'none';
```
//BOUNDARY: `self_kind` is one of: `self`, `&self`, `&mut self`, `none` (associated function / constructor). `impl_trait` is the trait name when the method comes from a trait impl block (null for inherent impls). Both views participate in the shared `Functions` view.

**Depth**
- `rust_functions`: `name`, `qualified_name`, `visibility`, `is_async`, `is_unsafe`, `is_const`, `is_test`, `generics`, `parameters`, `return_type`
- `rust_methods`: adds `parent_name`, `parent_qualified_name`, `declaring_type`, `self_kind`, `is_static`, `impl_trait`
- `is_static`: True when `self_kind = 'none'` — associated functions, constructors
- `is_test`: True for functions annotated with `#[test]`
- `parameters`: Raw parameter text (e.g., `key: &str, value: T`)
- `generics`: Raw generic text (e.g., `<T: Display>`)

---

## Capsule: RustTraitImpls

**Invariant**
`rust_impls` shows trait implementations — which types implement which traits. Does NOT include inherent impl blocks.

**Example**
```sql
-- What traits does a type implement?
SELECT trait_name, is_unsafe
FROM rust_impls
WHERE target_type = 'RegexMatcher';

-- Who implements a specific trait?
SELECT target_type, document_uri
FROM rust_impls
WHERE trait_name = 'Matcher';

-- Types implementing the most traits
SELECT target_type, COUNT(*) AS trait_count
FROM rust_impls
GROUP BY target_type
ORDER BY trait_count DESC;

-- Unsafe trait implementations
SELECT target_type, trait_name
FROM rust_impls
WHERE is_unsafe;
```
//BOUNDARY: `rust_impls` only shows trait implementations (IMPLEMENTS edges). For inherent impl methods (no trait), query `rust_methods WHERE impl_trait IS NULL`. `trait_name` is a deferred reference string — it may name a trait from an external crate that doesn't exist as a node in the graph.

**Depth**
- `type_uri`: Addressable URI of the implementing type node
- `target_type`: The type name (e.g., `RegexMatcher`)
- `target_qualified_name`: Fully qualified type name
- `trait_name`: The implemented trait name as a string
- `is_unsafe`: True for `unsafe impl Trait for Type`

---

## Capsule: RustDerives

**Invariant**
`rust_derives` has one row per type x derived trait combination. Complementary to the `derives` string on `rust_types`.

**Example**
```sql
-- Most commonly derived traits
SELECT derived_trait, COUNT(*) AS count
FROM rust_derives
GROUP BY derived_trait
ORDER BY count DESC;

-- What does a type derive?
SELECT derived_trait
FROM rust_derives
WHERE type_name = 'Config';

-- Find all types that derive Serialize
SELECT type_name, type_qualified_name
FROM rust_derives
WHERE derived_trait = 'Serialize';

-- Types that derive both Clone and Copy
SELECT type_name
FROM rust_derives
WHERE derived_trait IN ('Clone', 'Copy')
GROUP BY type_name
HAVING COUNT(DISTINCT derived_trait) = 2;
```
//BOUNDARY: `rust_derives` has one row per derive instance — use for filtering and aggregation. `rust_types.derives` has the same data as a comma-separated string — use for display.

---

## Capsule: RustModules

**Invariant**
`rust_modules` shows `mod` declarations with visibility and inline status.

**Example**
```sql
-- Module tree
SELECT name, qualified_name, visibility, is_inline
FROM rust_modules;

-- Public modules
SELECT name, document_uri
FROM rust_modules
WHERE visibility = 'public';
```
//BOUNDARY: `is_inline` is true for `mod name { ... }` (has body in this file). False for `mod name;` (references external file). Module files themselves are separate document nodes.

---

## Capsule: RustImports

**Invariant**
`rust_imports` shows `use` declarations with path, alias, glob, and re-export status.

**Example**
```sql
-- External crate usage
SELECT import_path, COUNT(*) AS usage_count
FROM rust_imports
WHERE import_path NOT LIKE 'crate::%'
  AND import_path NOT LIKE 'super::%'
  AND import_path NOT LIKE 'self::%'
GROUP BY import_path
ORDER BY usage_count DESC;

-- Re-exports (pub use)
SELECT document_uri, import_path
FROM rust_imports
WHERE is_reexport;

-- Glob imports
SELECT document_uri, import_path
FROM rust_imports
WHERE is_glob;

-- Aliased imports
SELECT import_path, alias
FROM rust_imports
WHERE alias IS NOT NULL;
```
//BOUNDARY: `import_path` is the full use path as written in source (e.g., `std::collections::HashMap`). For group uses like `use std::{io, fs}`, each item becomes a separate row. `is_reexport` is true for `pub use` declarations.

---

## Capsule: RustMacros

**Invariant**
`rust_macros` shows `macro_rules!` definitions. `rust_macro_expansion` flags locations where macros affect the graph's completeness.

**Example**
```sql
-- Macro definitions
SELECT name, qualified_name, visibility
FROM rust_macros;

-- Where is the graph incomplete due to macros?
SELECT document_uri, macro_name, description, line
FROM rust_macro_expansion;

-- Macro-heavy files
SELECT document_uri, COUNT(*) AS expansion_count
FROM rust_macro_expansion
GROUP BY document_uri
ORDER BY expansion_count DESC;

-- Which macros cause the most honesty annotations?
SELECT macro_name, COUNT(*) AS count
FROM rust_macro_expansion
GROUP BY macro_name
ORDER BY count DESC;
```
//BOUNDARY: Extraction is syntactic — macro bodies are not expanded. `rust_macro_expansion` annotations mark sites where derive macros, attribute macros, or `macro_rules!` invocations may have generated code invisible to the graph. Each annotation has a `description` explaining what was detected and a `line` for source location.

**Depth**
- `rust_macros`: Definitions only — `macro_rules! name { ... }`
- `rust_macro_expansion`: Honesty annotations — tells you WHERE the graph might be incomplete
- `macro_name`: The macro that was invoked (e.g., `derive`, `serde`, `tokio::main`)
- `description`: Human-readable explanation of what was detected
- Use `snippet()` with the `line` to see the source context

---

## Capsule: RustUnsafe

**Invariant**
`rust_unsafe` unions all unsafe items: functions, methods, traits, and trait implementations.

**Example**
```sql
-- Full unsafe audit
SELECT item_kind, name, qualified_name, document_uri
FROM rust_unsafe;

-- Unsafe by category
SELECT item_kind, COUNT(*) AS count
FROM rust_unsafe
GROUP BY item_kind;
```
//BOUNDARY: `item_kind` is one of: `function`, `method`, `trait`, `impl`. This is a union view over `rust_functions`, `rust_methods`, `rust_types`, and `rust_impls` filtered to unsafe items. Does NOT capture `unsafe { }` blocks within function bodies — only unsafe declarations.

---

## Capsule: RustVisibility

**Invariant**
Rust visibility is normalized to five values, consistent across all Rust views.

**Example**
```sql
-- Visibility distribution
SELECT visibility, COUNT(*) AS count
FROM rust_types
GROUP BY visibility;

-- pub(crate) items
SELECT name, type_kind
FROM rust_types
WHERE visibility = 'pub_crate';

-- Path-restricted visibility
SELECT name, visibility
FROM rust_types
WHERE visibility LIKE 'pub_in:%';
```
//BOUNDARY: Values: `public` (`pub`), `pub_crate` (`pub(crate)`), `pub_super` (`pub(super)`), `pub_in:path` (`pub(in path)` — e.g., `pub_in:crate::outer`), `private` (no modifier). Applies to `rust_types`, `rust_functions`, `rust_methods`, `rust_modules`, `rust_macros`.

---

## Views

```sql
rust_types(qualified_name, name, type_kind, visibility, generics, derives, supertraits, is_unsafe, definition_count, defined_in, structure)
rust_functions(document_uri, function_uri, headline, name, qualified_name, visibility, is_async, is_unsafe, is_const, is_test, generics, parameters, return_type)
rust_methods(document_uri, parent_uri, parent_name, parent_qualified_name, method_uri, headline, name, qualified_name, declaring_type, visibility, is_async, is_unsafe, is_const, is_static, self_kind, parameters, return_type, impl_trait)
rust_impls(type_uri, target_type, target_qualified_name, trait_name, is_unsafe, document_uri)
rust_derives(type_uri, type_name, type_qualified_name, derived_trait)
rust_modules(document_uri, module_uri, name, qualified_name, visibility, is_inline)
rust_unsafe(item_kind, name, qualified_name, document_uri)
rust_imports(document_uri, import_path, alias, is_glob, is_reexport)
rust_macros(document_uri, macro_uri, name, qualified_name, visibility)
rust_macro_expansion(document_uri, macro_name, description, line)
```

---

## Node Kinds

- `rs.type` — Struct, enum, trait, union, or type alias (distinguished by `properties->>'kind'`: `struct`, `enum`, `trait`, `union`, `type_alias`)
- `rs.member` — Method within an impl block
- `rs.function` — Top-level function (outside any impl block)
- `rs.module` — Module declaration
- `rs.macro` — Macro definition (`macro_rules!`)

## Edge Types

- `HAS_PART` — Composition (document -> type/function/module/macro, type -> member)
- `IMPLEMENTS` — Trait implementation (type -> trait name in `target` property)
- `DERIVES` — Derive macro application (type -> derived trait name in `target` property)
- `IMPORTS` — Use declaration (document -> path in `path` property, with `alias`, `is_glob`, `is_pub`)

## Annotation Kinds

- `rs.macro_expansion` — Honesty annotation marking macro expansion sites where the graph may be incomplete

---

## File Extensions

| Extension | Media Type Kind |
|-----------|-----------------|
| `.rs` | `code.rust` |
| `build.rs` | `code.rust.build` |

---

## Common Patterns

| Goal | Query |
|------|-------|
| Find all Rust files | `SELECT uri, headline FROM Files WHERE lang = 'code.rust'` |
| List structs | `SELECT name, derives FROM rust_types WHERE type_kind = 'struct'` |
| List traits | `SELECT name, supertraits FROM rust_types WHERE type_kind = 'trait'` |
| List enums | `SELECT name, structure FROM rust_types WHERE type_kind = 'enum'` |
| Methods on a type | `SELECT name, self_kind, return_type FROM rust_methods WHERE parent_name = 'MyType'` |
| Who implements trait X? | `SELECT target_type FROM rust_impls WHERE trait_name = 'X'` |
| What does type X derive? | `SELECT derived_trait FROM rust_derives WHERE type_name = 'X'` |
| Public API surface | `SELECT name, return_type FROM rust_functions WHERE visibility = 'public'` |
| Async methods | `SELECT parent_name, name FROM rust_methods WHERE is_async` |
| Unsafe audit | `SELECT item_kind, name, document_uri FROM rust_unsafe` |
| Dependency usage | `SELECT import_path, COUNT(*) FROM rust_imports GROUP BY 1 ORDER BY 2 DESC` |
| Re-exports | `SELECT document_uri, import_path FROM rust_imports WHERE is_reexport` |
| Macro hotspots | `SELECT document_uri, COUNT(*) FROM rust_macro_expansion GROUP BY 1 ORDER BY 2 DESC` |
| Test functions | `SELECT name, document_uri FROM rust_functions WHERE is_test` |
| Graph completeness | `SELECT document_uri, macro_name, description FROM rust_macro_expansion` |
| Derive fingerprint | `SELECT type_name, LIST(derived_trait ORDER BY derived_trait) FROM rust_derives GROUP BY 1` |
| Most-implemented traits | `SELECT trait_name, COUNT(*) FROM rust_impls GROUP BY 1 ORDER BY 2 DESC` |
| Trait method surface | `SELECT parent_name, name, impl_trait FROM rust_methods WHERE impl_trait IS NOT NULL` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Looking for `rs.struct` or `rs.enum` node kinds | All are `rs.type` — filter by `properties->>'kind'` or use `rust_types.type_kind` |
| Looking for `rs.method` node kind | Methods are `rs.member` — use `rust_methods` view |
| Expecting inherent impls in `rust_impls` | `rust_impls` only shows trait impls. For inherent methods, use `rust_methods WHERE impl_trait IS NULL` |
| Using `trait_name` in `rust_derives` | The column is `derived_trait`, not `trait_name` |
| Using `file_uri` as a column name | All Rust views use `document_uri` |
| Expecting structured generics | `generics` is raw text (e.g., `<'a, T: Clone>`) — not parsed into individual parameters |
| Querying `rust_macro_expansion` for definitions | `rust_macro_expansion` is honesty annotations. For definitions, use `rust_macros` |
| Expecting `rust_types.derives` to be an array | It's a comma-separated string. For per-trait querying, use `rust_derives` view |
| Assuming `defined_in` is a string | It's a `LIST` (array). Use `UNNEST(defined_in)`, `list_contains(defined_in, '...')`, or `array_to_string(defined_in, ', ')` |
| Expecting unsafe block detection | `rust_unsafe` only captures unsafe declarations (fn, trait, impl) — not `unsafe { }` blocks within function bodies |
| Using `properties->>'kind'` in WHERE | Use `json_extract_string(properties, '$.kind')` in WHERE/CASE to avoid DuckDB type coercion |
| Joining on `name` across views | Use `qualified_name` for precision — `name` alone is ambiguous across modules |
