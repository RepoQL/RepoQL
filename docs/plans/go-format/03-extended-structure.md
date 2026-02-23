---
description: Plan for Go format — type definitions, constants with iota/enum detection, variables, compiler directives, test function detection, init functions, and concurrency markers
tags: [format, go, golang, plan, constants, directives, tests, enum]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Go — Extended Structure

Implements: [Go Format Design](../../designs/future/go-format.md) — Extension Points (type definitions, constants, variables, compiler directives, test detection, init functions, concurrency markers), SQL Views (go_constants, go_variables, go_enum_blocks, go_tests, go_init_functions, go_directives, go_embeds)

## Scope

**Covers:**
- Type definitions and type aliases as `go.type` nodes with additional `kind` values
- Constants as `go.member` nodes with `kind: "constant"`, including value extraction
- Iota/enum detection: const blocks with named type + `iota` recognized as enum patterns
- Package-level variables as `go.member` nodes with `kind: "variable"`
- Sentinel error detection (`var ErrFoo = errors.New(...)`)
- Interface assertion detection (`var _ Handler = (*Server)(nil)`)
- Compiler directives: `//go:build`, `//go:embed`, `//go:generate`, `//go:linkname` as annotations
- Test function detection: `TestXxx`, `BenchmarkXxx`, `ExampleXxx`, `FuzzXxx`, `TestMain` as annotations
- Init function detection: `func init()` identification
- Concurrency markers: `go` statement sites, channel declarations, `select` statements as annotations
- Surface model extensions for all new extractions
- SQL views: `go_constants`, `go_variables`, `go_enum_blocks`, `go_tests`, `go_init_functions`, `go_directives`, `go_embeds`
- Tests for each feature with dedicated fixtures

**Does not cover:**
- go.mod / go.work parsing (Plan: 04-module-metadata)
- Interface satisfaction (Plan: 05-interface-satisfaction)
- Generics / type parameters (extension point)
- CGo detection (extension point)

## Enables

Once this exists:
- **Enum patterns queryable** — `SELECT * FROM go_enum_blocks WHERE type_name = 'Color'` shows iota-based enum constants
- **Constants discoverable** — `SELECT * FROM go_constants WHERE declaring_type = 'Config'`
- **Test inventory** — `SELECT * FROM go_tests WHERE test_kind = 'benchmark'` finds all benchmarks
- **Init function map** — `SELECT * FROM go_init_functions` shows all init functions across the codebase
- **Build constraints visible** — `SELECT * FROM go_directives WHERE directive_kind = 'build'` shows platform-specific files
- **Sentinel errors findable** — `SELECT * FROM go_variables WHERE is_sentinel_error` shows all sentinel errors
- **Interface assertions visible** — `SELECT * FROM go_variables WHERE is_interface_assertion` shows compile-time interface checks
- **Embedding relationships queryable** — `SELECT * FROM go_embeds` shows struct embedding chains
- **Concurrency hotspots** — `SELECT * FROM go_directives WHERE directive_kind IN ('goroutine', 'channel')` highlights concurrent code

## Prerequisites

- Plan 02 complete — `go.type`, `go.member`, `go.function` nodes exist, materialization pipeline operational, `GoDocumentSurface` populated
- `GoTreeSitterClient` extensible query execution available (Plan 01)

## North Star

Every structural element in a Go file — not just types and functions, but constants, variables, compiler directives, test functions, init functions — queryable without reading the file. When a const block uses `iota` with a named type, the agent sees an enum pattern. When a file has `//go:build linux`, the agent knows it's platform-specific.

## Done Criteria

### Surface Model Extensions
- `GoDocumentSurface` shall be extended with: TypeDefinitions[], Constants[], Variables[], Directives[], InitFunctions[]
- `GoTypeDefinitionInfo` shall carry: Name, UnderlyingType (text), IsAlias (bool), IsExported, ByteRange
- `GoConstantInfo` shall carry: Name, TypeName (if typed), Value (text), IsExported, ByteRange
- `GoConstantBlockInfo` shall carry: Constants[], TypeName (shared type for block), HasIota (bool), ByteRange
- `GoVariableInfo` shall carry: Name, TypeName (text), Value (text), IsExported, IsSentinelError, IsInterfaceAssertion, ByteRange
- `GoDirectiveInfo` shall carry: Kind (build/embed/generate/linkname/goroutine/channel/select), Text (raw directive), ByteRange

### Type Definitions and Aliases
- The client shall extract `type Name UnderlyingType` definitions (e.g., `type UserID int64`)
- The materializer shall create `go.type` nodes with `kind: "type_definition"` and `underlying_type` prop
- The client shall extract `type Name = UnderlyingType` aliases (e.g., `type Strings = []string`)
- The materializer shall create `go.type` nodes with `kind: "type_alias"` and `underlying_type` prop
- Type definitions and aliases shall participate in the `go_types` view

### Constants
- The client shall extract constant declarations (single and grouped) with name, type (if explicit), and value text
- The materializer shall create `go.member` nodes with `kind: "constant"`, `name`, `qualified_name`, `declaring_type` (enclosing type if any, else null), `accessibility`, `is_exported`, `const_type`, `const_value`
- Constants shall have `HAS_PART` edges from the document node (or enclosing type if inside a type block, though Go doesn't have type-scoped constants — they're always package-level)

### Iota/Enum Detection
- When a const block has a named type on the first spec and `iota` appears in the expression, the block shall be recognized as an enum pattern
- The materializer shall emit a `go.enum_block` annotation with: type_name, constant_names (list), constant_count
- All subsequent specs in the block that lack an explicit type shall inherit the type from the first spec
- Constants within an enum block shall carry `enum_type` in their properties

### Package-Level Variables
- The client shall extract `var` declarations (single and grouped) with name, type, and value text
- The materializer shall create `go.member` nodes with `kind: "variable"`, `name`, `qualified_name`, `accessibility`, `is_exported`, `var_type`, `var_value`

### Sentinel Error Detection
- When a variable's name starts with `Err` and its value matches `errors.New(...)` or `fmt.Errorf(...)`, the materializer shall set `is_sentinel_error: "true"`
- The materializer shall also detect sentinel errors assigned with `= &FooError{}` patterns

### Interface Assertion Detection
- When a variable declaration matches `var _ InterfaceName = (*TypeName)(nil)` or `var _ InterfaceName = TypeName{}`, the materializer shall set `is_interface_assertion: "true"` with `asserted_interface` and `asserted_type` props
- Interface assertions shall emit a `go.interface_assertion` annotation with interface and type names

### Compiler Directives
- The client shall extract `//go:build` constraints from comments and emit `go.build_constraint` annotations with the constraint expression
- The client shall extract `//go:generate` directives and emit `go.generate` annotations with the command text
- The client shall extract `//go:embed` directives and emit `go.embed` annotations with the pattern
- The client shall extract `//go:linkname` directives and emit `go.linkname` annotations with the local and remote symbol names

### Test Function Detection
- In files with names matching `*_test.go`, the client shall pattern-match function names:
  - `TestXxx` (uppercase letter after Test) → `go.test` annotation with `test_kind: "test"`, `tests_symbol: "Xxx"`
  - `BenchmarkXxx` → `test_kind: "benchmark"`
  - `ExampleXxx` → `test_kind: "example"`
  - `FuzzXxx` → `test_kind: "fuzz"`
  - `TestMain` → `test_kind: "testmain"`
- Annotations shall reference the function node's span
- The `go.function` node for the test function shall carry `test_kind` in its properties

### Init Function Detection
- The client shall detect all `func init()` declarations (multiple per file allowed)
- Each init function shall be a `go.function` node with `is_init: "true"` in properties
- The `go_init_functions` view shall show all init functions with file path

### Concurrency Markers
- The client shall detect `go` statement sites and emit `go.goroutine` annotations with the called function/expression
- The client shall detect channel type declarations (`chan T`, `<-chan T`, `chan<- T`) and emit `go.channel` annotations
- The client shall detect `select` statements and emit annotations

### SQL Views
- `go_constants` shall show: uri, file_uri, name, qualified_name, const_type, const_value, is_exported, enum_type, start_line, node_id
- `go_variables` shall show: uri, file_uri, name, qualified_name, var_type, var_value, is_exported, is_sentinel_error, is_interface_assertion, asserted_interface, asserted_type, start_line, node_id
- `go_enum_blocks` shall show: file_uri, type_name, constant_names, constant_count (from `go.enum_block` annotations)
- `go_tests` shall show: file_uri, function_name, test_kind, tests_symbol, start_line (from `go.test` annotations joined to function nodes)
- `go_init_functions` shall show: file_uri, file_name, package_name, start_line, node_id
- `go_directives` shall show: file_uri, directive_kind, directive_text, start_line (from annotations)
- `go_embeds` shall show: struct_uri, struct_name, embedded_type, file_uri (from `EMBEDS` edges)
- All views shall be added to the existing `go_views.sql` embedded resource

### Test Fixtures
- `Fixtures/enum_pattern.go` — const block with iota, named type, multiple values
- `Fixtures/type_definitions.go` — type definitions and aliases
- `Fixtures/variables.go` — sentinel errors, interface assertions, regular variables
- `Fixtures/directives.go` — build constraints, embed, generate, linkname
- `Fixtures/test_file_test.go` — Test, Benchmark, Example, Fuzz, TestMain functions
- `Fixtures/init_functions.go` — multiple init functions in one file
- `Fixtures/concurrency.go` — goroutine launches, channel declarations, select statements

## Constraints

- **Additive only** — this plan extends the surface model and materializer without changing existing extraction. No modifications to Plan 02's core node/edge generation
- **Tree-sitter extensible queries** — new extraction patterns use the extensible query execution from Plan 01, not modifications to the core query set
- **Directives from comments** — Go compiler directives are syntactically comments. The client must scan comment nodes for `//go:` prefixes. Tree-sitter captures comments; the directive extraction layer filters and classifies them
- **Test detection is filename-gated** — only files matching `*_test.go` are scanned for test function patterns. This avoids false positives on functions named `TestHelper` in non-test files
- **Iota detection is heuristic** — the check is: first const spec has a named type AND `iota` appears in the expression. This catches the standard Go enum pattern. Unusual iota usage (e.g., iota without a named type) is not flagged as an enum

## References

- [Go Format Design](../../designs/future/go-format.md) — Extension Points section
- [Go North Star](../../north-star/formats/go.md) — constants/enum, compiler directives, testing queries
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

| Failure | Behavior |
|---------|----------|
| Const block iota detection ambiguous | Default to not marking as enum — false negatives preferred |
| Variable value too complex to extract as text | Store null value, name and type still extracted |
| Directive comment malformed | Skip annotation, log diagnostic |
| Test function name doesn't match any pattern | Not annotated — regular function |
| Init function has parameters (invalid Go) | Skip, tree-sitter may still parse it; don't create annotation |
| Concurrency pattern in complex expression | Best-effort annotation — may miss some patterns |
