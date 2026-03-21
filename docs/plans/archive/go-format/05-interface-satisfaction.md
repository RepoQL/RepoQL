---
description: Plan for Go format — cross-file interface satisfaction computation, method set analysis, embedding chain resolution, IMPLEMENTS edges, and well-known stdlib interfaces
tags: [format, go, golang, plan, interfaces, multi-file, idle-processing]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Go — Interface Satisfaction

Implements: [Go Format Design](../../designs/future/go-format.md) — Interface Satisfaction — Cross-File Analysis, SQL Views (go_implements)

## Scope

**Covers:**
- `GoInterfaceSatisfactionAnalyzer` — multi-file analyzer registered in the idle processing pipeline
- Interface method set collection (direct methods + embedded interface methods, recursive)
- Type method set collection (direct methods + promoted methods from embeddings, recursive)
- `T` vs `*T` method set distinction — pointer receivers expand only the pointer method set
- Satisfaction check: type method set ⊇ interface method set
- `IMPLEMENTS` edge emission with receiver kind (value/pointer)
- Well-known stdlib interface definitions (error, fmt.Stringer, io.Reader, io.Writer, sort.Interface)
- Embedding chain resolution with cycle detection
- Method shadowing (outer type method shadows promoted method of same name)
- `go_implements` SQL view
- Tests: satisfaction computation, embedding promotion, pointer vs value, shadowing, cycles, stdlib interfaces

**Does not cover:**
- Cross-package type resolution via `go/types` (extension point — requires Go runtime)
- Generic interface satisfaction (extension point — requires type parameter resolution)
- Interface satisfaction for types defined via type definitions (`type MyHandler = Handler` — the alias satisfies the same interfaces as the underlying type, but computing this requires alias resolution)

## Enables

Once this exists:
- **"What implements Handler?"** — `SELECT * FROM go_implements WHERE interface_name = 'Handler'` answers across the codebase
- **"Does Server satisfy io.Writer?"** — `SELECT * FROM go_implements WHERE type_name = 'Server' AND interface_name = 'Writer'`
- **Stdlib interface satisfaction** — types satisfying `error`, `fmt.Stringer`, `io.Reader`, `io.Writer` are discoverable without those interfaces being in the indexed codebase
- **Pointer vs value distinction** — `SELECT * FROM go_implements WHERE receiver_kind = 'pointer'` shows types that only satisfy interfaces via pointer receiver
- **Architectural queries** — `SELECT interface_name, COUNT(*) FROM go_implements GROUP BY 1 ORDER BY 2 DESC` shows the most-implemented interfaces

## Prerequisites

- Plan 02 complete — `go.type` and `go.member` nodes exist with `kind`, `declaring_type`, `receiver_type`, `is_pointer_receiver` properties
- Plan 02 complete — `EMBEDS` edges exist between structs and embedded types
- Plan 03 beneficial but not required — if Plan 03 is complete, interface method specs within interface declarations are fully extracted. If not, interface methods are still available from Plan 01's surface model (they're part of the core extraction). The difference: Plan 03 adds embedded interface resolution within interface declarations, which improves accuracy for interfaces that embed other interfaces

## North Star

An agent asks "what types implement the Handler interface?" and gets the complete answer — every struct whose method set (including promoted methods from embeddings) satisfies the interface. The agent didn't have to read any files. The agent didn't have to manually compare method lists. The graph computed the relationship that Go's type system enforces implicitly.

## Done Criteria

### Analyzer Registration
- `GoInterfaceSatisfactionAnalyzer` shall implement the multi-file analyzer interface
- The analyzer shall be registered in the `MultiFileAnalysisPipeline` via `GoServiceCollectionExtensions`
- The analyzer shall run during idle processing, after the hot-path pipeline drains

### Interface Method Set Collection
- For each `go.type` node with `kind: "interface"`, the analyzer shall collect the complete method set
- The method set shall include direct methods (interface method specs from `go.member` nodes with `declaring_type` matching the interface)
- The method set shall include methods from embedded interfaces, resolved recursively
- Embedded interface resolution shall use name matching: `go.type` nodes with matching `name` or `qualified_name` within the same package scope
- When an embedded interface cannot be resolved (defined in another package), it shall be skipped with a diagnostic — the interface's method set is incomplete, and satisfaction checks using it may produce false negatives
- Method identity for satisfaction checking shall be: method name + parameter count. Full signature matching (parameter types, return types) is an extension point requiring type resolution

### Type Method Set Collection
- For each `go.type` node with `kind: "struct"`, the analyzer shall collect two method sets: `T` (value receiver) and `*T` (pointer receiver)
- `T` method set: all methods with value receiver (`is_pointer_receiver: "false"`) on this type
- `*T` method set: all methods (both value and pointer receiver) on this type
- Methods shall be collected by `declaring_type` matching across all documents in the scope — a method defined in file A with `declaring_type: "Server"` belongs to the type defined in file B with `name: "Server"` in the same package

### Embedding Promotion
- For each `EMBEDS` edge from a struct, the analyzer shall add the embedded type's method set to the struct's method set
- Promotion follows Go rules: value methods of the embedded type are promoted to both `T` and `*T`; pointer methods of the embedded type are promoted only to `*T`
- Promotion is recursive — if struct A embeds struct B which embeds struct C, A gets C's methods too
- **Shadowing:** when the outer type has a method with the same name as a promoted method, the promoted method is shadowed (not included in the method set)

### Cycle Detection
- Embedding chains shall be checked for cycles before resolution
- If struct A embeds B and B embeds A (directly or transitively), the cycle shall be broken and a diagnostic logged
- Go rejects embedding cycles at compile time, but malformed or partially-parsed files might produce them

### Satisfaction Check
- For each (type, interface) pair where the type's method set count ≥ interface's method set count:
  - If `T` method set ⊇ interface method set → emit IMPLEMENTS edge with `receiver_kind: "value"`
  - Else if `*T` method set ⊇ interface method set → emit IMPLEMENTS edge with `receiver_kind: "pointer"`
- Method set comparison is by method name (and optionally parameter count when available)
- The check shall only compare types and interfaces within the same package, plus well-known stdlib interfaces

### Well-Known Stdlib Interfaces
- The analyzer shall include built-in definitions for interfaces not in the indexed codebase:
  - `error`: `Error() string`
  - `fmt.Stringer`: `String() string`
  - `io.Reader`: `Read(p []byte) (n int, err error)`
  - `io.Writer`: `Write(p []byte) (n int, err error)`
  - `io.Closer`: `Close() error`
  - `sort.Interface`: `Len() int`, `Less(i, j int) bool`, `Swap(i, j int)`
- These are checked against all types regardless of whether the package imports them
- IMPLEMENTS edges for stdlib interfaces shall have `is_stdlib: "true"` and `DstId = null`

### IMPLEMENTS Edge
- `SrcId` shall be the type's node ID
- `DstId` shall be the interface's node ID (null for stdlib interfaces not in graph)
- `Type` shall be `"IMPLEMENTS"`
- Props shall include: `target` (interface qualified name), `receiver_kind` ("value" or "pointer"), `is_stdlib` ("true"/"false")

### SQL View
- `go_implements` shall show: type_uri, type_name, type_qualified_name, interface_uri, interface_name, interface_qualified_name, receiver_kind, is_stdlib, package_name
- `go_implements` shall query IMPLEMENTS edges joined to source (type) and destination (interface) nodes
- For stdlib interfaces (DstId is null), interface_uri shall be null and interface_name shall come from edge props
- The view shall be added to the existing `go_views.sql` embedded resource

### Performance
- The analyzer shall skip the satisfaction check for types with fewer methods than the interface requires
- For the expected scale (500 types, 50 interfaces, avg 3 methods per interface), no additional optimization is required
- If the computation exceeds 5 seconds for a scope, a warning shall be logged

### Tests
- A struct with methods matching an interface's method set shall produce an IMPLEMENTS edge
- A struct missing one method from an interface shall NOT produce an IMPLEMENTS edge
- A struct satisfying an interface only via pointer receiver shall have `receiver_kind: "pointer"`
- A struct satisfying an interface via value receiver shall have `receiver_kind: "value"`
- A struct embedding another struct shall inherit the embedded struct's methods for satisfaction
- A shadowed method (same name on outer type) shall replace the promoted method
- Embedding cycles shall be detected and broken without crashing
- Stdlib interfaces (error, fmt.Stringer) shall be checked against types
- A type with `Error() string` method shall satisfy the `error` interface
- Methods defined across multiple files shall be unified by `declaring_type` for satisfaction checking

## Constraints

- **Name-based matching only** — satisfaction is checked by method name (and parameter count when available), not by full type signature. This handles >95% of real-world cases but cannot verify parameter/return type compatibility across packages. Full type-level matching would require `go/types` or equivalent
- **Same-package scope** — type-to-interface satisfaction is computed within the same package. Cross-package satisfaction (type in package A satisfies interface in package B) is checked by name only and may miss cases where method signatures reference types from yet another package
- **No database queries during hot path** — the analyzer runs in multi-file analysis (idle processing) and may query the data store. It does NOT run during per-file materialization
- **Idempotent** — the analyzer must handle being re-run when files change. IMPLEMENTS edges from previous runs are replaced, not accumulated
- **Incremental scope** — the analyzer processes one item at a time (per the multi-file analysis pipeline). For interface satisfaction, the scope is the types and interfaces reachable from the item's file/package

## References

- [Go Format Design](../../designs/future/go-format.md) — Interface Satisfaction — Cross-File Analysis section
- [Go North Star](../../north-star/formats/go.md) — interface graph queries
- [Multi-File Analysis Flow](../../flows/current/indexing/multi-file-analysis.md) — pipeline that runs this analyzer
- [Go Specification: Method Sets](https://go.dev/ref/spec#Method_sets) — official rules for T vs *T method sets
- [Go Specification: Interface Types](https://go.dev/ref/spec#Interface_types) — interface embedding and satisfaction rules
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

| Failure | Behavior |
|---------|----------|
| Embedded interface not found in graph | Skip embedded methods, log diagnostic. Satisfaction check may produce false negatives |
| Embedded struct not found in graph | Skip promoted methods, log diagnostic |
| Embedding cycle detected | Break cycle, log warning, process remaining chain |
| Method set comparison fails | Skip type-interface pair, log diagnostic |
| Analyzer exceeds time budget | Log warning, return partial results (edges emitted so far are valid) |
| Type has methods across files but some files not yet indexed | Partial method set used — re-run on next analysis cycle will pick up new methods |
