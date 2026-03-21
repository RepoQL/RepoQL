---
description: Plan for C/C++ format loader — multi-file analysis, SQL views, shared view updates, and help:// documentation
tags: [format, cpp, c, plan, multi-file, views, documentation]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: C/C++ Loader — Cross-File Intelligence, SQL Views, and Documentation

Implements: [C/C++ Format Design](../designs/future/cpp-format-loader.md) — Multi-File Analysis, SQL Views, Enrichment Interface (design only), Documentation

## Scope

**Covers:**
- `CppMultiFileAnalyzer` — idle-processing multi-file analysis
- Header/source linking — `REFERS_TO` edges with `relationship=defines` between declarations and definitions
- Inheritance graph completion — `EXTENDS` edges between derived and base class nodes across files
- Transitive include computation — edges recording transitive header dependencies
- Forward declaration resolution — `REFERS_TO` edges from forward declarations to full definitions
- SQL views: `cpp_classes`, `cpp_functions`, `cpp_includes`, `cpp_templates`, `cpp_enums`, `cpp_macro_invocations`, `cpp_namespace_members`
- `IFormatSchemaProvider` implementation for view registration
- Shared `Functions` view update — add `'cpp.member'` and `'cpp.function'` to kind filter
- `help://` documentation for C/C++ format
- Validation against north-star declarations — verify each "covered" claim from the design
- Tests for multi-file analysis and SQL views

**Does not cover:**
- `ICppEnricher` implementation — interface is designed (Plan 01 design), not built in this increment
- libclang integration — future enrichment, not initial delivery
- Build system parsing — `compile_commands.json` and CMakeLists.txt analysis is a future extension

## Enables

Once this exists:
- **Agents can see complete types across header and source** — `ConnectionPool` declared in `pool.h` and defined in `pool.cpp` appears as one unified entity
- **Agents can trace the full inheritance tree** — "What derives from Transport?" answered from `EXTENDS` edges across all files
- **Agents can trace transitive include chains** — "What does changing `mutex.h` affect?" answered without a build system
- **Agents can query C++ through purpose-built views** — `cpp_classes`, `cpp_functions`, `cpp_templates` provide the right projection over the graph
- **Agents can find everything in a namespace across all files** — `cpp_namespace_members` unifies namespace contributions
- **C/C++ format support is documented** — `help://` docs let agents self-discover C++ query patterns
- **C/C++ format support is complete for v1** — all three plans delivered

This is the final plan. After this, the C/C++ format loader delivers everything described in the design's Option A (tree-sitter only).

## Prerequisites

- Plan: cpp-01-grammar-and-basic-parsing complete — grammar, classifier, materializer, core node types
- Plan: cpp-02-error-handling-and-analysis complete — include edges, macro annotations, template properties, preprocessor nodes
- Idle processing infrastructure — `IndexingEngine` epoch tracking and idle work queue (see `src/Indexing/RepoQL.Indexing/Indexing/IndexingEngine.cs`)

## North Star

An agent in a 3,000-file C++ project asks "what inherits from Transport?" and gets every derived class. It asks "show me the net namespace" and sees unified declarations across 20 headers. It asks "what includes `<mutex>`?" and traces the chain. No file was opened. No build system was configured. The query surface feels identical to querying C# or Ruby — same views, same patterns, same SQL.

## Done Criteria

### Multi-File Analysis Infrastructure

- The `CppMultiFileAnalyzer` shall run during idle processing after the hot-path epoch drains
- The analyzer shall process the batch of newly-indexed C/C++ files in the current epoch
- When a referenced target file is not yet indexed, the analyzer shall skip that edge — it will be resolved on the next idle cycle when the target appears

### Header/Source Linking

- For each function definition node with a qualified name in a source file (`.cpp`, `.cc`, `.cxx`), the analyzer shall search for a matching declaration node in header files (`.h`, `.hpp`, `.hh`, `.hxx`)
- Matching shall be by qualified name + arity (number of parameters)
  - When multiple matches exist (overloads with same arity), the analyzer shall create edges for all matches and log a warning
  - When no match exists, the analyzer shall skip (declaration may be in an unindexed header)
- Each match shall create a `REFERS_TO` edge with `relationship = "defines"` in properties:
  - Source: the declaration node (in header)
  - Destination: the definition node (in source)
- The analyzer shall also link method definitions to their class declarations:
  - `void ConnectionPool::connect(...)` in `pool.cpp` → declaration in `ConnectionPool` class in `pool.h`

### Inheritance Graph Completion

- For each `cpp.type` node with a non-empty `extends` property, the analyzer shall resolve base class names to their definition nodes across the index
- Resolution shall search by unqualified name first, then qualified name if ambiguous
  - When the base class name matches exactly one node → create edge
  - When the base class name matches multiple nodes → log warning, skip (ambiguous without type resolution)
  - When the base class name matches no nodes → skip (base class may be in an unindexed file or external library)
- Each resolved base shall create an `EXTENDS` edge:
  - Source: the derived class node
  - Destination: the base class node
  - Properties: `access` (`public`, `private`, `protected`), `is_virtual` (`"true"` or absent)

### Transitive Include Computation

- The analyzer shall compute transitive include chains from existing direct include edges
- For each source file, the analyzer shall walk its direct includes, then their includes, recursively:
  - Create `REFERS_TO` edges with `relationship = "transitive_include"` and `depth` property (integer)
  - When a cycle is detected, the analyzer shall stop recursion for that path and emit an annotation with `rule_id = "cpp/include_cycle"`
- System includes (`<>` style) that are not in the index shall terminate the chain (no further resolution)

### Forward Declaration Resolution

- For each forward declaration (e.g., `class Foo;` without body), the analyzer shall search for the full definition across the index
- Matching shall be by qualified name
- Each match shall create a `REFERS_TO` edge with `relationship = "forward_declares"`:
  - Source: the forward declaration node
  - Destination: the full definition node

### SQL Views

- The `IFormatSchemaProvider` implementation shall register all C++ views during schema initialization
- All views shall use `CREATE OR REPLACE VIEW` syntax

#### cpp_classes
- The view shall project `cpp.type` nodes where `kind` IN (`class`, `struct`, `union`)
- Columns: `uri`, `file_uri`, `name`, `qualified_name`, `type_kind`, `default_access`, `extends`, `is_abstract`, `start_line`, `end_line`, `headline`, `node_id`, `span_id`
- `file_uri` derived via `repository_uri_container(n.uri)`
- `start_line`/`end_line` derived via `TRY_CAST(repository_uri_line_start/end(n.uri) AS INTEGER)`

#### cpp_functions
- The view shall project `cpp.member` and `cpp.function` nodes where `kind` IN (`method`, `constructor`, `function`)
- Columns: `uri`, `file_uri`, `name`, `qualified_name`, `declaring_type`, `return_type`, `access`, `signature`, `is_virtual`, `is_pure_virtual`, `is_noexcept`, `is_constexpr`, `is_static`, `start_line`, `end_line`, `headline`, `node_id`, `span_id`
- Boolean fields (`is_virtual`, `is_pure_virtual`, `is_noexcept`, `is_constexpr`, `is_static`) shall be cast to boolean via `COALESCE(properties->>'field', 'false') = 'true'`

#### cpp_includes
- The view shall project `cpp.include` nodes
- Columns: `target_header`, `include_style`, `source_uri`, `node_id`

#### cpp_templates
- The view shall project nodes with `is_template = 'true'` and `kind LIKE 'cpp.%'`
- Columns: `uri`, `name`, `template_params`, `base_template`, `template_args`, `template_kind` (computed: `specialization` or `primary`), `file_uri`, `node_id`

#### cpp_enums
- The view shall project `cpp.type` nodes where `kind = 'enum'`
- Columns: `uri`, `name`, `is_scoped`, `underlying_type`, `file_uri`, `node_id`

#### cpp_macro_invocations
- The view shall project annotations with `rule_id = 'cpp/macro_interference'`
- Columns: `id`, `message`, `name` (from `data`), `context` (from `data`), `file_uri`, `start_line`, `end_line`, `span_id`
- `start_line`/`end_line` extracted from annotation `data` JSON

#### cpp_namespace_members
- The view shall project all `cpp.%` nodes that have a non-null `namespace` property
- Columns: `namespace`, `name`, `member_kind`, `accessibility`, `file_uri`, `node_id`

### Shared View Updates

- The shared `Functions` view (`src/RepoQL.Data.DuckDB/Schema/Views/functions.sql`) shall have `'cpp.member'` and `'cpp.function'` added to its kind filter
- No changes to the shared `Types` view — `cpp.type` already matches `WHERE kind LIKE '%.type'`

### Documentation

- A `help://` document shall be created at `src/RepoQL.Documentation/repoql/tools/query/formats/cpp.md`
- The document shall include:
  - Overview of C/C++ support capabilities
  - Available SQL views with column descriptions
  - Example queries for common tasks (find classes, trace inheritance, query namespaces, find macros)
  - Preprocessor boundary explanation — what agents can and can't see
  - Known limitations (no macro expansion, no type resolution without libclang)

### North-Star Validation

- Each "Covered" declaration in the design's north-star coverage table shall be verified against a test C++ project:
  - Discovery: headlines show classes, functions, namespaces
  - Header/source split: declarations and definitions linked
  - Preprocessor: includes as edges, macros as nodes, conditional blocks as annotations
  - Classes: members with access specifiers, virtual methods, base classes
  - Inheritance: `EXTENDS` edges, cross-file resolution
  - Templates: parameters, specializations, constraints
  - Namespaces: unified view across files
  - Enums: scoped vs unscoped, enumerators
  - Functions: signatures, qualifiers
  - Include graph: direct and transitive
  - Integrity: error isolation, macro classification
- The two "Partial" declarations shall be documented with their specific limitations

### Tests

- Test header/source linking — create `pool.h` with declaration and `pool.cpp` with definition → verify `REFERS_TO` edge with `relationship=defines`
- Test header/source linking with overloads — two functions with same name, different arity → verify correct matching
- Test inheritance graph — `TcpTransport : public Transport` across files → verify `EXTENDS` edge with `access=public`
- Test virtual inheritance — `class D : virtual public B` → verify `is_virtual = "true"` on edge
- Test multiple inheritance — class with two bases → verify two `EXTENDS` edges
- Test transitive includes — A includes B includes C → verify transitive edge from A to C
- Test include cycle detection — A includes B includes A → verify `cpp/include_cycle` annotation
- Test forward declaration resolution — `class Foo;` in one file, `class Foo { ... }` in another → verify `REFERS_TO` with `relationship=forward_declares`
- Test `cpp_classes` view — verify class, struct, union appear with correct columns
- Test `cpp_functions` view — verify boolean casting for `is_virtual`, `is_static`, etc.
- Test `cpp_includes` view — verify include style and target columns
- Test `cpp_templates` view — verify `template_kind` computed column
- Test `cpp_enums` view — verify scoped enum with underlying type
- Test `cpp_macro_invocations` view — verify annotation data extraction
- Test `cpp_namespace_members` view — verify cross-file namespace unification
- Test shared Functions view — verify `cpp.member` and `cpp.function` appear in results
- Tests shall use TUnit (`[Test]`), AwesomeAssertions, and FakeItEasy per project conventions

## Constraints

- **Qualified-name matching only** — no type-signature matching for header/source linking; design chose simplicity over precision (type resolution requires libclang)
- **Arity-based disambiguation** — best effort for overloads without full type resolution; false positives are rare (overloaded functions across TUs are uncommon)
- **No `ICppEnricher` implementation** — the interface exists in the design as a future extension point; this plan does not build any enricher
- **Views use frozen schema only** — `CREATE OR REPLACE VIEW` over `node`, `edge`, `span`, `annotation`; no new tables
- **Documentation ships with features** — `help://` docs are part of this plan, not a follow-up

## References

- [C/C++ Format Design](../designs/future/cpp-format-loader.md) — multi-file analysis, SQL views, enrichment interface, north-star coverage table
- [C/C++ Format North Star](../north-star/formats/cpp.md) — full declaration set for validation
- [C/C++ Indexing Flow](../flows/future/cpp-indexing.md) — idle processing stage, multi-file analysis
- Plan: cpp-01-grammar-and-basic-parsing — materializer, node types, x-ray templates
- Plan: cpp-02-error-handling-and-analysis — include edges, macro annotations, template properties
- Existing SQL views (`src/RepoQL.Data.DuckDB/Schema/Views/`) — `types.sql`, `functions.sql` as patterns
- C# views (`src/Formats/RepoQL.Formats.DotNet/Schema/csharp_views.sql`) — language-specific view pattern
- Ruby views (`src/Formats/RepoQL.Formats.Ruby/Schema/ruby_views.sql`) — edge type pattern
- [Processor Guide](../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — idle processor patterns
- [Testing Guidelines](../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy conventions

## Error Policy

Multi-file analysis errors must not cascade across files. When resolution fails for a specific edge:
1. Log warning with source URI, target name, and reason (ambiguous, not found, etc.)
2. Skip the edge — it may be resolved on the next idle cycle
3. Continue processing remaining files in the epoch

View creation failures are more serious — a malformed view prevents all queries through that surface:
1. Log error with view name and SQL error
2. Skip the failing view
3. Emit a diagnostic annotation visible in `::diagnostics`
4. Other views and the rest of the format loader are unaffected
