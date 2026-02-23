---
description: Design for Go format support — extracting packages, types, functions, methods, interfaces, and their relationships from Go source via tree-sitter
tags: [format, go, golang, tree-sitter, design, code]
audience: { human: 45, agent: 55 }
purpose: { design: 80, flow: 20 }
---

# Go Format — Design

## North Star

An agent should understand a Go codebase's structure — packages, types, interfaces, methods, and their relationships — without reading source files, and query it all through the same SQL surface as every other format. When a struct's methods are defined across three files, the agent sees one complete type. When an interface requires two methods, the agent finds every struct in the codebase whose method set satisfies it — even though none of them declare `implements`. Go's implicit interfaces didn't hide relationships. The graph computed them.

**Informed by:** `docs/north-star/formats/go.md`
**Research:** `docs/research/go-parsing-from-dotnet.md`

## Context

Go files appear in repositories as application code, libraries, CLIs, tests, benchmarks, examples, and generated code. Go is simpler to parse than Ruby or C# — no operator overloading, no macros, no ambiguous operators, mandatory braces. But it has structural features unlike any other language RepoQL supports:

- **Implicit interface implementation** — types satisfy interfaces by having the right methods, with no keyword or declaration. This is the most important relationship in the language, and it must be computed, not declared.
- **Struct embedding** — composition via anonymous fields promotes methods and fields to the outer struct, changing which interfaces a type satisfies.
- **Visibility by casing** — `Exported` (uppercase) vs `unexported` (lowercase). No keywords, no state machine.
- **Methods across files** — a type's methods may be defined in any file in the same package. One type, many files.
- **Enum by convention** — `const` blocks with `iota` and a named type. No `enum` keyword.
- **Compiler directives** — `//go:build`, `//go:embed`, `//go:generate` are comments with semantic weight.
- **go.mod** — module metadata and dependency declarations. Separate file format from `.go`.

The Ruby format loader established the pattern for using tree-sitter in a code format. This design follows that pattern closely — same `IFormatLoader`/`IFormatMaterializer` split, same tree-sitter client architecture, same state transfer via `DocumentModel.Metadata`, same SQL view registration. Go is a simpler language than Ruby (no open classes, no metaprogramming, no visibility state machine), so the per-file parser is straightforward. The unique challenge is the semantic layer: computing implicit interface satisfaction from method sets across files.

## Constraints

| Constraint | Source |
|------------|--------|
| Single writer to DuckDB | Hard constraint — all writes through `DuckDbDataStore` |
| Frozen schema — 5 tables only | Extend via views/macros/UDFs, never new tables |
| Errors never cascade | One malformed `.go` file must never stop indexing |
| TreeSitter.DotNet (MIT) | NuGet package with Go grammar bundled. Cross-platform native binaries for all six RepoQL targets |
| No Go runtime required | Parser runs in-process via native tree-sitter library |
| tree-sitter parsers not thread-safe | Each thread needs its own Parser instance |

---

## Design

### Classification

Go files get provisional media type from the naming convention layer. The classifier confirms and adds the kind parameter.

| Pattern | Kind | Notes |
|---------|------|-------|
| `*.go` | `code.go` | Standard Go source |
| `*_test.go` | `code.go` | Tests — distinguished by annotation, not media type |
| `go.mod` | `code.go.mod` | Module metadata |
| `go.work` | `code.go.work` | Workspace metadata |

```csharp
SemanticMediaType.Create("text", "x-go").WithKind("code.go")
SemanticMediaType.Create("text", "x-go-mod").WithKind("code.go.mod")
SemanticMediaType.Create("text", "x-go-work").WithKind("code.go.work")
```

**Test files** (`*_test.go`) share the `code.go` media type. They are Go source files that happen to contain test functions. The test nature is captured per-function via annotations, not per-file via classification — a test file may also contain helper types and production-adjacent code.

### Tree-Sitter Integration

The core complexity is contained behind `GoTreeSitterClient` — a thin wrapper around TreeSitter.DotNet that no other component touches. No tree-sitter types escape this class.

```
GoTreeSitterClient
├── Parse(string sourceCode) → GoDocumentSurface
├── Thread-local Parser instances (tree-sitter is not thread-safe)
└── S-expression queries for symbol extraction
```

**Thread safety:** Same pattern as Ruby — `ThreadLocal<Parser>` with a shared immutable `Language` object.

**Query-based extraction:** Tree-sitter queries target specific patterns structurally. Go's rigid syntax makes queries particularly reliable — no optional parentheses, no ambiguous operators.

```scheme
;; Package clause
(package_clause (package_identifier) @package_name)

;; Imports (single and grouped)
(import_declaration
    (import_spec
        name: (package_identifier)? @alias
        path: (interpreted_string_literal) @path))

;; Struct declarations
(type_declaration
    (type_spec
        name: (type_identifier) @struct_name
        type: (struct_type) @struct_body))

;; Interface declarations
(type_declaration
    (type_spec
        name: (type_identifier) @iface_name
        type: (interface_type) @iface_body))

;; Functions (no receiver)
(function_declaration
    name: (identifier) @func_name
    parameters: (parameter_list) @params
    result: (_)? @result)

;; Methods (with receiver)
(method_declaration
    receiver: (parameter_list) @receiver
    name: (field_identifier) @method_name
    parameters: (parameter_list) @params
    result: (_)? @result)
```

**Error recovery:** Same as Ruby — tree-sitter always returns a complete tree. Invalid regions produce `ERROR` nodes while the rest parses normally. Particularly valuable for Go files being actively edited.

**Grammar maturity:** tree-sitter-go is under the official tree-sitter organization, 400+ commits, used by GitHub, Neovim, Helix, and Zed. Generics support added in v0.20.0. More mature than the Ruby grammar by contributor count and usage.

### Surface Model

The parser extracts a `GoDocumentSurface` — a pure data model carrying everything needed for materialization. No tree-sitter types escape the parser. Go's simpler structure means a flatter surface than Ruby's.

```
GoDocumentSurface
├── PackageName          — string (extracted from package clause)
├── Imports[]            — path, alias, category (stdlib/internal/external), span
├── Structs[]            — name, exported, span
│   └── Fields[]         — name, type, tag, is_embedded, exported, span
├── Interfaces[]         — name, exported, span
│   ├── Methods[]        — name, params, return_type, span
│   └── EmbeddedInterfaces[]  — type name (string)
├── Functions[]          — name, exported, params, return_type, span
├── Methods[]            — name, exported, receiver_name, receiver_type, is_pointer_receiver, params, return_type, span
├── Stats                — struct_count, interface_count, function_count, method_count, line_count
└── ErrorNodeCount       — tree-sitter error nodes (partial parse indicator)
```

**Key difference from Ruby:** Methods are top-level in the surface model, not nested inside types. Go methods belong to a package, not to a type declaration — they can appear in any file in the package. The `receiver_type` on each method associates it with its type. Materialization links them via `HAS_PART` edges using `receiver_type` matching.

**Not in Phase 1 surface model:** Type definitions, type aliases, constants, variables, compiler directives, test detection. These are additive — each extends the surface record and materializer without changing existing extraction. See Extension Points.

### Visibility

Go visibility is trivial compared to Ruby — no state machine, no modifiers, no scope changes.

```csharp
static bool IsExported(string name) => !string.IsNullOrEmpty(name) && char.IsUpper(name[0]);
```

Map to `accessibility = "public"` / `"private"` for universal view compatibility.

### Import Classification

Imports are classified by path pattern during extraction:

| Pattern | Category | Examples |
|---------|----------|---------|
| No dots in path | `stdlib` | `"fmt"`, `"net/http"`, `"encoding/json"` |
| Contains module path prefix | `internal` | `"github.com/myorg/myapp/pkg/auth"` |
| Everything else | `external` | `"github.com/gorilla/mux"` |

Precise classification requires `go.mod` context (to know the module path). Without it, the heuristic is: no dots = stdlib, otherwise external. When `go.mod` is indexed, a multi-file analyzer can reclassify internal imports.

Alias handling: `_` (blank import, side-effect only), `.` (dot import, unqualified access), named alias, or default (last path segment).

### Graph Materialization

State transfer via `GoDocumentState` in `DocumentModel.Metadata`, following the Ruby pattern.

#### What Earns Nodehood

Not everything extracted from Go source needs to be a node. The test: does it have its own span you'd navigate to? Does it have children? Does it participate in edges as a source or target? If none of those, it's a property or an edge.

| Concept | Representation | Rationale |
|---------|----------------|-----------|
| Package declaration | Property on document node (`package_name`) | One per file. No children, no structure, nothing to navigate to. Metadata about the file. |
| Import spec | `IMPORTS` edge from document | A relationship, not an entity. "This file imports that path." Same as Ruby's `REQUIRES` edges — no `rb.import` node exists. |
| Struct / interface | `go.type` node | Has children (fields, methods). Participates in shared `Types` view. Target of `IMPLEMENTS` and `EMBEDS` edges. Navigable. |
| Method / field | `go.member` node | Has spans, parameters, return types. Participates in shared `Functions` view. Navigable. Fields generate `EMBEDS` edges. |
| Top-level function | `go.function` node | Own span, parameters, return type. Navigable. Matches `rb.function`. |

**Nodes:**

| Kind | What | Key Props |
|------|------|-----------|
| `document` | Root node | `language`, `line_count`, `byte_size`, `package_name` |
| `go.type` | Struct or interface | `name`, `qualified_name`, `kind` ("struct"/"interface"), `accessibility`, `is_exported` |
| `go.member` | Method, field | `name`, `qualified_name`, `kind` ("method"/"field"), `declaring_type`, `accessibility`, `is_exported`, `receiver`, `receiver_type`, `is_pointer_receiver`, `parameters`, `return_type`, `signature`, `tag`, `field_type`, `is_embedded` |
| `go.function` | Top-level function | `name`, `kind` ("function"), `accessibility`, `is_exported`, `parameters`, `return_type`, `signature` |

Three node kinds (plus `document`). Package is a property. Imports are edges. This matches the Ruby model's economy — Ruby has four node kinds (`rb.type`, `rb.member`, `rb.function`, `rb.constant`) plus `document`, with `REQUIRES` as edges rather than nodes.

**Shared view participation:**
- `go.type` matches `WHERE kind LIKE '%.type'` — appears in the shared `Types` view automatically
- `go.member` and `go.function` need adding to the shared `Functions` view's kind list (`functions.sql`). Go's `kind: "method"` and `kind: "function"` values match the existing `$.kind` filter
- Standard property names (`name`, `qualified_name`, `kind`, `accessibility`, `declaring_type`, `is_static`, `parameters`, `return_type`, `signature`) match what shared views project
- `is_static` is always `false` for Go methods — Go has no static methods, only functions

**Qualified names:** `PackageName.TypeName` for types, `PackageName.TypeName.MethodName` for methods, `PackageName.FunctionName` for functions. Uses `.` separator (not `::` like Ruby) — matches Go convention.

**Composition hierarchy:** `document → type → member` via `HAS_PART` edges. Flatter than the original `document → package → type → member` — the package node added a join hop for no queryable benefit. Methods are attached to their receiver type via `declaring_type` matching, not by syntactic nesting. This is the key difference from Ruby — Ruby methods are syntactically inside their class; Go methods are syntactically alongside their type.

**Method-to-type attachment:** During materialization, methods are matched to their receiver type by `receiver_type` name within the same document. If no matching type node exists in the document (method for a type defined in another file), the method is attached directly to the document node. The `declaring_type` property always records the receiver type name regardless of attachment.

**Edges:**

| Type | From | To | Props |
|------|------|----|-------|
| `HAS_PART` | document / type | child nodes | `ordinal` (source order) |
| `IMPORTS` | document | (deferred) | `target` (import path), `alias`, `import_category` |
| `EMBEDS` | struct | (deferred) | `target` (embedded type name) |

**Struct fields:** Each named field in a struct becomes a `go.member` node with `kind: "field"`. Embedded (anonymous) fields generate both a `go.member` node with `is_embedded: true` and an `EMBEDS` edge from the struct to the embedded type. Embedded fields get their type name as their `name` property.

**Field tags:** Stored as the `tag` property on field nodes. The raw tag string (e.g., `` `json:"name" db:"column"` ``) is preserved — tag parsing into key-value pairs is a view concern, not a materialization concern.

**Deferred reference edges:** Same pattern as Ruby — `IMPORTS` and `EMBEDS` edges are created with `DstId = null` and target in props. Resolution happens during multi-file analysis.

**Spans:** 1-based lines, 0-based bytes. Created via `DocumentModel.LineMap.GetSpan(startByte, endByte)`, same as Ruby.

### X-Ray Summaries

**Headline:** Built in C# (no Liquid templates — following Ruby/PHP convention).

```
{filename} | code.go | {line_count} ln, ~{token_count} tok | pkg:{package} | {primary_declarations} | {key_names}
```

Examples:

```
user_service.go | code.go | 220 ln, ~1.2k tok | pkg:auth | UserService | Authenticate, Authorize, RevokeToken
handler.go | code.go | 45 ln, ~0.3k tok | pkg:http | interface Handler | ServeHTTP
main.go | code.go | 80 ln, ~0.5k tok | pkg:main | func main | cmd/api entry point
```

**Structure:** Indented outline with visibility symbols and receiver information.

```
user_service.go (code.go)
  package auth

  + type UserService struct    #symbol=UserService
    + field DB *sql.DB
    - field cache map[string]*User
  + func (*UserService) Authenticate(ctx context.Context, token string) (*User, error)    #symbol=Authenticate
  + func (*UserService) Authorize(ctx context.Context, user *User, action string) (bool, error)    #symbol=Authorize
  - func (*UserService) lookupCache(token string) *User    #symbol=lookupCache
  + func NewUserService(db *sql.DB, logger *slog.Logger) *UserService    #symbol=NewUserService
```

Visibility symbols: `+` exported, `-` unexported. The `#symbol=` anchors enable `read("file:///user_service.go#symbol=Authenticate")`.

### go.mod / go.work Parsing

Separate from tree-sitter. These files have a simple, well-specified format suited to line-scanning.

**go.mod elements:**

| Directive | Graph representation |
|-----------|---------------------|
| `module` path | Property on document node (`module_path`) |
| `go` version | Property on document node (`go_version`) |
| `require` (direct) | `DEPENDS_ON` edge from document, `indirect: false` |
| `require` (indirect, `// indirect`) | `DEPENDS_ON` edge from document, `indirect: true` |
| `replace` | `go.mod_replace` annotation |
| `retract` | Annotation (informational) |
| `toolchain` | Property on document node |

**go.work elements:**

| Directive | Graph representation |
|-----------|---------------------|
| `use` paths | Annotations listing workspace members |
| `go` version | Property on document node |
| `replace` | `go.mod_replace` annotation |

**Parser approach:** State machine with line scanning. Track `require ( ... )` blocks. Detect `// indirect` comments on require lines. No tree-sitter — the format is too simple to justify grammar-based parsing and isn't supported by tree-sitter-go anyway.

### SQL Views

Embedded resource `Schema/go_views.sql`, registered via `IFormatSchemaProvider`.

**Core views (Phase 1):**

| View | Purpose |
|------|---------|
| `go_types` | Structs and interfaces with kind, package, field count |
| `go_functions` | Top-level functions with signature, package |
| `go_methods` | Methods with receiver type, pointer vs value, declaring type |
| `go_imports` | Import paths with alias and category (from `IMPORTS` edges) |
| `go_fields` | Struct fields with type, tag, embedded flag |

**Extended views (Phase 2+):**

| View | Purpose |
|------|---------|
| `go_embeds` | Embedding relationships between structs |
| `go_constants` | Constants with type and value |
| `go_variables` | Package-level variables (sentinel errors, interface assertions) |
| `go_enum_blocks` | Const blocks with iota recognized as enum patterns |
| `go_tests` | Test/benchmark/example/fuzz functions with test_kind |
| `go_init_functions` | All init() functions across the codebase |
| `go_directives` | Build constraints, generate, embed, linkname |
| `go_implements` | Computed interface satisfaction (Phase 4) |
| `go_dependencies` | go.mod dependencies (Phase 3) |
| `go_replaces` | go.mod replace directives (Phase 3) |

All views query the frozen 5 tables filtered by `go.*` kinds. The `go_imports` view queries `IMPORTS` edges joined to document nodes — no `go.import` node kind exists.

### Error Handling

| Failure | Behavior |
|---------|----------|
| Tree-sitter parse produces ERROR nodes | Skip error regions, extract surrounding structure, log diagnostic |
| File can't be read | `PipelineResult.Error` with diagnostic |
| X-ray summary build fails | Log warning, continue with null headline/structure |
| Method receiver type not found in file | Attach method to document node, set `declaring_type` property |
| go.mod parse error | Log warning, partial extraction of what was parseable |
| Tree-sitter native library missing | Startup failure with clear diagnostic pointing to NuGet package |

Each extraction phase (package, imports, structs, interfaces, functions, methods) is independently try/caught. A malformed struct never prevents function extraction.

---

## Interface Satisfaction — Cross-File Analysis

This is the novel piece. Ruby has mixins (declared, syntactic). C# has `implements` (declared, syntactic). Go has implicit interfaces (computed, semantic). No existing RepoQL format needs this.

### Why It's Hard

Interface satisfaction requires cross-file reasoning:

1. A type's method set is spread across all files in its package
2. Struct embedding promotes methods from embedded types, recursively
3. `T` and `*T` have different method sets — `*T` includes pointer-receiver methods
4. Well-known interfaces (`error`, `io.Reader`) are defined outside the indexed codebase
5. The computation is O(types × interfaces × methods) — potentially expensive

### When It Runs

After the hot-path pipeline (parse → commit) drains, during multi-file analysis (idle processing). This is the existing `MultiFileAnalysisPipeline` — Go adds an analyzer to it.

### Computation Flow

```
1. COLLECT interface method sets
   For each go.type where kind='interface':
     method_set = direct methods + methods from embedded interfaces (recursive)

2. COLLECT type method sets
   For each go.type where kind='struct':
     value_methods = methods with value receiver on this type
     pointer_methods = methods with pointer receiver on this type
     For each EMBEDS edge (recursive):
       promoted = embedded type's method set (excluding shadowed names)
       value_methods += promoted value methods
       pointer_methods += promoted pointer methods
     T_method_set = value_methods
     *T_method_set = value_methods + pointer_methods

3. CHECK satisfaction
   For each (type, interface) pair:
     if T_method_set ⊇ interface_method_set:
       emit IMPLEMENTS edge (receiver_kind='value')
     elif *T_method_set ⊇ interface_method_set:
       emit IMPLEMENTS edge (receiver_kind='pointer')

4. CHECK well-known stdlib interfaces
   Built-in definitions for: error (Error() string),
   fmt.Stringer (String() string), io.Reader (Read([]byte) (int, error)),
   io.Writer (Write([]byte) (int, error)), sort.Interface (Len, Less, Swap)
   These are checked even when not imported.
```

### IMPLEMENTS Edge

| Property | Value |
|----------|-------|
| `SrcId` | Type node ID |
| `DstId` | Interface node ID (null for stdlib interfaces) |
| `Type` | `"IMPLEMENTS"` |
| `Props.target` | Interface qualified name |
| `Props.receiver_kind` | `"value"` or `"pointer"` |
| `Props.is_stdlib` | `"true"` for well-known interfaces not in graph |

### Embedding Chain Resolution

Embedding chains must be resolved before interface satisfaction can be computed. Cycles (struct A embeds B, B embeds A) are detected and broken — Go rejects them at compile time, but malformed files might produce them.

```
type Logger struct { ... }
func (l Logger) Log(msg string) { ... }

type Service struct {
    Logger           // embeds Logger — promotes Log()
    sync.Mutex       // embeds Mutex — promotes Lock(), Unlock()
}
// Service.Log() works. *Service satisfies sync.Locker.
```

### Performance Considerations

For a large Go codebase (2,000 files, 500 types, 50 interfaces):
- Method set collection: O(types × files per package) — bounded by package size
- Embedding resolution: O(embedding depth × types) — typically shallow (1-2 levels)
- Satisfaction check: O(types × interfaces × avg_methods_per_interface) — ~500 × 50 × 3 = 75,000 comparisons

This is fast enough to not need optimization. If codebases with thousands of interfaces appear, the check can be pruned by only testing types whose method count ≥ interface method count.

### Scope

Phase 4 (idle processing). Depends on Phase 1 (core structure) and Phase 2 (struct fields and embedding). The multi-file analysis flow already exists — Go adds a `GoInterfaceSatisfactionAnalyzer` to the pipeline.

---

## Cross-Cutting Concerns

**URI addressing:** Go files use `file:///path#symbol=TypeName.MethodName` for symbol navigation. Package-qualified names use `.` separator.

**Methods across files:** A type's methods can appear in any file in the same package. Each file produces its own nodes — a method node in file A with `declaring_type = "UserService"` and a struct node in file B with `name = "UserService"`. The `go_methods` view joins on `declaring_type` to reunify. This is strictly better than Ruby's open class problem — Go types can't be reopened (no fields or methods added from outside the package), only methods can be defined in separate files.

**Search integration:** `Artifact.Text` contains the source code and participates in semantic search. Node headlines and structure text make types, functions, and methods discoverable via explore.

**No metaprogramming:** Go has no dynamic method generation. No `define_method`, no `method_missing`, no `eval`. The graph is complete for what the parser can see. No honesty annotations needed for missing structure. Code generation (`//go:generate`) is noted as an annotation, but generated files are indexed normally as regular `.go` files.

**Shared Functions view:** `go.member` with `kind: "method"` and `go.function` with `kind: "function"` need to be added to the hardcoded kind list in `functions.sql`. This is the same integration point as Ruby (`rb.member`, `rb.function`).

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| TreeSitter.DotNet | ANTLR4 | Already a dependency (Ruby). Go grammar bundled. Excellent error tolerance. Lower implementation effort — follow proven pattern |
| TreeSitter.DotNet | Native Go toolchain (asty) | No external runtime dependency. In-process, no IPC. Cross-platform without Go installed |
| Normalized node kinds (`go.type`, `go.member`, `go.function`) | Fine-grained kinds (`go.struct`, `go.interface`, `go.method`, `go.field`) | Matches shared view conventions (`csharp.type`, `rb.type`). `kind` prop distinguishes subtypes. One node kind, queried with filters |
| Package as document property | `go.package` node | One per file. No children, no structure. Adding a node creates a join hop in every query for zero benefit. Package name is metadata about the file |
| Imports as edges | `go.import` nodes | Imports are relationships, not entities. Nothing to navigate to, no children. Same as Ruby's `REQUIRES` edges — no `rb.import` node exists |
| Methods as top-level surface entries | Methods nested inside struct surface | Go methods are syntactically top-level, associated by receiver. Surface model reflects the language |
| Syntax-level interface satisfaction | `go/types` semantic analysis | No Go runtime required. Syntax matching handles >95% of cases. Semantic analysis (cross-package type resolution) would require Go installed — violates constraint. Research gap on the accuracy delta |
| Line-scanning for go.mod | Tree-sitter or Pidgin | Format is too simple. ~50 lines of state machine code vs grammar dependency. go.mod isn't a tree-sitter-go grammar target |
| Per-file method attachment, view-level reunification | Merge methods into type nodes at parse time | Can't merge at parse time — method's file may be parsed before type's file. Views join by `declaring_type`. Same as how Ruby handles open classes |

## Alternatives Considered

**ANTLR4:** Also already a dependency (PHP). Viable — Go grammar exists in grammars-v4 with generics support. Rejected: tree-sitter's error tolerance is better for incomplete files, and the Ruby pattern is already proven. ANTLR would work but offers no advantage over tree-sitter for Go.

**Native Go toolchain (asty subprocess):** Perfect fidelity — it's Go's own parser. Rejected: adds Go runtime as a deployment dependency. The TypeScript loader uses a similar subprocess pattern (Node.js), but Go's syntax is simple enough that tree-sitter captures everything we need. The fidelity gap between tree-sitter and `go/parser` is minimal for structural extraction.

**`go/types` for interface satisfaction:** Would give 100% correct interface satisfaction, including cross-package resolution. Rejected: requires Go runtime. Syntax-level matching (same-package, name-based method set comparison) handles the vast majority of cases. Cross-package interface satisfaction (type in package A satisfies interface in package B where both are indexed) works by name matching. The gap is unresolved: types satisfying interfaces via method signatures that reference types from third packages cannot be verified by syntax alone.

**gopls (LSP):** Heavyweight, requires Go runtime, designed for editor integration. Not suitable as a batch parser.

**Fine-grained node kinds:** `go.struct`, `go.interface`, `go.method`, `go.field`, `go.function`, `go.constant`, `go.variable`. More expressive but fragments the SQL surface. Agents would need to know all Go-specific kinds. Rejected: the normalized pattern (`go.type` + `kind` prop) is proven across C#, PHP, and Ruby. Go-specific views can expose the detail.

**Package and import as nodes:** The original design had `go.package` and `go.import` as node kinds. Package is metadata about the file (one per file, no children, no structure) — a property on the document node. Imports are relationships (file imports path) — edges from document with path/alias/category in props. Neither has its own span you'd navigate to, neither has children, neither participates in edges as a target. Demoting them eliminates a join hop (package) and redundant representation (import node + import edge).

## Risks

| Risk | Mitigation |
|------|------------|
| TreeSitter.DotNet single maintainer | Grammar is official tree-sitter-go (400+ commits, used by GitHub). If NuGet wrapper abandoned, grammar and native libraries can be repackaged |
| tree-sitter-go grammar lags behind Go spec | Grammar tracks Go releases. Generics added in v0.20.0. Monitor for range-over-func (Go 1.22), generic type aliases (Go 1.24) |
| Syntax-level interface satisfaction misses cross-package cases | Name-based matching works within the indexed codebase. Well-known stdlib interfaces hardcoded. Gap documented. `go/types` integration is a future extension point |
| Embedding chain resolution has edge cases | Cycle detection breaks infinite loops. Shadowing rules (method defined on outer type shadows promoted method of same name) implemented per Go spec |
| Method-to-type attachment across files is incomplete at parse time | By design — each file is parsed independently. View-level reunification via `declaring_type` join. Multi-file analysis can emit cross-file `HAS_PART` edges if needed |
| go.mod parser doesn't handle all directives | Go modules spec is well-documented. Start with `module`, `go`, `require`, `replace`. `retract`, `toolchain`, `exclude` are additive |
| Large Go codebases slow interface satisfaction | O(types × interfaces × methods). 500 × 50 × 3 = 75K comparisons — fast. Prune by method count if needed |

## Extension Points

- **Type definitions and aliases:** `type UserID int64`, `type Strings = []string`. Add `kind: "type_definition"` and `kind: "type_alias"` to `go.type` nodes
- **Constants and iota/enum detection:** Scan const blocks for named type + `iota`. Emit `go.enum_block` annotations. Constants as `go.member` with `kind: "constant"`
- **Package-level variables:** Sentinel errors (`var ErrNotFound = errors.New(...)`), interface assertions (`var _ Handler = (*Server)(nil)`)
- **Compiler directives:** `//go:build`, `//go:embed`, `//go:generate`, `//go:linkname` — extracted from comments, emitted as annotations
- **Test function detection:** Pattern-match `TestXxx`, `BenchmarkXxx`, `ExampleXxx`, `FuzzXxx` in `_test.go` files. Emit `go.test` annotations
- **Init function detection:** Find all `init()` functions, link blank imports to their triggered init functions
- **Concurrency markers:** `go` statement sites, channel declarations, `select` statements — annotations for concurrency analysis
- **Generics:** Type parameters on structs, interfaces, and functions. `type_params` property. Constraint details
- **CGo detection:** `import "C"` marks FFI boundaries — annotation
- **`go/types` integration:** Shell out to a Go tool for semantic interface satisfaction if syntax-level matching proves insufficient

---

## Project Structure

```
src/Formats/RepoQL.Formats.Go/
    GoLoader.cs                            # IFormatLoader + IFormatMaterializer + IFormatSchemaProvider
    GoClassifier.cs                        # IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
    GoParser.cs                            # IAsyncPipeline<IClassifiedArtifact, Records?>
    GoDocumentState.cs                     # State transfer between Load and Materialize
    GoConstants.cs                         # Node kinds, edge types, property keys
    GoMediaTypes.cs                        # Extension → SemanticMediaType mapping
    Surface/
        GoDocumentSurface.cs               # Root surface model
        GoByteRange.cs                     # Byte offset pair
        GoImportInfo.cs                    # Import with path, alias, category
        GoStructInfo.cs                    # Struct with fields
        GoFieldInfo.cs                     # Struct field with type, tag, embedded flag
        GoInterfaceInfo.cs                 # Interface with methods and embedded interfaces
        GoInterfaceMethodInfo.cs           # Interface method spec
        GoFunctionInfo.cs                  # Top-level function
        GoMethodInfo.cs                    # Method with receiver
        GoParseStats.cs                    # Parse statistics
        GoQueryCapture.cs                  # Tree-sitter query capture
        GoQueryCaptureGroup.cs             # Grouped captures from a query match
    TreeSitter/
        GoTreeSitterClient.cs              # Tree-sitter wrapper (contains all native interop)
        GoQueries.cs                       # S-expression query strings
    GoMod/
        GoModParser.cs                     # Line-scanning parser for go.mod / go.work
        GoModInfo.cs                       # Module path, deps, replaces
    Schema/
        go_views.sql                       # Embedded resource, format-specific views
    GoServiceCollectionExtensions.cs       # DI registration
    RepoQL.Formats.Go.csproj               # References: TreeSitter.DotNet, RepoQL.Contracts, RepoQL.Indexing

src/tests/RepoQL.Formats.Go.Tests/
    GoTreeSitterClientTests.cs             # Parser extraction correctness
    GoLoaderTests.cs                       # Load + Materialize round-trip
    GoModParserTests.cs                    # go.mod parsing
    GoInterfaceDetectionTests.cs           # Interface satisfaction computation
    GoEnumPatternTests.cs                  # Iota/enum const block detection
    GoEmbeddingTests.cs                    # Struct embedding and method promotion
    GoDirectiveTests.cs                    # Compiler directive extraction
    GoTestDetectionTests.cs                # Test/benchmark/example function detection
    Fixtures/
        simple_struct.go                   # Struct with methods, fields
        interfaces.go                      # Interface declarations
        functions.go                       # Top-level functions
        imports.go                         # Import styles (single, grouped, blank, dot, alias)
        embedding.go                       # Struct embedding chains
        enum_pattern.go                    # Const block with iota
        test_file_test.go                  # Test/benchmark/example functions
        malformed.go                       # Syntax errors for error tolerance
        go.mod                             # Module metadata
    RepoQL.Formats.Go.Tests.csproj         # References: TUnit, AwesomeAssertions, FakeItEasy
```

### Files to Modify (Existing)

| File | Change |
|------|--------|
| `src/RepoQL.Data.DuckDB/Schema/Views/functions.sql` | Add `'go.member', 'go.function'` to WHERE IN clause |
| `src/RepoQL.Core/RepoIndexerServiceCollectionExtensions.cs` | Add `services.AddGoFormat();` |
| `RepoQL.sln` | Add new projects |

---

*Parse the tree. Compute the interfaces. Let SQL reunify what Go spreads across files.*
