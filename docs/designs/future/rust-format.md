---
description: Design for Rust format support — extracting structs, enums, traits, impl blocks, functions, modules, macros, and relationships from Rust source via tree-sitter
tags: [format, rust, tree-sitter, design, code]
audience: { human: 45, agent: 55 }
purpose: { design: 85, flow: 15 }
---

# Rust Format — Design

## North Star

An agent should understand a Rust codebase's structure — types, traits, implementations, modules, and their relationships — without reading source files, and query it all through the same SQL surface as every other format.

Impl blocks dissolve into their target types. An agent asks "what methods does ConnectionPool have?" and sees the complete API — inherent methods, trait implementations, derived capabilities — regardless of which files the impl blocks live in. The impl block is not an entity; it's a delivery mechanism.

Doc comments are the API documentation. They always appear in x-ray structure when present in source. An agent reading structure sees not just what exists but what the author said it does.

Derives are both properties and edges. `WHERE derives LIKE '%Serialize%'` for quick checks. `SELECT * FROM rust_derives WHERE derived_trait = 'Serialize'` for relationship traversal. Both cheap to produce, both desire paths.

Macro expansion is invisible — and the agent knows it. The syntactic footprint is captured (attributes, derive lists, macro_rules! definitions, invocations). The generated code is not. This is an honest boundary, not a limitation to apologize for. Agents can query `rust_macro_expansion` to see exactly where the graph is incomplete.

**Informed by:** `docs/north-star/formats/rust.md`
**Research:** `docs/research/rust-parsing-from-dotnet.md`

## Context

Rust files appear in repositories as library and binary crate sources, build scripts, examples, integration tests, and benchmarks. Rust's syntax is structurally regular but has specific challenges: impl blocks detached from type definitions (methods live in `impl Type {}` blocks that can appear in any file within the crate), trait-based polymorphism (impl blocks for traits generate relationship edges), pervasive generics with lifetimes and where clauses, a module system that maps to filesystem layout, and macros that generate code invisibly.

The Ruby format loader established the pattern for using tree-sitter (via TreeSitter.DotNet) in a code format. This design follows that pattern closely. The loader/parser/materializer split, state transfer via `DocumentModel.Metadata`, and SQL view registration are identical.

**Key difference from Ruby:** Rust has impl blocks that dissolve into their target types rather than Ruby's open classes. Ruby's metaprogramming generates methods at parse time (`attr_accessor`); Rust's macros generate code invisibly at compile time. Ruby has mixin-based composition (include/extend/prepend); Rust has trait-based composition (impl Trait for Type). Rust also has a richer type system — generics with lifetimes, associated types, where clauses — that produces more structured metadata per declaration.

## Constraints

| Constraint | Source |
|------------|--------|
| Single writer to DuckDB | Hard constraint — all writes through `DuckDbDataStore` |
| Frozen schema — 5 tables only | Extend via views/macros/UDFs, never new tables |
| Errors never cascade | One malformed Rust file must never stop indexing |
| TreeSitter.DotNet (MIT) | NuGet package with Rust grammar bundled. Cross-platform native binaries for all six RepoQL targets |
| No Rust runtime preferred | Parser runs in-process via native tree-sitter library. Not a hard constraint — research identifies subprocess approaches as viable fallbacks |
| tree-sitter parsers not thread-safe | Each thread needs its own Parser instance |

---

## Design

### Classification

Rust files get provisional media type `text/x-rust` from the naming convention layer (`.rs` extension). The classifier confirms and adds the kind parameter.

| Extension | Kind | Notes |
|-----------|------|-------|
| `.rs` | `code.rust` | Standard Rust source |
| `build.rs` | `code.rust.build` | Build scripts — same syntax, different role |
| `Cargo.toml` | Not handled | TOML format — separate loader (see Extension Points) |

```csharp
SemanticMediaType.Create("text", "x-rust").WithKind("code.rust")
```

### Tree-Sitter Integration

The core complexity is contained behind `RustTreeSitterClient` — a thin wrapper around TreeSitter.DotNet that no other component touches. No tree-sitter types escape this class.

```
RustTreeSitterClient
├── Parse(string sourceCode) → RustParseResult
├── Thread-local Parser instances (tree-sitter is not thread-safe)
└── S-expression queries for symbol extraction
```

**Thread safety:** Tree-sitter parsers are not thread-safe. `RustTreeSitterClient` uses `ThreadLocal<Parser>` to give each thread its own parser instance. The `Language` object is created once and shared — it is immutable and thread-safe.

```csharp
private static readonly Language SharedLanguage = new Language("tree-sitter-rust", "tree_sitter_rust");
private readonly ThreadLocal<Parser> _parsers = new(() => new Parser(SharedLanguage), trackAllValues: true);
```

**Query-based extraction:** Rather than walking the full CST, use tree-sitter's query language to target specific patterns. This is more robust than manual traversal — queries match structurally, not positionally.

```scheme
;; Structs
(struct_item name: (type_identifier) @name
    type_parameters: (type_parameters)? @generics
    body: (field_declaration_list)? @body) @struct

;; Enums
(enum_item name: (type_identifier) @name
    type_parameters: (type_parameters)? @generics
    body: (enum_variant_list) @body) @enum

;; Enum variants
(enum_variant name: (identifier) @name) @variant

;; Traits
(trait_item name: (type_identifier) @name
    type_parameters: (type_parameters)? @generics
    bounds: (trait_bounds)? @supertraits
    body: (declaration_list) @body) @trait

;; Impl blocks (inherent and trait)
(impl_item
    trait: (type_identifier)? @trait_name
    type: (_) @target_type
    body: (declaration_list) @body) @impl

;; Functions (free and method)
(function_item name: (identifier) @name
    parameters: (parameters) @params
    return_type: (_)? @return_type) @function

;; Function signatures (trait method declarations without body)
(function_signature_item name: (identifier) @name
    parameters: (parameters) @params
    return_type: (_)? @return_type) @function_sig

;; Modules
(mod_item name: (identifier) @name
    body: (declaration_list)? @body) @module

;; Use declarations
(use_declaration argument: (_) @path) @use

;; Constants
(const_item name: (identifier) @name
    type: (_) @const_type) @const

;; Statics
(static_item name: (identifier) @name
    type: (_) @static_type) @static

;; Type aliases
(type_item name: (type_identifier) @name
    type: (_) @aliased_type) @type_alias

;; Union definitions
(union_item name: (type_identifier) @name
    body: (field_declaration_list) @body) @union

;; Macro definitions
(macro_definition name: (identifier) @name) @macro_def

;; Macro invocations
(macro_invocation macro: (_) @macro_name) @macro_call

;; Attributes (including derive)
(attribute_item (attribute
    (identifier) @attr_name
    arguments: (token_tree)? @attr_args)) @attribute

;; Visibility modifiers
(visibility_modifier) @visibility

;; Extern blocks
(foreign_mod_item) @extern_block

;; Struct fields
(field_declaration name: (field_identifier) @name
    type: (_) @field_type) @field

;; Associated types in traits
(associated_type name: (type_identifier) @name) @assoc_type
```

**Error recovery:** Tree-sitter always returns a complete tree, even for syntactically broken files. Invalid regions produce `ERROR` nodes while the rest parses normally. The client skips `ERROR` nodes during extraction and logs a diagnostic. This aligns with the "errors never cascade" constraint — partial structure is better than no structure.

### Surface Model

The parser extracts a `RustDocumentSurface` — a pure data model carrying everything needed for materialization. No tree-sitter types escape the parser.

```
RustDocumentSurface
├── Structs[]          — name, visibility, generics, where_clause, derives, attributes, span
│   └── Fields[]       — name, visibility, field_type, span
├── Enums[]            — name, visibility, generics, where_clause, derives, attributes, span
│   └── Variants[]     — name, variant_kind (unit/tuple/struct), fields, discriminant, span
├── Traits[]           — name, visibility, generics, where_clause, supertraits, is_auto, is_unsafe, span
│   ├── Methods[]      — name, visibility, is_async, is_unsafe, is_const, self_kind, params, return_type, has_default, span
│   ├── AssociatedTypes[] — name, bounds, default_type, span
│   └── AssociatedConsts[] — name, const_type, has_default, span
├── ImplBlocks[]       — target_type, trait_name (null for inherent), generics, where_clause, is_unsafe, span
│   ├── Methods[]      — name, visibility, is_async, is_unsafe, is_const, self_kind, params, return_type, span
│   ├── AssociatedTypes[] — name, assigned_type, span
│   └── AssociatedConsts[] — name, const_type, value, span
├── Functions[]        — top-level functions, name, visibility, is_async, is_unsafe, is_const, generics, params, return_type, span
├── Modules[]          — name, visibility, is_inline, span
├── Constants[]        — name, visibility, const_type, span
├── Statics[]          — name, visibility, static_type, is_mutable, span
├── TypeAliases[]      — name, visibility, generics, aliased_type, span
├── Unions[]           — name, visibility, generics, derives, span
│   └── Fields[]       — name, visibility, field_type, span
├── MacroDefs[]        — name, visibility, span
├── MacroInvocations[] — macro_name, span
├── UseDeclarations[]  — path, alias, is_glob, is_pub, span
├── ExternBlocks[]     — abi, span
│   └── ExternFunctions[] — name, params, return_type, span
├── Attributes[]       — name, arguments, target_span (span of item they decorate)
└── Stats              — struct_count, enum_count, trait_count, impl_count, function_count, line_count
```

### Impl Block Resolution

This is the key Rust-specific complexity. Impl blocks are detached from type definitions — `impl Foo {}` can appear anywhere in the crate, even in a different file than where `Foo` is defined.

**Design decision:** Impl blocks dissolve into their target types. Methods from `impl Foo {}` parent to the Foo node via HAS_PART edges. The impl block itself does not become a node in the graph.

**When the target type is in the same file:** Methods parent directly to the `rs.type` node for the target type. This covers the common case — most inherent impls are in the same file as the type definition.

**When the target type is in another file:** A stub `rs.type` node is created for the target type in the file containing the impl block. Methods parent to this stub. This follows Ruby's open class pattern exactly — each file that defines or extends a type gets its own `rs.type` node. The `rust_types` view aggregates across files.

The stub node carries `is_stub: true` to distinguish it from the defining occurrence. Its `kind` prop matches the defining type (or defaults to `"struct"` if unknown at parse time — resolved during multi-file analysis). The node kind is still `rs.type`, so it participates in the shared `Types` view and the `rust_types` aggregation view automatically.

**Trait impls:** Generate IMPLEMENTS edges from the stub or local `rs.type` node to the trait. Methods parent to the type node as with inherent impls.

```
File: pool.rs
  struct ConnectionPool { ... }    →  rs.type node (kind=struct)
  impl ConnectionPool { ... }      →  methods parent to ConnectionPool via HAS_PART
  impl Drop for ConnectionPool     →  IMPLEMENTS edge (ConnectionPool → Drop)
                                      + drop() method parents to ConnectionPool

File: config.rs (no struct definition — only impl)
  impl From<Config> for ConnectionPool  →  stub rs.type node (kind=struct, is_stub=true)
                                           + IMPLEMENTS edge (stub → From)
                                           + from() method parents to stub via HAS_PART
```

This ensures uniform graph structure: methods are always two HAS_PART hops from the document (document → type → method). Views never need to handle a special document-parented case.

**Resolution during multi-file analysis:** Stub `rs.type` nodes can be resolved to their defining counterparts by matching `qualified_name`. The `rust_types` view aggregates by `qualified_name` regardless, so stubs fold naturally into the complete picture.

### Visibility Tracking

Rust visibility is explicit per item (unlike Ruby's contextual visibility). Each declaration can have one of:

| Rust syntax | `accessibility` value | Meaning |
|-------------|----------------------|---------|
| `pub` | `"public"` | Visible everywhere |
| `pub(crate)` | `"pub_crate"` | Visible within the crate |
| `pub(super)` | `"pub_super"` | Visible to the parent module |
| `pub(in path)` | `"pub_in:{path}"` | Visible within a specific path |
| *(absent)* | `"private"` | Visible within the current module |

The property is named `accessibility` (not `visibility`) to match the shared Types and Functions view contract.

The tree-sitter query for visibility modifiers:

```scheme
;; pub
(visibility_modifier) @vis

;; pub(crate), pub(super), pub(in path)
(visibility_modifier
    (crate) @pub_crate)
(visibility_modifier
    (super) @pub_super)
(visibility_modifier
    path: (scoped_identifier) @pub_in_path)
```

Each struct field, function, module, constant, static, trait, and type alias carries its own visibility. Methods within impl blocks inherit the impl block's visibility context — methods in an inherent impl of a private type are effectively private even if marked `pub`.

### Macros & Attributes

The syntactic boundary: patterns with a syntactic footprint are extracted. Macro expansion is invisible — there is no way to see what `#[derive(Serialize)]` generates without running the Rust compiler. When unextractable macro-generated structure exists, attributes on the item document what was invoked.

**`macro_rules!` definitions:** Captured as `rs.macro` nodes with name and visibility. The macro body is stored in the artifact but not structurally parsed (macro DSLs are custom syntax).

**Macro invocations:** Captured as `rs.macro_expansion` annotations on the nearest containing item, with the macro name in `rule_id` and a description in `message`. Top-level invocations (e.g., `lazy_static! { ... }`) are annotations on the document.

**`#[derive(...)]` attributes:** Captured both as a `derives` property on the type (comma-separated string for easy access) and as DERIVES edges for relationship traversal. `#[derive(Debug, Clone, Serialize)]` on `struct Config` produces:
- `Config` node with `derives: "Debug, Clone, Serialize"` property
- Three DERIVES edges: Config → Debug, Config → Clone, Config → Serialize
- An `rs.macro_expansion` annotation: "derive macros invoked: Debug, Clone, Serialize — generated impl blocks not captured"

**Other attributes:** Captured as properties on the decorated item. Key attributes receive structured extraction:

| Attribute | Extraction |
|-----------|-----------|
| `#[derive(...)]` | `derives` prop + DERIVES edges + `rs.macro_expansion` annotation |
| `#[test]` | `is_test: true` prop on function |
| `#[cfg(...)]` | `cfg` prop with predicate text |
| `#[allow(...)]` / `#[deny(...)]` | `lint_attrs` prop |
| `#[inline]` / `#[inline(always)]` | `is_inline` prop |
| `#[must_use]` | `must_use` prop |
| `#[deprecated]` | `is_deprecated` prop |
| `#[doc = "..."]` | Mapped to node headline/structure |
| Proc-macro attributes (`#[tokio::main]`, `#[async_trait]`, etc.) | `attributes` prop + `rs.macro_expansion` annotation |
| All others | `attributes` prop (JSON array of name + args) |

**Honesty contract:** Macro-expanded code is invisible. Agents see the derive list and attribute invocations — not the generated impl blocks. The `rs.macro_expansion` annotations provide a structured query surface for agents to discover exactly where the graph is incomplete. The `rust_macro_expansion` view surfaces these (see SQL Views).

### Graph Materialization

State transfer via `RustDocumentState` in `DocumentModel.Metadata`, following the Ruby/PHP pattern.

**Nodes (5 Rust-specific kinds):**

| Kind | What | Key Props |
|------|------|-----------|
| `rs.type` | Struct, enum, trait, union, type alias | `name`, `qualified_name`, `kind` (struct/enum/trait/union/type_alias), `accessibility`, `generics`, `where_clause`, `derives`, `extends` (supertraits), `implements` (JSON array), `is_auto`, `is_unsafe`, `is_stub`, `fields`, `variants`, `associated_types`, `associated_consts` |
| `rs.member` | Methods only | `name`, `qualified_name`, `kind` ("method"), `declaring_type`, `accessibility`, `is_async`, `is_unsafe`, `is_const`, `is_static`, `self_kind`, `parameters`, `return_type`, `impl_trait` |
| `rs.function` | Top-level (free) function | `name`, `qualified_name`, `kind` ("function"), `accessibility`, `is_async`, `is_unsafe`, `is_const`, `is_static`, `generics`, `parameters`, `return_type`, `is_test` |
| `rs.macro` | `macro_rules!` definition | `name`, `qualified_name`, `accessibility` |
| `rs.module` | Module declaration | `name`, `qualified_name`, `accessibility`, `is_inline` |

**`is_static` semantics:** For `rs.member`: `true` when the method has no `self` parameter (associated function, e.g., `Type::new()`); `false` when it has `self`, `&self`, or `&mut self`. For `rs.function`: always `true` (free functions are not bound to an instance). This ensures the shared `Functions` view projects a meaningful value.

**Not nodes — structured properties on `rs.type`:**

| Property | JSON shape | Why not nodes |
|----------|-----------|---------------|
| `fields` | `[{name, type, accessibility, doc}]` | Agents see fields in structure, never query them independently. 15 fields as nodes = 15 nodes + 15 edges for information already in the structure |
| `variants` | `[{name, variant_kind, fields, doc}]` | Same — agents read structure to see variants, don't search for them |
| `associated_types` | `[{name, bounds, default_type}]` | Part of the trait contract, visible in structure |
| `associated_consts` | `[{name, const_type, has_default}]` | Part of the trait contract, visible in structure |

**Not nodes — visible in structure only:**

Constants, statics, and extern functions appear in x-ray structure and headlines but do not produce graph nodes. Agents see them when reading structure; they don't need independent queryability.

**Shared view participation:**

The shared views have specific property name contracts that Rust nodes must match:

- **`Types` view** (`WHERE n.kind LIKE '%.type'`): `rs.type` matches automatically. Projects `properties->>'kind'` as `type_kind`, `properties->>'accessibility'`, `properties->>'extends'`, `properties->'implements'`. Rust stores the type discriminator in `kind` (not `type_kind`) matching how Ruby stores `"class"` / `"module"` in `kind`.
- **`Functions` view** (hardcoded kind list + `$.kind IN ('method', 'constructor', 'function')`): `rs.member` and `rs.function` must be added to the kind list. Rust methods use `kind: "method"`, free functions use `kind: "function"`. Projects `$.accessibility`, `$.is_static`, `$.declaring_type`, `$.parameters`, `$.return_type`.

Property name mapping from Rust terminology:

| Rust concept | Property name | Why |
|-------------|---------------|-----|
| Type discriminator (struct/enum/trait/...) | `kind` | Shared Types view reads `properties->>'kind'` as `type_kind` |
| Visibility (`pub`, `pub(crate)`, etc.) | `accessibility` | Shared views project `accessibility` |
| Trait supertraits | `extends` | Shared Types view projects `extends` |
| Trait implementations | `implements` | Shared Types view projects `implements` (JSON array) |

**Edges:**

| Type | From | To | Props |
|------|------|----|-------|
| `HAS_PART` | document / type / module | child nodes | `ordinal` (source order) |
| `IMPLEMENTS` | type | trait | `target` (trait name), `is_unsafe` |
| `EXTENDS` | trait | supertrait | `target` (trait name) |
| `DERIVES` | type | trait | `target` (trait name) |
| `IMPORTS` | document | symbol path | `path`, `alias`, `is_glob`, `is_pub` |

**Reference edges** (IMPLEMENTS, EXTENDS, DERIVES, IMPORTS): `IsComposition = false`, `DstId = null`, target name in `Props["target"]`. These are deferred references — resolved during multi-file analysis if both ends exist in the graph. This is the standard pattern across all RepoQL format loaders.

**Composition edges** (HAS_PART): `IsComposition = true`, `Ordinal` tracks source order. These form the containment tree: document → type/module/function → member.

**Spans:** 1-based lines, 0-based bytes. Created via `DocumentModel.LineMap.GetSpan(startByte, endByte)`, same as Ruby/PHP.

### X-Ray Summaries

**Headline:** Built in C# (no Liquid templates — following Ruby/PHP convention).

```
{filename} | {primary_declaration} | {key_members} | {line_count} ln, ~{token_count} tok
```

Examples:

```
pool.rs | struct ConnectionPool<C: Connection> | connect, execute, disconnect, with_timeout | 280 ln, ~1.4k tok
error.rs | enum ApiError | NotFound, Unauthorized, Internal, RateLimit, +8 | 120 ln, ~580 tok
storage.rs | trait Storage | get, put, delete, list | 65 ln, ~310 tok
lib.rs | mod pool, mod error, mod storage, mod config | 24 ln, ~110 tok
```

**Structure:** Indented outline with visibility symbols and Rust-specific annotations.

```
/// A pool of reusable database connections.
+struct ConnectionPool<C: Connection>           #symbol=ConnectionPool
  derives: Debug, Clone
  /// The active connections.
  +pool: Vec<C>                                 #symbol=ConnectionPool.pool
  /// Configuration used to create new connections.
  +config: Config                               #symbol=ConnectionPool.config
  -idle_count: AtomicUsize                      #symbol=ConnectionPool.idle_count
  /// Connect to the database using the given configuration.
  +pub async fn connect(config: &Config) -> Result<Self>  #symbol=ConnectionPool.connect
  /// Execute a query against the pool.
  +pub fn execute<Q: Query>(&self, q: Q) -> Result<Q::Output>  #symbol=ConnectionPool.execute
  -fn validate_connection(&self, conn: &C) -> bool  #symbol=ConnectionPool.validate_connection
  impl Drop                                     #symbol=ConnectionPool::Drop
    +fn drop(&mut self)                         #symbol=ConnectionPool.drop
  impl From<Config>                             #symbol=ConnectionPool::From
    /// Create a pool from a Config with default settings.
    +fn from(config: Config) -> Self            #symbol=ConnectionPool.from
```

Doc comments (`///`) are always included in the structure when present in source. They are the API documentation — omitting them loses the most valuable information a structure representation carries. Items without doc comments in source have no comment line in structure.

Visibility symbols: `+` public, `~` pub(crate), `#` pub(super), `-` private. Trait impl sections are grouped under `impl TraitName` headers. The `#symbol=` anchors enable `read("file:///pool.rs#symbol=ConnectionPool.connect")`.

### SQL Views

Embedded resource `Schema/rust_views.sql`, registered via `IFormatSchemaProvider`.

```sql
-- rust_types: structs, enums, traits, unions, type aliases
-- Aggregates across files: a type with impls in 3 files appears once,
-- with definition_count and defined_in showing distribution.
-- Stub nodes (from cross-file impls) fold naturally into aggregation.
CREATE OR REPLACE VIEW rust_types AS
SELECT
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'name' AS name,
    n.properties->>'kind' AS type_kind,
    n.properties->>'accessibility' AS visibility,
    n.properties->>'generics' AS generics,
    n.properties->>'derives' AS derives,
    n.properties->>'extends' AS supertraits,
    COALESCE(n.properties->>'is_unsafe', 'false') = 'true' AS is_unsafe,
    COUNT(DISTINCT doc.uri) AS definition_count,
    LIST(DISTINCT doc.uri) AS defined_in,
    MAX(n.structure) AS structure
FROM node n
JOIN edge e ON e.destination_node_id = n.id
    AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE n.kind = 'rs.type'
GROUP BY
    n.properties->>'qualified_name',
    n.properties->>'name',
    n.properties->>'kind',
    n.properties->>'accessibility',
    n.properties->>'generics',
    n.properties->>'derives',
    n.properties->>'extends',
    COALESCE(n.properties->>'is_unsafe', 'false');

-- rust_functions: free functions with full signature detail
CREATE OR REPLACE VIEW rust_functions AS
SELECT
    doc.uri AS document_uri,
    f.uri AS function_uri,
    f.headline,
    f.properties->>'name' AS name,
    f.properties->>'qualified_name' AS qualified_name,
    f.properties->>'accessibility' AS visibility,
    COALESCE(f.properties->>'is_async', 'false') = 'true' AS is_async,
    COALESCE(f.properties->>'is_unsafe', 'false') = 'true' AS is_unsafe,
    COALESCE(f.properties->>'is_const', 'false') = 'true' AS is_const,
    COALESCE(f.properties->>'is_test', 'false') = 'true' AS is_test,
    f.properties->>'generics' AS generics,
    f.properties->>'parameters' AS parameters,
    f.properties->>'return_type' AS return_type
FROM node f
JOIN edge fe ON fe.destination_node_id = f.id
    AND fe.type = 'HAS_PART' AND fe.is_composition = TRUE
JOIN node doc ON doc.id = fe.source_node_id AND doc.kind = 'document'
WHERE f.kind = 'rs.function';

-- rust_methods: methods with their declaring type and signature detail
CREATE OR REPLACE VIEW rust_methods AS
SELECT
    doc.uri AS document_uri,
    parent.uri AS parent_uri,
    parent.properties->>'name' AS parent_name,
    parent.properties->>'qualified_name' AS parent_qualified_name,
    m.uri AS method_uri,
    m.headline,
    m.properties->>'name' AS name,
    m.properties->>'declaring_type' AS declaring_type,
    m.properties->>'accessibility' AS visibility,
    COALESCE(m.properties->>'is_async', 'false') = 'true' AS is_async,
    COALESCE(m.properties->>'is_unsafe', 'false') = 'true' AS is_unsafe,
    COALESCE(m.properties->>'is_const', 'false') = 'true' AS is_const,
    COALESCE(m.properties->>'is_static', 'false') = 'true' AS is_static,
    m.properties->>'self_kind' AS self_kind,
    m.properties->>'parameters' AS parameters,
    m.properties->>'return_type' AS return_type,
    m.properties->>'impl_trait' AS impl_trait
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node parent ON parent.id = me.source_node_id
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'rs.member';

-- rust_impls: trait implementations with target type and trait name
CREATE OR REPLACE VIEW rust_impls AS
SELECT
    src.uri AS type_uri,
    src.properties->>'name' AS target_type,
    src.properties->>'qualified_name' AS target_qualified_name,
    e.properties->>'target' AS trait_name,
    COALESCE(e.properties->>'is_unsafe', 'false') = 'true' AS is_unsafe,
    doc.uri AS document_uri
FROM edge e
JOIN node src ON src.id = e.source_node_id
JOIN edge de ON de.destination_node_id = src.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE e.type = 'IMPLEMENTS' AND src.kind = 'rs.type';

-- rust_derives: derive macro relationships per type
CREATE OR REPLACE VIEW rust_derives AS
SELECT
    src.uri AS type_uri,
    src.properties->>'name' AS type_name,
    src.properties->>'qualified_name' AS type_qualified_name,
    e.properties->>'target' AS derived_trait
FROM edge e
JOIN node src ON src.id = e.source_node_id
WHERE e.type = 'DERIVES' AND src.kind = 'rs.type';

-- rust_macros: macro_rules! definitions
CREATE OR REPLACE VIEW rust_macros AS
SELECT
    doc.uri AS document_uri,
    m.uri AS macro_uri,
    m.properties->>'name' AS name,
    m.properties->>'qualified_name' AS qualified_name,
    m.properties->>'accessibility' AS visibility
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node doc ON doc.id = me.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'rs.macro';

-- rust_imports: use declarations with alias and glob tracking
CREATE OR REPLACE VIEW rust_imports AS
SELECT
    doc.uri AS document_uri,
    e.properties->>'path' AS import_path,
    e.properties->>'alias' AS alias,
    COALESCE(e.properties->>'is_glob', 'false') = 'true' AS is_glob,
    COALESCE(e.properties->>'is_pub', 'false') = 'true' AS is_reexport
FROM edge e
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE e.type = 'IMPORTS';

-- rust_modules: module declarations
CREATE OR REPLACE VIEW rust_modules AS
SELECT
    doc.uri AS document_uri,
    m.uri AS module_uri,
    m.properties->>'name' AS name,
    m.properties->>'qualified_name' AS qualified_name,
    m.properties->>'accessibility' AS visibility,
    COALESCE(m.properties->>'is_inline', 'false') = 'true' AS is_inline
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node doc ON doc.id = me.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'rs.module';

-- rust_unsafe: everything marked unsafe — functions, methods, traits, trait impls
CREATE OR REPLACE VIEW rust_unsafe AS
SELECT 'function' AS item_kind, name, qualified_name, document_uri
FROM rust_functions WHERE is_unsafe
UNION ALL
SELECT 'method' AS item_kind, name, declaring_type || '.' || name AS qualified_name, document_uri
FROM rust_methods WHERE is_unsafe
UNION ALL
SELECT 'trait' AS item_kind, name, qualified_name, defined_in[1] AS document_uri
FROM rust_types WHERE is_unsafe AND type_kind = 'trait'
UNION ALL
SELECT 'impl' AS item_kind, target_type || ' → ' || trait_name AS name,
    target_qualified_name AS qualified_name, document_uri
FROM rust_impls WHERE is_unsafe;

-- rust_macro_expansion: honesty annotations about invisible macro-generated code
CREATE OR REPLACE VIEW rust_macro_expansion AS
SELECT
    doc.uri AS document_uri,
    a.rule_id AS macro_name,
    a.message AS description,
    s.start_line AS line
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
LEFT JOIN span s ON s.id = a.target_span_id
WHERE a.kind = 'rs.macro_expansion';
```

### Error Handling

| Failure | Behavior |
|---------|----------|
| Tree-sitter parse produces ERROR nodes | Skip error regions, extract surrounding structure, emit diagnostic annotation |
| File can't be read (encoding, I/O) | `PipelineResult.Error` with diagnostic |
| X-ray summary build fails | Log warning, continue with null headline/structure |
| Visibility modifier unrecognized | Default `accessibility` to `"private"` — safe assumption for Rust |
| Impl block target type not in same file | Create stub `rs.type` node with `is_stub: true` |
| Attribute parsing fails | Store raw attribute text in `attributes` property |
| Tree-sitter native library missing | Startup failure with clear diagnostic pointing to NuGet package |

Each extraction phase (structs, enums, traits, impls, functions, modules) is independently try/caught. A malformed struct definition never prevents function extraction elsewhere in the file.

---

## Cross-Cutting Concerns

**URI addressing:** Rust files use `file:///path#symbol=TypeName.method_name` for symbol navigation. Trait impl methods use `TypeName::TraitName.method_name` convention (e.g., `ConnectionPool::Drop.drop`). The `#symbol=` fragment resolves through node name matching.

**Scattered implementations:** Each file that contains impl blocks for a type creates its own `rs.type` node — either the defining node (when the struct/enum/trait definition is present) or a stub node (when only impl blocks exist). Methods always parent to a type node, ensuring uniform two-hop graph structure. The `rust_types` view aggregates by `qualified_name` to show the "one complete picture" the north-star requires — all methods across all impl blocks, all trait implementations, all files. Stubs fold naturally into the aggregation.

**Deferred references:** Reference edges (IMPLEMENTS, EXTENDS, DERIVES, IMPORTS) are created with `DstId = null` and target name in `Props["target"]`. This is the standard pattern across all RepoQL format loaders. Resolution happens in the multi-file analysis phase, not at parse time. Pre-resolution, queries like "what implements Storage?" match on `Props->>'target'` string comparison, which works for same-name matches.

**Search integration:** `Artifact.Text` contains the source code and participates in semantic search. Node headlines and structure text make types and functions discoverable via explore.

**Module-to-file resolution:** `mod config;` declarations reference files (`config.rs` or `config/mod.rs`), but resolving this mapping requires knowledge of the crate root and directory structure. The parser captures the `mod` declaration as an `rs.module` node with `is_inline: false`. Multi-file analysis can resolve the file path using filesystem conventions — same pattern as Ruby's `require_relative`.

**Macro honesty surface:** When the parser encounters derive macros, proc-macro attributes, or macro invocations, it emits `rs.macro_expansion` annotations. The `rust_macro_expansion` view surfaces these, enabling agents to query "where in this codebase is the graph incomplete due to macro expansion?" This parallels Ruby's `ruby_metaprogramming` annotations and view.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| TreeSitter.DotNet | rust-analyzer (LSP) | rust-analyzer needs 1–2.5 GB RAM per project, conflicts with "runs on a developer laptop." Zero-dependency in-process parsing vs. heavy persistent subprocess |
| TreeSitter.DotNet | syn (Rust binary) | syn has no error tolerance — returns nothing on any syntax error. Conflicts with "errors never cascade." Also requires distributing cross-platform Rust binaries |
| TreeSitter.DotNet | ANTLR4 | ANTLR Rust grammar is 3+ years behind current Rust (targets v1.60). tree-sitter-rust grammar is actively maintained by the official tree-sitter org |
| TreeSitter.DotNet | ra_ap_syntax | Would require building a Rust bridge binary with complex cross-platform distribution. Higher ceiling (error-tolerant, lossless CST) but much higher integration cost. Viable future upgrade |
| Stub `rs.type` nodes for cross-file impls | Methods parented to document | Uniform two-hop graph structure (document → type → method). Views work without special cases. Ruby's open class pattern proven. Stubs fold into aggregation views |
| `rs.` prefix node kinds | Separate `rust.struct` / `rust.enum` kinds per type | Matches cross-format convention (`csharp.type`, `php.type`, `rb.type`). `kind` prop distinguishes. One node kind, queried with filters. Shared `Types` view works automatically |
| Fields/variants/associated items as properties | Fields/variants as nodes | Agents see these in structure, never query them independently. A struct with 15 fields as nodes = 15 nodes + 15 HAS_PART edges for data already in the structure. JSON properties on the type node are cheaper and match how agents actually use the information |
| Constants/statics visible in structure only | Constants/statics as nodes | Agents rarely search for constants by name. They see them in structure when reading a file. Nodes would add graph weight for things agents encounter incidentally, not seek out |
| Derives as both props and edges | Props only or edges only | Props give easy per-type access (`WHERE derives LIKE '%Serialize%'`). Edges enable relationship traversal (`SELECT * FROM rust_derives WHERE derived_trait = 'Serialize'`). Both are cheap to produce |
| Normalized `accessibility` values | Raw visibility modifier text | `"pub_crate"` is queryable; `"pub(crate)"` requires escaping in SQL. Property named `accessibility` to match shared view contract |
| `kind` for type discriminator | `type_kind` as property name | Shared Types view reads `properties->>'kind'` as `type_kind`. Ruby stores `kind`. Same property name, shared view works without changes |
| Syntactic macro boundary | Attempting macro expansion | Macro expansion requires the Rust compiler. No syntax-only parser can do it. Honest about what's invisible — `rs.macro_expansion` annotations let agents query the gaps |
| Query-based extraction | Full CST traversal | Queries are declarative, structural, and more robust to grammar evolution. Ruby loader validates the pattern |

## Alternatives Considered

**rust-analyzer via LSP:** Compiler-grade semantic analysis — type inference, macro expansion, cross-file name resolution. Rejected for v1: 1–2.5 GB RAM per project conflicts with the laptop constraint, requires Rust toolchain, adds a persistent subprocess integration pattern unlike any existing loader. The right tool for a "Rust deep analysis" import — not the right tool for per-file structural indexing.

**syn via Rust binary:** Complete Rust AST, fast parsing. Rejected: no error tolerance — a single syntax error returns nothing. Also requires building and distributing cross-platform Rust binaries, creating a new integration pattern.

**ANTLR4:** Natural choice given PHP precedent. Rejected: the community grammar targets Rust v1.60 (April 2022), missing 25+ releases of language evolution. tree-sitter-rust is maintained by a large community and used by every major editor.

**ra_ap_syntax standalone:** Error-tolerant, lossless CST from rust-analyzer's parser. Rejected for v1: same distribution complexity as syn (custom Rust binary for each platform), unstable API with frequent breaking changes. Worth considering as a future upgrade path if tree-sitter's error recovery or coverage proves insufficient.

**cargo metadata + rustdoc JSON:** Supplementary data sources providing dependency graphs, workspace structure, and public API documentation. Not syntax parsers — orthogonal to tree-sitter. Cargo metadata is a viable enrichment source when the Rust toolchain is available (see Extension Points). Rustdoc JSON is nightly-only and requires compilable projects — too restrictive for general indexing.

**Cross-file methods parented to document:** Original design had methods from cross-file impl blocks parenting directly to the document node with a `declaring_type` property. Rejected: creates a non-uniform graph structure where the `rust_methods` view (two-hop join) silently drops document-parented methods. Stub `rs.type` nodes follow the proven Ruby pattern and keep all views working uniformly.

## Risks

| Risk | Mitigation |
|------|------------|
| TreeSitter.DotNet single maintainer (10 GitHub stars) | Grammar source is official tree-sitter-rust (1,100+ downstream dependents). If the NuGet wrapper is abandoned, the grammar and native libraries can be packaged independently. Risk shared with Ruby loader — already accepted |
| tree-sitter-rust grammar lags behind Rust nightly | Grammar covers through Rust 2024 edition features. New syntax additions are typically minor and additive. Monitor tree-sitter-rust releases |
| Macro expansion invisible | Honest boundary: attributes and derive lists captured, expansion not. `rs.macro_expansion` annotations and `rust_macro_expansion` view let agents query the gaps |
| Cross-file impl resolution incomplete at parse time | Stub `rs.type` nodes created per-file. Multi-file analysis resolves stubs to defining nodes by matching `qualified_name`. Same architecture as Ruby open class aggregation |
| Complex generics produce noisy signatures | Generics stored as structured text. SQL views can filter/simplify. X-ray structure shows simplified signatures |
| Thread-safety bugs in `ThreadLocal<Parser>` | Proven pattern from Ruby loader. Unit test concurrent parsing. Tree-sitter documentation is clear on the constraint |
| Native library loading fails on some platform | Test all six RIDs in CI. Fallback: heuristic parser for platforms where tree-sitter fails. Risk shared with Ruby loader |
| Edition detection gaps | tree-sitter-rust targets the latest edition. Older edition syntax (edition 2015 patterns) may parse differently. Monitor for edge cases during testing |

## Extension Points

- **Cargo metadata enrichment:** When `cargo` is available, import workspace structure (crate names, versions, editions), dependency graph (DEPENDS_ON edges between crates), feature flag definitions (as annotations), and target types (lib/bin/example/test/bench)
- **ra_ap_syntax upgrade path:** Replace tree-sitter with ra_ap_syntax P/Invoke if coverage gaps are found. Surface model and materialization unchanged — only the parser changes
- **Test module detection:** `#[cfg(test)] mod tests` modules and `#[test]` functions get test-specific annotations and views. `rust_tests` view lists test functions with the module they test
- **Unsafe audit view:** `rust_unsafe` view (designed above) enables safety surface queries. Extension: annotate unsafe blocks within safe functions with precise spans
- **Workspace-level views:** When multiple crates are indexed, `rust_workspace` view shows crate graph, dependency relationships, and cross-crate trait implementation queries
- **Doc comment extraction:** `///` and `//!` comments captured as structured metadata on nodes, distinct from inline comments. `rust_undocumented` view finds public items without doc comments
- **Cargo.toml parsing:** Separate TOML loader extracts crate metadata, dependencies, features. Links to Rust nodes via crate-name matching
- **Feature flag conditional compilation:** `#[cfg(feature = "...")]` predicates stored as properties enable "what's behind this feature flag?" queries

---

## Project Structure

```
src/Formats/RepoQL.Formats.Rust/
    RustLoader.cs                              # IFormatLoader + IFormatMaterializer + IFormatSchemaProvider
    RustClassifier.cs                          # IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
    RustParser.cs                              # IAsyncPipeline<IClassifiedArtifact, Records?>
    RustDocumentState.cs                       # State transfer between Load and Materialize
    RustConstants.cs                           # Node kinds, edge types, property keys, media types
    Surface/
        RustDocumentSurface.cs                 # Root surface model
        RustStructInfo.cs                      # Struct data (with generics, derives)
        RustEnumInfo.cs                        # Enum data (with variants)
        RustEnumVariantInfo.cs                 # Variant data (unit/tuple/struct)
        RustTraitInfo.cs                       # Trait data (with supertraits, is_auto, is_unsafe)
        RustImplBlockInfo.cs                   # Impl block data (target_type, trait_name, is_unsafe)
        RustMethodInfo.cs                      # Method data (with self_kind, is_async, is_unsafe)
        RustFieldInfo.cs                       # Field data (name, visibility, field_type)
        RustFunctionInfo.cs                    # Free function data
        RustModuleInfo.cs                      # Module data (is_inline)
        RustConstantInfo.cs                    # Constant data (for surface model — not materialized as node)
        RustStaticInfo.cs                      # Static data (for surface model — not materialized as node)
        RustTypeAliasInfo.cs                   # Type alias data
        RustUnionInfo.cs                       # Union data
        RustMacroDefInfo.cs                    # macro_rules! definition data
        RustMacroInvocationInfo.cs             # Macro invocation data
        RustUseDeclarationInfo.cs              # Use declaration data (path, alias, is_glob, is_pub)
        RustAttributeInfo.cs                   # Attribute data (name, arguments)
        RustExternBlockInfo.cs                 # Extern block data (abi, for surface model)
        RustByteRange.cs                       # Byte range for span creation
        RustParseStats.cs                      # Parse statistics
    TreeSitter/
        RustTreeSitterClient.cs                # Tree-sitter wrapper (contains all native interop)
        RustQueries.cs                         # S-expression query strings
    Schema/
        rust_views.sql
    RustServiceCollectionExtensions.cs
    RepoQL.Formats.Rust.csproj                 # References: TreeSitter.DotNet, RepoQL.Contracts, RepoQL.Indexing

src/tests/RepoQL.Formats.Rust.Tests/
    RustLoaderTests.cs                         # Load + Materialize round-trip
    RustTreeSitterClientTests.cs               # Parser extraction correctness
    RustImplResolutionTests.cs                 # Impl block dissolution, stub nodes, cross-file parenting
    RustVisibilityTests.cs                     # Visibility normalization (pub/pub(crate)/pub(super)/private)
    RustDeriveTests.cs                         # Derive prop + edge creation, rust_derives view
    RustTraitTests.cs                          # Trait definitions, supertraits, IMPLEMENTS edges
    RustEnumTests.cs                           # Enum variants (unit/tuple/struct), discriminants
    RustGenericTests.cs                        # Generics, lifetimes, where clauses in props
    RustModuleTests.cs                         # Module declarations, inline vs external
    RustMacroTests.cs                          # macro_rules! defs, invocations, rs.macro_expansion annotations
    RustUseDeclarationTests.cs                 # Import paths, aliases, globs, re-exports
    RustUnsafeTests.cs                         # Unsafe functions, traits, impls, rust_unsafe view
    RustConcurrentParsingTests.cs              # Thread-safety of ThreadLocal<Parser>
    RustSharedViewTests.cs                     # Verify rs.type appears in Types, rs.member in Functions
    Fixtures/
        simple_struct.rs
        enum_with_variants.rs
        trait_definition.rs
        impl_blocks.rs
        cross_file_impl.rs
        visibility_modifiers.rs
        derives_and_attributes.rs
        generics_and_lifetimes.rs
        module_declarations.rs
        macro_definitions.rs
        use_declarations.rs
        unsafe_items.rs
        async_functions.rs
        type_aliases_and_constants.rs
        extern_block.rs
        malformed.rs
    RepoQL.Formats.Rust.Tests.csproj           # References: TUnit, AwesomeAssertions, FakeItEasy
```

---

*If an agent can find every trait implementation across scattered impl blocks, see a type's complete API as one unified view, query the full derive and unsafe surface in single SQL statements, and know exactly where macro expansion makes the graph incomplete — the loader is working.*
