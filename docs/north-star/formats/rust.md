---
description: What great Rust format support looks like - declarations for querying Rust structure through the knowledge graph
tags: [rust, format, north-star, vision]
audience: { human: 50, agent: 50 }
purpose: { north-star: 100 }
---

# Rust Format Support: What Great Looks Like

> An agent should be able to understand a Rust codebase's structure — types, traits, implementations, modules, and their relationships — without reading source files, and query it all through the same SQL surface as every other format.

An agent lands in a Rust workspace with 400 source files across 12 crates. It doesn't open any. It scans headlines and sees: a `ConnectionPool` struct with 8 methods across two impl blocks, a `Storage` trait with 4 required methods implemented by 3 types, an enum with 12 variants encoding every error the system can produce, a module tree rooted at `lib.rs` with 6 public submodules. It asks "what implements Storage?" and gets every concrete type across every crate. It asks "show me everything derived from Serialize" and sees 47 structs and enums — the complete serialization surface. It asks "what's unsafe?" and finds 3 functions and 1 trait impl, with spans pointing to exactly where safety boundaries are crossed. The impl blocks were scattered across files. The trait bounds involved lifetimes and associated types. The agent saw clean structure. Rust's type system didn't create complexity — it created queryable information.

---

## Trait Graph and Ownership

- An agent should be able to see every trait a type implements, and how — inherent impl, trait impl, or derive
- An agent should be able to find every type that implements a given trait across the entire workspace
- An agent should be able to see supertrait relationships as a traversable hierarchy
- An agent should be able to distinguish between a type's own methods and methods it gains from trait implementations
- An agent should be able to see which derives are applied to a type and what traits they provide
- An agent should be able to see which types implement `Clone`, `Copy`, or `Drop` — the ownership traits that define how values are managed
- An agent should be able to find types that explicitly implement or opt out of `Send` and `Sync`

```sql
-- "What types implement Storage?"
SELECT * FROM rust_impls WHERE trait_name = 'Storage'

-- "What does Config derive?"
SELECT * FROM rust_types WHERE name = 'Config' AND derives IS NOT NULL

-- "What implements Storage across the workspace?"
SELECT t.name, t.crate, i.trait_name
FROM rust_impls i JOIN rust_types t ON t.name = i.target_type
WHERE i.trait_name = 'Storage'
```

---

## Scattered Implementations

- An agent should be able to see all methods on a type regardless of which file or impl block defines them
- An agent should be able to find every impl block for a given type across the workspace
- An agent should be able to distinguish between inherent impls and trait impls
- An agent should be able to see the complete API surface of a type as one unified view

Rust's impl blocks can appear in any file within the crate. `ConnectionPool` might have its core methods in `pool.rs`, its `Debug` implementation in the same file, its `From<Config>` conversion in `config.rs`, and its `Drop` implementation in `cleanup.rs`. An agent should see one complete type with all its capabilities.

---

## Module Tree and Visibility

- An agent should be able to traverse the module hierarchy from crate root to leaf
- An agent should be able to see what each module exports — its public surface
- An agent should be able to query items by visibility level: `pub`, `pub(crate)`, `pub(super)`, or private
- An agent should be able to follow `mod` declarations to the files they reference
- An agent should be able to see re-exports (`pub use`) and trace them to their origins
- An agent should be able to find everything visible from a given module scope

```
headline  →  "pool.rs | ConnectionPool | connect, execute, disconnect, with_timeout | 280 ln, ~1.4k tok"
structure →  +struct ConnectionPool<C: Connection>              #symbol=ConnectionPool
               +pub async fn connect(config: &Config) -> Result<Self>   #symbol=ConnectionPool.connect
               +pub fn execute<Q: Query>(&self, q: Q) -> Result<Q::Output>  #symbol=ConnectionPool.execute
               -fn validate_connection(&self, conn: &C) -> bool  #symbol=ConnectionPool.validate_connection
             +impl Drop for ConnectionPool<C>                   #symbol=ConnectionPool::Drop
               +fn drop(&mut self)                               #symbol=ConnectionPool.drop
```

---

## Enums and Variants

- An agent should be able to see an enum's complete variant list with data shapes — unit, tuple, or struct variants
- An agent should be able to query variant data types and field names
- An agent should be able to find all enums that serve as error types — by implementing `std::error::Error`, by name convention, or by appearing as the `E` in `Result<T, E>` return types
- An agent should be able to see discriminant values where explicitly set

```sql
-- "What are the variants of ApiError?"
SELECT * FROM rust_types WHERE parent_type = 'ApiError' AND kind = 'enum_variant'
```

---

## Generics and Bounds

- An agent should be able to see type parameters and their bounds on any function, struct, enum, or trait
- An agent should be able to find functions constrained by a specific trait bound
- An agent should be able to see where clauses as structured information, not raw text
- An agent should be able to see associated types and their constraints within trait definitions
- An agent should be able to see lifetime parameters on items and identify which items are lifetime-generic

---

## Safety Boundaries

- An agent should be able to find every `unsafe fn`, `unsafe trait`, and `unsafe impl` in the codebase
- An agent should be able to see unsafe blocks within safe functions with their precise spans
- An agent should be able to query the safety surface area — "how much unsafe exists?" — in one query
- An agent should be able to find extern blocks and FFI declarations as a category

```sql
-- "Where is unsafe used?"
SELECT * FROM rust_functions WHERE is_unsafe = true
UNION ALL
SELECT * FROM rust_impls WHERE is_unsafe = true
```

---

## Async and Concurrency

- An agent should be able to find all async functions and see their return types (the declared type, not the wrapped Future)
- An agent should be able to distinguish async methods from sync methods on the same type

---

## Attributes and Macros (Syntactic Boundary)

- An agent should be able to see all attributes applied to an item — derives, proc-macro attributes, lint controls, and custom attributes — as queryable metadata
- An agent should be able to see `macro_rules!` definitions as indexed symbols with their names and visibility
- An agent should be able to see macro invocations at call sites — what macro was called and where
- An agent should be able to see `#[derive(...)]` attributes and map them to the traits they provide
- An agent should be able to see `#[cfg(...)]` predicates on items and know which items are conditionally compiled
- An agent should be able to see `#[test]`, `#[tokio::main]`, `#[serde(rename)]`, and other proc-macro attributes with their arguments
- An agent should be able to trust that the graph captures syntactically visible structure and clearly marks where macro expansion makes the rest invisible

Macros are Rust's metaprogramming mechanism. Like Ruby's `eval`, the north star is not "capture the expanded output" — it's "capture what has a syntactic footprint, and be honest about what's invisible." An agent sees that `#[derive(Serialize, Deserialize)]` is applied, but not the generated `impl` blocks.

---

## Workspace and Crate Structure

- An agent should be able to see every crate in a workspace with its name, version, and edition
- An agent should be able to see inter-crate dependencies as edges in the graph
- An agent should be able to distinguish between library crates, binary crates, examples, tests, and benchmarks
- An agent should be able to see feature flag definitions and which items are gated behind them
- An agent should be able to query across crate boundaries within a workspace as naturally as querying within a single crate

---

## Dependencies and Imports

- An agent should be able to see what each file imports through `use` declarations
- An agent should be able to distinguish between crate-internal imports and external dependency imports
- An agent should be able to find glob imports (`use foo::*`) and re-exports (`pub use`)
- An agent should be able to trace the import graph between modules
- An agent should be able to find unused imports surfaced as diagnostics

---

## Error Handling

- An agent should be able to find all functions that return `Result` or `Option` and see their error types
- An agent should be able to trace which error types propagate through which functions
- An agent should be able to find error type definitions and see which variants they contain
- An agent should be able to find `From` implementations between error types — the conversion graph that enables `?` propagation

```sql
-- "What functions can produce AuthError?"
SELECT * FROM rust_functions WHERE return_type LIKE '%Result<%AuthError%'
```

---

## Testing

- An agent should be able to find all test functions (`#[test]`) and see which module they live in
- An agent should be able to distinguish unit tests (inline `#[cfg(test)]` modules) from integration tests (`tests/` directory)
- An agent should be able to see which source items are covered by nearby tests — tests in the same module, tests that reference the same symbols
- An agent should be able to find test helper functions and test fixtures

---

## Documentation

- An agent should be able to see documentation comments (`///`, `//!`) on items as structured metadata distinct from inline comments
- An agent should be able to query which public items lack documentation
- An agent should be able to see module-level documentation (`//!`) as part of the module's description

---

## Constants, Statics, and Type Aliases

- An agent should be able to find all constants and statics with their types and values
- An agent should be able to find type aliases and see what they resolve to
- An agent should be able to distinguish `const` from `static` and see mutability on statics
- An agent should be able to find `const fn` functions and see that they are eligible for compile-time evaluation

---

## Integrity

- An agent should be able to find files with parse errors and see what structure was recoverable
- An agent should be able to see compiler warnings and clippy lints surfaced as diagnostics
- An agent should be able to find unresolved imports — `use` paths that point to nothing
- An agent should be able to trust that complex syntax (deeply nested generics, macro-heavy files, edition-specific features) parses correctly

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| See all implementations of a trait across the workspace | Trait impls are Rust's polymorphism — "what can do X?" is the central question |
| See a type's complete API across scattered impl blocks | Rust splits definitions by design — agents need the unified view |
| Traverse the module tree and visibility boundaries | Modules define the public contract — knowing what's exported shapes understanding |
| Query safety boundaries in one pass | Unsafe code is where bugs hide — finding it fast matters |
| See all attributes as queryable metadata | Derives, test markers, serde annotations — attributes carry intent |
| Trace error propagation through Result types | Error handling is what agents investigate first in Rust code |
| Find tests and see what they cover | "Is this tested?" is the immediate follow-up to understanding code |
| Query across crate boundaries | Workspaces are one project — crate walls shouldn't be query walls |
| Trust that honest gaps are marked | Macro-expanded code is invisible — honest gaps beat false completeness |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Show methods from only one impl block | An agent should see a type's complete API from all impl blocks |
| Flatten trait impls into a list | An agent should traverse the trait hierarchy and see supertrait chains |
| Ignore attributes as decoration | An agent should see all attributes — derives, test markers, serde annotations — as queryable metadata |
| Require opening files to find unsafe | An agent should query safety boundaries through SQL |
| Hide conditional compilation | An agent should see `#[cfg]` predicates as properties on items |
| Pretend macros don't exist | An agent should see macro definitions and invocations — and know the expansion is invisible |
| Stop at crate boundaries | An agent should query across a workspace as one connected graph |
| Treat tests as invisible | An agent should find test functions and see which code they cover |
| Ignore error flow | An agent should trace error propagation through `Result` types and `From` conversions |
| Discard doc comments | An agent should see documentation as structured metadata on items |

---

*An agent should be able to understand Rust code the way a Rust developer does — through types, traits, implementations, and ownership — not the way a parser does.*
