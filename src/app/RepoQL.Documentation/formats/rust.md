---
description: "Rust — structs, enums, traits, impls, derives, unsafe audit — rust_types, rust_methods, rust_impls, rust_derives views with query patterns"
tags: ["rust", "format", "traits", "impls", "derives", "methods", "code", "macros", "unsafe", "modules", "generics"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Rust Format

Query Rust structs, enums, traits, functions, methods, impl blocks, derives, imports, modules, macros, and unsafe items with SQL views. Syntactic extraction via tree-sitter — no Rust toolchain required.

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

## View Signatures

```sql
rust_types(qualified_name, name, type_kind, visibility, generics, derives, supertraits, is_unsafe, definition_count, defined_in, structure)
rust_functions(file_uri, function_uri, headline, name, qualified_name, visibility, is_async, is_unsafe, is_const, is_test, generics, parameters, return_type)
rust_methods(file_uri, parent_uri, parent_name, parent_qualified_name, method_uri, headline, name, qualified_name, declaring_type, visibility, is_async, is_unsafe, is_const, is_static, self_kind, parameters, return_type, impl_trait)
rust_impls(type_uri, target_type, target_qualified_name, trait_name, is_unsafe, file_uri)
rust_derives(type_uri, type_name, type_qualified_name, derived_trait)
rust_modules(file_uri, module_uri, name, qualified_name, visibility, is_inline)
rust_unsafe(item_kind, name, qualified_name, file_uri)
rust_imports(file_uri, import_path, alias, is_glob, is_reexport)
rust_macros(file_uri, macro_uri, name, qualified_name, visibility)
rust_macro_expansion(file_uri, macro_name, description, line)
```

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
| Unsafe audit | `SELECT item_kind, name, file_uri FROM rust_unsafe` |
| Dependency usage | `SELECT import_path, COUNT(*) FROM rust_imports GROUP BY 1 ORDER BY 2 DESC` |
| Re-exports | `SELECT file_uri, import_path FROM rust_imports WHERE is_reexport` |
| Macro hotspots | `SELECT file_uri, COUNT(*) FROM rust_macro_expansion GROUP BY 1 ORDER BY 2 DESC` |
| Test functions | `SELECT name, file_uri FROM rust_functions WHERE is_test` |
| Graph completeness | `SELECT file_uri, macro_name, description FROM rust_macro_expansion` |
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
| Expecting structured generics | `generics` is raw text (e.g., `<'a, T: Clone>`) — not parsed into individual parameters |
| Querying `rust_macro_expansion` for definitions | `rust_macro_expansion` is honesty annotations. For definitions, use `rust_macros` |
| Expecting `rust_types.derives` to be an array | It's a comma-separated string. For per-trait querying, use `rust_derives` view |
| Assuming `defined_in` is a string | It's a `LIST` (array). Use `UNNEST(defined_in)`, `list_contains(defined_in, '...')`, or `array_to_string(defined_in, ', ')` |
| Expecting unsafe block detection | `rust_unsafe` only captures unsafe declarations (fn, trait, impl) — not `unsafe { }` blocks within function bodies |
| Using `properties->>'kind'` in WHERE | Use `json_extract_string(properties, '$.kind')` in WHERE/CASE to avoid DuckDB type coercion |
| Joining on `name` across views | Use `qualified_name` for precision — `name` alone is ambiguous across modules |

---

## Views

### rust_types

Rust type declarations (`rs.type`) aggregated by qualified name — structs, enums, traits, unions, and type aliases with derives, generics, and stub deduplication.

#### Quick Reference

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

#### Capsule: RustTypes

**Invariant**
`rust_types` aggregates struct, enum, trait, union, and type alias declarations by qualified name, preferring non-stub definitions over stubs. Exposes one row per unique qualified name with a `type_kind` discriminator.

**Example**
```sql
-- All types with kind and visibility
SELECT qualified_name, type_kind, visibility, derives
FROM rust_types;

-- Traits with supertraits
SELECT name, supertraits
FROM rust_types
WHERE type_kind = 'trait' AND supertraits IS NOT NULL;

-- Type kind distribution
SELECT type_kind, COUNT(*) AS count
FROM rust_types
GROUP BY type_kind;

-- Union types
SELECT name, generics
FROM rust_types
WHERE type_kind = 'union';

-- Enum structure
SELECT name, structure
FROM rust_types
WHERE type_kind = 'enum';
```
//BOUNDARY: `type_kind` is one of: `struct`, `enum`, `trait`, `union`, `type_alias`. All are `rs.type` nodes — there is no `rs.struct` or `rs.enum` node kind. `type_alias` includes `type Alias = Target;` declarations. `union` includes `union Name { ... }` — Rust's unsafe union types. `generics` is raw text (e.g., `<'a, T: Clone + Send>`) — not parsed into individual parameters.

**Depth**
- `qualified_name`: `module::TypeName` format — the preferred key for cross-view joins
- `generics`: Raw text including lifetimes and bounds — not parsed into individual parameters
- `supertraits`: Trait supertrait bounds as text (e.g., `Send + Sync`) — traits only
- `is_unsafe`: True for `unsafe trait` declarations
- `defined_in`: Array of document URIs — use `UNNEST()` or `list_contains()` to query
- `structure`: X-ray structure text — use `read()` for full content
- Also participates in shared `Types` view via `WHERE n.kind LIKE '%.type'`

#### Capsule: RustTypesDeduplication

**Invariant**
The view groups by `qualified_name`, preferring non-stub definitions for all column values. Stub types are created when an impl block references a type not defined in the same file.

**Example**
```sql
-- Types defined across multiple files (stub + real definition)
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

#### Capsule: RustTypesDerives

**Invariant**
The `derives` column is a comma-separated string summary. For per-trait filtering, use the `rust_derives` view instead.

**Example**
```sql
-- Types with Debug (string LIKE approach)
SELECT name
FROM rust_types
WHERE derives LIKE '%Debug%';

-- Better: per-derive querying via rust_derives
SELECT type_name
FROM rust_derives
WHERE derived_trait = 'Debug';
```
//BOUNDARY: `derives` is a display string (e.g., `"Debug, Clone, PartialEq"`). It is NOT an array — `LIKE` works but array functions do not. `rust_derives` has one row per type x trait combination for precise querying.

#### Column Reference

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

---

### rust_methods

Method-level Rust members (`rs.member`) within impl blocks — with parent type, self receiver kind, and trait implementation context.

#### Quick Reference

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

#### Capsule: RustFunctionsAndMethods

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

#### Capsule: RustMethodsSelfKind

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

#### Capsule: RustMethodsTraitContext

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

#### Capsule: RustMethodsFlags

**Invariant**
Boolean flags surface async, unsafe, and const qualifiers on methods.

**Example**
```sql
-- Unsafe methods
SELECT parent_name, name, file_uri
FROM rust_methods
WHERE is_unsafe;

-- Const methods
SELECT parent_name, name
FROM rust_methods
WHERE is_const;
```
//BOUNDARY: These flags are syntactic — they reflect the `async`, `unsafe`, and `const` keywords on the method signature. They do not propagate from callees.

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `file_uri` | string | Document containing this method |
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

---

### rust_impls

Trait implementations (`IMPLEMENTS` edges) — which types implement which traits, with unsafe tracking and source location.

#### Quick Reference

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

#### Capsule: RustTraitImpls

**Invariant**
`rust_impls` shows trait implementations — which types implement which traits. Does NOT include inherent impl blocks.

**Example**
```sql
-- What traits does a type implement?
SELECT trait_name, is_unsafe
FROM rust_impls
WHERE target_type = 'RegexMatcher';

-- Who implements a specific trait?
SELECT target_type, file_uri
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

#### Capsule: RustImplsScope

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

#### Capsule: RustImplsUnsafe

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

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `type_uri` | string | URI of the implementing type node |
| `target_type` | string | Simple name of the implementing type |
| `target_qualified_name` | string | Qualified name of the implementing type |
| `trait_name` | string | Name of the implemented trait (deferred reference) |
| `is_unsafe` | boolean | True for `unsafe impl` declarations |
| `file_uri` | string | Document where the impl block appears |

---

### rust_derives

Derive macro applications (`DERIVES` edges) — one row per type x derived trait combination.

#### Quick Reference

```sql
-- Most commonly derived traits
SELECT derived_trait, COUNT(*) AS count
FROM rust_derives
GROUP BY derived_trait
ORDER BY count DESC;

-- What does a type derive?
SELECT derived_trait FROM rust_derives WHERE type_name = 'Config';

-- Find all Serialize types
SELECT type_name FROM rust_derives WHERE derived_trait = 'Serialize';
```

#### Capsule: RustDerives

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

#### Capsule: RustDerivesVsProperty

**Invariant**
`rust_derives` has one row per derive instance. `rust_types.derives` has the same data as a comma-separated string. Use `rust_derives` for filtering and aggregation. Use `rust_types.derives` for display.

**Example**
```sql
-- Per-trait filtering: use rust_derives
SELECT type_name
FROM rust_derives
WHERE derived_trait = 'Clone';

-- Display: use rust_types.derives
SELECT name, derives
FROM rust_types
WHERE type_kind = 'struct';

-- Derive fingerprinting (trait combination analysis)
SELECT type_name, LIST(derived_trait ORDER BY derived_trait) AS fingerprint
FROM rust_derives
GROUP BY type_name
ORDER BY LEN(fingerprint) DESC;
```
//BOUNDARY: The column is `derived_trait`, NOT `trait_name`. A common mistake is using `trait_name` which does not exist in this view.

**Depth**
- `derived_trait`: The trait name from `#[derive(Trait)]` (e.g., `Debug`, `Clone`, `Serialize`)
- Each `#[derive(Debug, Clone)]` produces two rows — one for `Debug`, one for `Clone`
- Derive fingerprinting reveals architectural patterns: `[Debug]` = internal, `[Clone, Debug, Eq, PartialEq]` = value types
- SeeAlso: `rust_impls` for explicit trait implementations (vs derived)

#### Capsule: RustDerivesAggregation

**Invariant**
The one-row-per-derive structure enables powerful aggregation and cross-referencing.

**Example**
```sql
-- Types that derive both Clone and Copy
SELECT type_name
FROM rust_derives
WHERE derived_trait IN ('Clone', 'Copy')
GROUP BY type_name
HAVING COUNT(DISTINCT derived_trait) = 2;

-- Derive count per type
SELECT type_name, COUNT(*) AS derive_count
FROM rust_derives
GROUP BY type_name
ORDER BY derive_count DESC;

-- Cross-reference: types that derive Serialize but not Deserialize
SELECT d1.type_name
FROM rust_derives d1
LEFT JOIN rust_derives d2
  ON d1.type_name = d2.type_name AND d2.derived_trait = 'Deserialize'
WHERE d1.derived_trait = 'Serialize'
  AND d2.type_name IS NULL;
```

#### Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `type_uri` | string | URI of the type node |
| `type_name` | string | Simple name of the type |
| `type_qualified_name` | string | Qualified name of the type |
| `derived_trait` | string | Name of the derived trait (e.g., `Debug`, `Clone`) |

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

## Capsule: RustModules

**Invariant**
`rust_modules` shows `mod` declarations with visibility and inline status.

**Example**
```sql
-- Module tree
SELECT name, qualified_name, visibility, is_inline
FROM rust_modules;

-- Public modules
SELECT name, file_uri
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
SELECT file_uri, import_path
FROM rust_imports
WHERE is_reexport;

-- Glob imports
SELECT file_uri, import_path
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
SELECT file_uri, macro_name, description, line
FROM rust_macro_expansion;

-- Macro-heavy files
SELECT file_uri, COUNT(*) AS expansion_count
FROM rust_macro_expansion
GROUP BY file_uri
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
SELECT item_kind, name, qualified_name, file_uri
FROM rust_unsafe;

-- Unsafe by category
SELECT item_kind, COUNT(*) AS count
FROM rust_unsafe
GROUP BY item_kind;
```
//BOUNDARY: `item_kind` is one of: `function`, `method`, `trait`, `impl`. This is a union view over `rust_functions`, `rust_methods`, `rust_types`, and `rust_impls` filtered to unsafe items. Does NOT capture `unsafe { }` blocks within function bodies — only unsafe declarations.
