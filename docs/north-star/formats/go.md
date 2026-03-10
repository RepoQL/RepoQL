---
description: What great Go format support looks like - declarations for querying Go structure through the knowledge graph
tags: [go, golang, format, north-star, vision]
audience: { human: 50, agent: 50 }
purpose: { north-star: 100 }
---

# Go Format Support: What Great Looks Like

> An agent should be able to understand a Go codebase's structure — packages, types, interfaces, methods, and their relationships — without reading source files, and query it all through the same SQL surface as every other format.

An agent lands in a Go monorepo with 2,000 files across 150 packages. It doesn't open any. It scans headlines and sees: a `UserService` struct with 8 exported methods and 3 unexported helpers, an `Authenticator` interface requiring two methods, a `cmd/api` entry point importing 12 internal packages. It asks "what implements Handler?" and gets every struct in the codebase whose method set satisfies the interface — even though none of them declare `implements`. It asks "what does the Order struct embed?" and sees three embedded types with their promoted methods. It traces from `cmd/api/main.go` through the import graph to find every package the binary depends on. Go's implicit interfaces didn't hide relationships. The graph computed them.

---

## Discovery

- An agent should be able to distinguish file roles from a headline alone — entry point, library, test, benchmark, example, generated code
- An agent should be able to see what a file declares and its package without opening it
- An agent should be able to tell a struct from an interface from a type alias from a standalone function file from the headline
- An agent should be able to see key exported names in the headline — actual names, not just counts
- An agent should be able to see build constraints on a file without opening it — platform-specific files identified from the headline

```
headline  →  "user_service.go | code.go | 220 ln, ~1.2k tok | pkg:auth | UserService | Authenticate, Authorize, RevokeToken"
headline  →  "handler.go | code.go | 45 ln, ~0.3k tok | pkg:http | interface Handler | ServeHTTP"
headline  →  "main.go | code.go | 80 ln, ~0.5k tok | pkg:main | func main | cmd/api entry point"
headline  →  "user_service_test.go | code.go.test | 180 ln, ~1.0k tok | pkg:auth_test | TestAuthenticate, TestAuthorize, BenchmarkTokenValidation"
headline  →  "signal_linux.go | code.go | 40 ln, ~0.2k tok | pkg:os | //go:build linux | handleSignal"
```

---

## Packages and Imports

- An agent should be able to see every package in the module and its directory path
- An agent should be able to find all files belonging to a given package
- An agent should be able to trace the import graph — what each package depends on
- An agent should be able to find all consumers of a package — everything that imports it
- An agent should be able to distinguish between standard library, internal, and external imports
- An agent should be able to see import aliases, dot imports, and blank imports (`_`) with their structural role — blank imports trigger `init()` side effects
- An agent should be able to detect circular import attempts as diagnostics — Go rejects them at compile time

```sql
-- What packages import the auth package?
SELECT DISTINCT source_package
FROM go_imports
WHERE target_path LIKE '%/auth'

-- Trace the full import tree of a binary
WITH RECURSIVE deps AS (
  SELECT target_path FROM go_imports WHERE source_path = 'cmd/api'
  UNION
  SELECT i.target_path FROM go_imports i JOIN deps d ON i.source_path = d.target_path
)
SELECT * FROM deps

-- Go types through the universal surface
SELECT name, file_uri FROM Types WHERE lang = 'go' AND kind = 'interface'
```

---

## The Interface Graph

- An agent should be able to find every type that implements a given interface — computed from method sets, not from declarations
- An agent should be able to ask "what interfaces does this type satisfy?" and get the complete list
- An agent should be able to see the distinct method sets of `T` and `*T` and which interfaces each satisfies independently — `*T` may implement an interface that `T` does not, when pointer receivers are involved
- An agent should be able to trace how struct embedding changes which interfaces a type satisfies
- An agent should be able to find interfaces with no implementations and types that implement many interfaces
- An agent should be able to trust that interface satisfaction is computed across the entire indexed codebase, not limited to a single package or file
- An agent should be able to query which types satisfy well-known standard library interfaces (`io.Reader`, `io.Writer`, `fmt.Stringer`, `error`) even when those interfaces are not defined in the indexed code
- An agent should be able to ask "why doesn't this type implement this interface?" and see which methods are missing or have mismatched signatures
- An agent should be able to recognize compile-time interface assertion patterns (`var _ Interface = (*Type)(nil)`) as supplementary evidence for interface relationships

Go's implicit interfaces are the most powerful structural relationship in the language. A type satisfies an interface by having the right methods — no annotation, no keyword, no declaration. If the graph can't answer "what implements io.Reader?" across the entire codebase in one query, the most fundamental Go question goes unanswered. Equally important: when a type *doesn't* satisfy an interface, an agent should see why — missing method, wrong signature, pointer/value mismatch.

```sql
-- What implements Handler?
SELECT type_name, file_uri
FROM go_implements
WHERE interface_name = 'Handler'

-- What interfaces does UserService satisfy?
SELECT interface_name, interface_package
FROM go_implements
WHERE type_name = 'UserService'
```

---

## Struct Embedding and Composition

- An agent should be able to see what types a struct embeds
- An agent should be able to see which methods are promoted from embedded types
- An agent should be able to trace the full embedding chain — struct embeds struct embeds struct
- An agent should be able to distinguish between a method defined on a type and a method promoted from an embedded type
- An agent should be able to understand how embedding affects interface satisfaction — embedded methods count toward the embedding type's method set

Embedding is Go's composition mechanism. It replaces inheritance, trait mixing, and delegation with a single concept: embed a type, get its methods. An agent that can trace the embedding graph understands a dimension of structure that method lists alone would miss.

```sql
-- What does Order embed?
SELECT embedded_type, embedded_package
FROM go_embeds
WHERE embedding_type = 'Order'

-- What methods does Order gain from embedding?
SELECT name, signature, source_type AS promoted_from
FROM go_methods
WHERE declaring_type = 'Order' AND is_promoted = true
```

---

## Methods and Receivers

- An agent should be able to see all methods on a type, including their receiver type (value or pointer)
- An agent should be able to see full method signatures — parameters with types, return types, variadic indicators
- An agent should be able to find all methods with pointer receivers versus value receivers on a given type
- An agent should be able to see that methods on a type may be defined across multiple files in the same package
- An agent should be able to find constructors by convention — `NewFoo()` functions that return the type
- An agent should be able to distinguish between methods (with receiver) and standalone functions (without)

```
structure →
  user_service.go (code.go)
    package auth

    + type UserService struct
      embedded: BaseService, sync.Mutex
      + field DB *sql.DB
      + field Logger *slog.Logger
      - field cache map[string]*User
    + func (*UserService) Authenticate(ctx context.Context, token string) (*User, error)    #symbol=Authenticate
    + func (*UserService) Authorize(ctx context.Context, user *User, action string) (bool, error)  #symbol=Authorize
    - func (*UserService) lookupCache(token string) *User                                   #symbol=lookupCache
    + func NewUserService(db *sql.DB, logger *slog.Logger) *UserService                     #symbol=NewUserService
```

---

## Init Functions and Side Effects

- An agent should be able to find all `init()` functions across the codebase
- An agent should be able to see that a package may have multiple `init()` functions, even across files
- An agent should be able to trace the init execution order — determined by import dependency, then file order, then declaration order
- An agent should be able to link blank imports (`import _ "pkg"`) to the `init()` functions they trigger — these are side-effect-only imports
- An agent should be able to find packages with `init()` functions that have no other exported symbols — pure side-effect packages

`init()` functions are Go's package initialization mechanism. They run automatically, can't be called directly, and their execution order is determined by the import graph. They are a common source of subtle startup behavior that agents need to trace.

---

## The Error Interface

- An agent should be able to find all types that implement the `error` interface (`Error() string`)
- An agent should be able to find sentinel error values — package-level `var` declarations of type `error`
- An agent should be able to distinguish custom error types (structs implementing `error`) from sentinel errors (`var ErrNotFound = errors.New(...)`)
- An agent should be able to find error wrapping patterns where one error type contains another

`error` is Go's most pervasive interface — a single method, satisfied by hundreds of types in any real codebase. Finding all error types, understanding the error hierarchy, and tracing sentinel errors are among the most common structural questions.

```sql
-- All custom error types
SELECT type_name, file_uri
FROM go_implements
WHERE interface_name = 'error'

-- All sentinel errors
SELECT name, file_uri
FROM go_variables
WHERE type = 'error' AND exported = true
```

---

## Concurrency

- An agent should be able to find functions that launch goroutines — functions containing `go` statements
- An agent should be able to find channel declarations and see their direction (send-only, receive-only, bidirectional)
- An agent should be able to find `select` statements and the channels they coordinate
- An agent should be able to find usage of synchronization primitives (`sync.Mutex`, `sync.WaitGroup`, `sync.Once`, `sync.Map`)

Concurrency is Go's defining runtime feature. An agent debugging a race condition or understanding a service's concurrency model needs to find goroutine launch sites, channel usage, and synchronization points from the graph.

---

## Package-Level Variables

- An agent should be able to find all package-level variable declarations with their types
- An agent should be able to find sentinel errors and other well-known variable patterns
- An agent should be able to recognize compile-time interface assertion patterns (`var _ Interface = (*Type)(nil)`)
- An agent should be able to distinguish between exported and unexported package-level state

---

## Types and Visibility

- An agent should be able to query types by their kind — struct, interface, type definition, type alias
- An agent should be able to filter by exported versus unexported — the single visibility dimension in Go
- An agent should be able to see type parameters and constraints on generic types and functions
- An agent should be able to see constraint details — type sets with `~T` approximation elements and `T1 | T2` union elements
- An agent should be able to find all generic types and functions in the codebase
- An agent should be able to find all concrete types underlying a type definition (`type UserID int64`)
- An agent should be able to find all type aliases and what they alias

```sql
-- All exported interfaces in the module
SELECT name, package, file_uri
FROM go_types
WHERE kind = 'interface' AND exported = true

-- All generic types with their constraints
SELECT name, type_params
FROM go_types
WHERE type_params IS NOT NULL
```

---

## Constants and the Enum Pattern

- An agent should be able to find all constants and their values
- An agent should be able to recognize `const` blocks with `iota` as enum-like patterns
- An agent should be able to see the named type backing an enum-like const block
- An agent should be able to query all members of an enum-like group in one query
- An agent should be able to find where enum values are used without knowing the pattern is an enum

Go has no `enum` keyword. The convention — `const` block + named type + `iota` — creates enum-like values that are invisible to tools that only look for keywords. An agent should see the pattern, not just the syntax.

```sql
-- All enum-like const groups
SELECT type_name, member_count, members
FROM go_enum_blocks

-- All constants of a given type
SELECT name, value
FROM go_constants
WHERE type = 'OrderStatus'
```

---

## Module Metadata

- An agent should be able to see the module path, Go version, and dependency list from `go.mod`
- An agent should be able to distinguish direct from indirect dependencies
- An agent should be able to see replace directives and their local or versioned targets
- An agent should be able to see workspace membership from `go.work` files
- An agent should be able to trace which packages come from which dependency

```sql
-- All direct dependencies
SELECT path, version
FROM go_dependencies
WHERE indirect = false

-- Replace directives (local development overrides)
SELECT original, replacement
FROM go_replaces
```

---

## Compiler Directives

- An agent should be able to find all `//go:build` constraints and see which platforms a file targets
- An agent should be able to find all `//go:generate` directives and see the command each one invokes
- An agent should be able to find all `//go:embed` directives and see what files are embedded into binaries
- An agent should be able to find all `//go:linkname` directives — rare but important cross-package invisible dependencies

Compiler directives look like comments but carry semantic weight. `//go:embed templates/*` creates a file dependency. `//go:build !windows` means the file doesn't exist on Windows. An agent should be able to query these as structured data, not search through comment text.

---

## Testing

- An agent should be able to find all test functions and see what they test — linked by naming convention (`TestAuthenticate` → `Authenticate`)
- An agent should be able to distinguish test kinds: unit tests, benchmarks, examples, fuzz tests
- An agent should be able to find `TestMain` functions that control test setup/teardown per package
- An agent should be able to see example functions and their expected output comments
- An agent should be able to distinguish white-box tests (`package foo`) from black-box tests (`package foo_test`)
- An agent should be able to find all benchmarks in the codebase in one query
- An agent should be able to recognize table-driven test functions as covering multiple cases, even when individual cases are runtime data

```sql
-- All benchmarks
SELECT name, package, file_uri
FROM go_tests
WHERE test_kind = 'benchmark'

-- Tests for a specific function
SELECT name, file_uri
FROM go_tests
WHERE tests_symbol = 'Authenticate'
```

---

## Project Structure

- An agent should be able to identify entry points from `cmd/` directories
- An agent should be able to see `internal/` import restrictions — which packages are accessible only within the module
- An agent should be able to find platform-specific files by build constraint or naming convention
- An agent should be able to exclude `testdata/` and `vendor/` directories from queries by default, and include them explicitly when needed
- An agent should be able to find files that use CGo (`import "C"`) and see them as FFI boundaries
- An agent should be able to see the conventional project layout — `cmd/`, `internal/`, `pkg/` — reflected in the graph

---

## Integrity

- An agent should be able to find files with parse errors and see what was recoverable
- An agent should be able to trust that modern Go syntax — generics, range-over-func, generic type aliases — parses correctly
- An agent should be able to find unresolved imports — packages referenced but not found in the module
- An agent should be able to trust that a single malformed file never prevents other files from being indexed

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Find what implements an interface — computed, not declared | Implicit interfaces are Go's defining feature — without computation, the most fundamental relationship is invisible |
| Explain why a type *doesn't* satisfy an interface | The hardest Go debugging question answered from the graph |
| Find all error types and sentinel errors | `error` is Go's most pervasive interface — every codebase has dozens of implementations |
| Trace the embedding graph and see promoted methods | Embedding is Go's composition mechanism — it answers "what can this type do?" |
| See the complete type across all files in a package | Methods defined in separate files are one type's API |
| Find goroutine launch sites and synchronization | Concurrency is Go's defining runtime feature — agents need to see it |
| Find and trace `init()` functions and side-effect imports | Startup behavior is implicit and order-dependent — the graph makes it explicit |
| Recognize enum patterns from const blocks | Go has no enum keyword — the graph must see the convention |
| Query compiler directives as structured data | `//go:embed` and `//go:build` carry structural weight disguised as comments |
| Link tests to what they test by naming convention | "What tests cover Authenticate?" should be one query |
| Trace the import graph from entry point to leaf | "What does this binary depend on?" answered without reading files |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Require reading files to find interface implementations | An agent should find what implements an interface in one query |
| Only show positive interface matches | An agent should see why a type fails to satisfy an interface |
| Show methods without receiver information | An agent should see value vs pointer receiver on every method |
| Treat embedding as a footnote | An agent should trace embedding chains and see promoted methods |
| Ignore `init()` and side-effect imports | An agent should find all init functions and trace startup order |
| Treat `error` like any other interface | An agent should find error types and sentinel errors as a first-class concern |
| Ignore the iota/const/type enum pattern | An agent should recognize enum-like patterns from structure |
| Bury compiler directives in comments | An agent should query `//go:build` and `//go:embed` as structured data |
| Split a type's methods across files without reunification | An agent should see one complete type, regardless of file boundaries |
| Require Go installed to understand Go code | An agent should query Go structure from the graph alone |

---

*An agent should be able to understand Go code the way a Go developer does — through packages, interfaces, embedding, and conventions — not the way a parser does.*
