---
description: "go_types → structs, interfaces, type definitions, aliases. go_functions → top-level functions. go_methods → methods with receivers. go_implements → interface satisfaction across files and stdlib. go_directives → goroutines, channels, selects, build constraints."
tags: ["go", "golang", "code", "interfaces", "goroutines", "modules"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Go Format

Query Go structs, interfaces, functions, methods, constants, variables, imports, dependencies, tests, directives, and interface satisfaction with SQL views. Syntactic extraction via tree-sitter — no Go toolchain required.

---

## Capsule: GoTypes

**Invariant**
`go_types` aggregates struct, interface, type definition, and type alias declarations with field and method counts.

**Example**
```sql
-- All types with member counts
SELECT name, type_kind, field_count, method_count
FROM go_types;

-- Interfaces
SELECT name, method_count
FROM go_types
WHERE type_kind = 'interface';

-- Structs with many fields
SELECT name, field_count, method_count
FROM go_types
WHERE type_kind = 'struct'
ORDER BY field_count DESC;
```
//BOUNDARY: `type_kind` is one of: `struct`, `interface`, `type_definition`, `type_alias`. Field and method counts come from `go.member` child nodes. Type definitions and aliases have zero fields — their underlying type is on the `go.type` node's `properties->>'underlying_type'`.

**Depth**
- `type_uri`: Addressable URI (use with `read` or `snippet`)
- `qualified_name`: `package.TypeName` format
- `package_name`: From the file's package declaration
- `field_count` / `method_count`: Counts of direct child `go.member` nodes
- Also participates in shared `Types` view via `WHERE n.kind LIKE '%.type'`

---

## Capsule: GoFunctionsAndMethods

**Invariant**
`go_functions` shows top-level functions (no receiver). `go_methods` shows methods with their receiver type.

**Example**
```sql
-- Public API surface
SELECT name, signature
FROM go_functions
WHERE visibility = 'public';

-- Methods on a type
SELECT name, signature, is_pointer_receiver
FROM go_methods
WHERE type_name = 'Program';

-- Functions returning error
SELECT name, signature
FROM go_functions
WHERE return_type LIKE '%error%';

-- All methods that modify their receiver
SELECT type_name, name, signature
FROM go_methods
WHERE is_pointer_receiver = true;
```
//BOUNDARY: Top-level functions are in `go_functions` (node kind `go.function`). Methods with receivers are in `go_methods` (node kind `go.member` with `kind='method'`). Both participate in the shared `Functions` view.

**Depth**
- `go_functions`: `name`, `qualified_name`, `visibility` (`public`/`private`), `parameters`, `return_type`, `signature`, `headline`
- `go_methods`: adds `type_name`, `type_qualified_name`, `declaring_type`, `receiver`, `receiver_type`, `is_pointer_receiver`
- `go_init_functions`: init functions specifically — `document_uri`, `package_name`, `function_name`, `start_line`
- Visibility follows Go convention: exported (uppercase) = `public`, unexported = `private`

---

## Capsule: GoFields

**Invariant**
`go_fields` shows struct field declarations with types, tags, and embedding status.

**Example**
```sql
-- Fields on a struct
SELECT name, field_type, tag, is_embedded
FROM go_fields
WHERE type_name = 'Program';

-- All embedded fields (composition)
SELECT type_name, name, field_type
FROM go_fields
WHERE is_embedded = true;

-- Fields with JSON tags
SELECT type_name, name, tag
FROM go_fields
WHERE tag LIKE '%json:%';
```
//BOUNDARY: `is_embedded` is true for anonymous fields (Go's composition mechanism). Embedded field names are derived from the type name.

---

## Capsule: GoImplements

**Invariant**
`go_implements` maps which concrete types satisfy which interfaces. Computed across files during idle processing, including well-known stdlib interfaces.

**Example**
```sql
-- What interfaces does a type satisfy?
SELECT interface_name, interface_target, receiver_kind
FROM go_implements
WHERE type_name = 'Key';

-- Who implements a local interface?
SELECT type_name, receiver_kind
FROM go_implements
WHERE interface_name = 'Model';

-- Stdlib interface satisfaction (fmt.Stringer, error, io.Reader, etc.)
SELECT type_name, interface_target, receiver_kind
FROM go_implements
WHERE is_stdlib = true;
```
//BOUNDARY: For stdlib interfaces, `interface_name` and `interface_uri` are null (the stdlib type node doesn't exist in the graph). Use `interface_target` for the interface name in all cases. `receiver_kind` is `value` or `pointer` — pointer receivers satisfy interfaces only via `*T`, value receivers satisfy via both `T` and `*T`.

**Depth**
- `is_stdlib`: true for well-known stdlib interfaces (error, fmt.Stringer, io.Reader, io.Writer, io.Closer, sort.Interface, etc.)
- `interface_target`: The interface name as a string — always populated, works for both local and stdlib
- `interface_name` / `interface_qualified_name`: Populated only for local interfaces (where the interface node exists in the graph)
- `receiver_kind`: `value` or `pointer` — reflects which method set was matched

---

## Capsule: GoImports

**Invariant**
`go_imports` shows import declarations with path, alias, and stdlib/external classification.

**Example**
```sql
-- External dependencies used
SELECT target, COUNT(*) as usage_count
FROM go_imports
WHERE import_category = 'external'
GROUP BY target
ORDER BY usage_count DESC;

-- Stdlib usage
SELECT target, COUNT(*) as usage_count
FROM go_imports
WHERE import_category = 'stdlib'
GROUP BY target
ORDER BY usage_count DESC;

-- Aliased imports
SELECT document_uri, target, alias
FROM go_imports
WHERE alias IS NOT NULL;
```
//BOUNDARY: `import_category` is `stdlib` (no dots in path) or `external` (contains dots). This is a heuristic, not a lookup.

---

## Capsule: GoDependencies

**Invariant**
`go_dependencies` and `go_replaces` expose `go.mod` module metadata — dependencies, versions, and path replacements.

**Example**
```sql
-- Direct dependencies
SELECT module_path, version
FROM go_dependencies
WHERE NOT is_indirect;

-- Indirect (transitive) dependencies
SELECT module_path, version
FROM go_dependencies
WHERE is_indirect;

-- Local path replacements (monorepo, development)
SELECT old_path, new_path, is_local_path
FROM go_replaces;
```
//BOUNDARY: `go_dependencies` reads from `go.mod` files only (filtered by `language = 'go.mod'`). `go_replaces` reads from `go.mod_replace` annotations.

---

## Capsule: GoConstants

**Invariant**
`go_constants` shows constant declarations. `go_enum_blocks` detects Go's enum-like iota patterns.

**Example**
```sql
-- All exported constants
SELECT name, const_type, const_value
FROM go_constants
WHERE is_exported = true;

-- Enum-like constant groups (iota patterns)
SELECT type_name, constant_names, constant_count
FROM go_enum_blocks;

-- Constants belonging to an enum type
SELECT name, const_value
FROM go_constants
WHERE enum_type = 'KeyType';
```
//BOUNDARY: `enum_type` is non-null only when the constant belongs to a `const ( ... )` block where the first spec has a named type and uses `iota`. This matches Go's standard enum pattern.

---

## Capsule: GoVariables

**Invariant**
`go_variables` shows package-level var declarations, with detection of sentinel errors and interface compile-time assertions.

**Example**
```sql
-- Sentinel errors (errors.New / fmt.Errorf patterns)
SELECT name, var_value
FROM go_variables
WHERE is_sentinel_error = true;

-- Interface assertions (var _ Interface = (*Type)(nil))
SELECT asserted_type, asserted_interface
FROM go_variables
WHERE is_interface_assertion = true;
```
//BOUNDARY: `is_sentinel_error` matches variables starting with `Err` whose value calls `errors.New` or `fmt.Errorf`, or addresses a struct ending in `Error`. `is_interface_assertion` matches `var _ SomeInterface = (*SomeType)(nil)` patterns.

---

## Capsule: GoTests

**Invariant**
`go_tests` detects test, benchmark, example, and fuzz functions in `_test.go` files.

**Example**
```sql
-- All tests
SELECT function_name, test_kind, tests_symbol, start_line
FROM go_tests;

-- Just benchmarks
SELECT function_name, tests_symbol
FROM go_tests
WHERE test_kind = 'benchmark';

-- Test coverage by file
SELECT document_uri, COUNT(*) as test_count
FROM go_tests
GROUP BY document_uri;
```
//BOUNDARY: `test_kind` is one of: `test`, `benchmark`, `example`, `fuzz`, `testmain`. `tests_symbol` is the name suffix after the prefix (e.g., `TestFoo` → `Foo`). Detection requires the file to end with `_test.go`.

---

## Capsule: GoDirectives

**Invariant**
`go_directives` surfaces compiler directives from comments (`//go:build`, `//go:embed`, `//go:generate`, `//go:linkname`) and concurrency patterns (goroutine launches, channel declarations, select statements).

**Example**
```sql
-- Build constraints
SELECT document_uri, directive_text
FROM go_directives
WHERE directive_kind = 'build';

-- Goroutine launch sites
SELECT document_uri, directive_text, start_line
FROM go_directives
WHERE directive_kind = 'goroutine';

-- Concurrency complexity
SELECT directive_kind, COUNT(*) as count
FROM go_directives
WHERE directive_kind IN ('goroutine', 'channel', 'select')
GROUP BY directive_kind;
```
//BOUNDARY: `directive_kind` values: `build`, `embed`, `generate`, `linkname` (from comments), `goroutine`, `channel`, `select` (from syntax tree). `directive_text` is the directive or expression text.

---

## Capsule: GoEmbeds

**Invariant**
`go_embeds` shows struct embedding (composition) relationships.

**Example**
```sql
-- What does a struct embed?
SELECT embedded_type
FROM go_embeds
WHERE struct_name = 'standardRenderer';

-- Find all types that embed a given type
SELECT struct_name, document_uri
FROM go_embeds
WHERE embedded_type = 'Mutex';
```
//BOUNDARY: `go_embeds` reads `EMBEDS` edges sourced from `go.type` nodes. These are Go's anonymous struct fields — the mechanism for composition and method promotion.

---

## Views

```sql
go_types(document_uri, type_uri, name, qualified_name, type_kind, package_name, field_count, method_count)
go_functions(document_uri, package_name, function_uri, headline, name, qualified_name, visibility, parameters, return_type, signature)
go_methods(document_uri, type_uri, type_name, type_qualified_name, method_uri, headline, name, declaring_type, receiver, receiver_type, is_pointer_receiver, visibility, parameters, return_type, signature)
go_fields(document_uri, type_uri, type_name, type_qualified_name, field_uri, name, field_type, tag, is_embedded, visibility)
go_constants(uri, document_uri, name, qualified_name, const_type, const_value, is_exported, enum_type, start_line, node_id)
go_variables(uri, document_uri, name, qualified_name, var_type, var_value, is_exported, is_sentinel_error, is_interface_assertion, asserted_interface, asserted_type, start_line, node_id)
go_imports(document_uri, package_name, target, alias, import_category)
go_embeds(struct_uri, struct_name, embedded_type, document_uri)
go_enum_blocks(document_uri, type_name, constant_names, constant_count)
go_tests(document_uri, function_name, test_kind, tests_symbol, start_line)
go_init_functions(document_uri, package_name, function_name, start_line, node_id)
go_directives(document_uri, directive_kind, directive_text, start_line)
go_implements(type_uri, type_name, type_qualified_name, interface_uri, interface_name, interface_qualified_name, receiver_kind, is_stdlib, interface_target)
go_dependencies(document_uri, module_path, version, is_indirect)
go_replaces(document_uri, old_path, old_version, new_path, new_version, is_local_path)
```

---

## Node Kinds

- `go.type` — Struct, interface, type definition, or type alias (distinguished by `properties->>'kind'`: `struct`, `interface`, `type_definition`, `type_alias`)
- `go.member` — Method, field, constant, or variable (distinguished by `properties->>'kind'`: `method`, `field`, `constant`, `variable`)
- `go.function` — Top-level function (no receiver)

## Edge Types

- `HAS_PART` — Composition (document → type → member, document → function)
- `IMPORTS` — Import declaration (document → target path in properties)
- `EMBEDS` — Struct embedding (type → target type name in properties)
- `IMPLEMENTS` — Interface satisfaction (type → interface, computed during idle processing)
- `DEPENDS_ON` — Module dependency from go.mod (document → module path in properties)

## Annotation Kinds

- `go.test` — Test/benchmark/example/fuzz function detection
- `go.enum_block` — Constant block with iota and named type
- `go.build_constraint` — `//go:build` directive
- `go.generate` — `//go:generate` directive
- `go.embed` — `//go:embed` directive
- `go.linkname` — `//go:linkname` directive
- `go.goroutine` — `go` statement (goroutine launch)
- `go.channel` — Channel type declaration
- `go.select` — Select statement
- `go.interface_assertion` — Compile-time interface check (`var _ I = (*T)(nil)`)
- `go.mod_replace` — Replace directive in go.mod

---

## File Extensions

| Extension / Name | Media Type Kind |
|------------------|-----------------|
| `.go` | `code.go` |
| `go.mod` | `code.go.mod` |
| `go.work` | `code.go.work` |

---

## Common Patterns

| Goal | Query |
|------|-------|
| Find all Go files | `SELECT uri, headline FROM Files WHERE lang = 'go'` |
| List structs | `SELECT name, field_count, method_count FROM go_types WHERE type_kind = 'struct'` |
| List interfaces | `SELECT name, method_count FROM go_types WHERE type_kind = 'interface'` |
| Methods on a type | `SELECT name, signature FROM go_methods WHERE type_name = 'MyType'` |
| Public API surface | `SELECT name, signature FROM go_functions WHERE visibility = 'public'` |
| Who implements X? | `SELECT type_name, receiver_kind FROM go_implements WHERE interface_name = 'Handler'` |
| Stdlib compliance | `SELECT type_name, interface_target FROM go_implements WHERE is_stdlib = true` |
| Error surface | `SELECT name, return_type FROM go_functions WHERE return_type LIKE '%error%'` |
| Sentinel errors | `SELECT name, var_value FROM go_variables WHERE is_sentinel_error = true` |
| Module dependencies | `SELECT module_path, version FROM go_dependencies WHERE NOT is_indirect` |
| Import breakdown | `SELECT import_category, COUNT(*) FROM go_imports GROUP BY import_category` |
| Test inventory | `SELECT test_kind, COUNT(*) FROM go_tests GROUP BY test_kind` |
| Concurrency sites | `SELECT directive_kind, COUNT(*) FROM go_directives WHERE directive_kind IN ('goroutine','channel','select') GROUP BY directive_kind` |
| Enum types | `SELECT type_name, constant_count FROM go_enum_blocks` |
| Struct composition | `SELECT struct_name, embedded_type FROM go_embeds` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Looking for `go.struct` or `go.interface` node kinds | Both are `go.type` — filter by `properties->>'kind'` or use `go_types.type_kind` |
| Looking for `go.method` node kind | Methods are `go.member` with `kind='method'` — use `go_methods` view |
| Using `interface_name` for stdlib interfaces | Stdlib interface nodes don't exist in the graph — use `interface_target` which is always populated |
| Expecting `go_tests` data without `_test.go` files | Test detection requires filename to end with `_test.go` |
| Using `properties->>'kind'` in WHERE clauses | Use `json_extract_string(properties, '$.kind')` in WHERE/CASE to avoid DuckDB type coercion errors |
| Expecting runtime type information | Extraction is syntactic (tree-sitter) — no Go toolchain, no type inference |
| Querying `go_dependencies` for import paths | `go_dependencies` reads `go.mod`. For import paths in source files, use `go_imports` |
| Confusing `go_embeds` with `go_fields WHERE is_embedded` | Both show embeddings — `go_embeds` has EMBEDS edges (type→type), `go_fields` has the field nodes with `is_embedded = true` |
