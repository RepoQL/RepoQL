---
description: What great C/C++ format support looks like - declarations for querying C and C++ structure through the knowledge graph
tags: [cpp, c, format, north-star, vision]
audience: { human: 50, agent: 50 }
purpose: { north-star: 100 }
---

# C/C++ Format Support: What Great Looks Like

> An agent should be able to understand a C/C++ codebase's structure — classes, namespaces, templates, and the include graph that stitches headers to source files — without reading source files, without a build system, and without running the preprocessor. Where the preprocessor hides structure, the agent should know exactly where it can't see, and say so. Everything syntactically visible is queryable. Everything invisible is marked.

An agent lands in a C++ project with 3,000 source files across a library, a server, and a test suite. It doesn't open any. It scans headlines and sees: a `ConnectionPool` class with 6 public methods and 2 private helpers, a `Serializer` template specialization for 4 types, a header declaring an abstract `Transport` interface with 3 pure virtual methods, a `.cpp` file defining those methods for `TcpTransport` and `UdpTransport`. It lands in a C project with 800 files — no classes, no namespaces, no templates — and sees: a `connection_t` struct with 5 fields, a `conn_open` function taking a config pointer and returning a handle, a header full of `#define` constants for error codes, a file with 12 function pointer typedefs defining a plugin API. Same tools, same patterns, same graph.

It asks "what inherits from Transport?" and gets every derived class across the codebase. It asks "what includes `<mutex>`?" and traces the header dependency chain from 40 source files through 12 internal headers to the standard library. It asks "show me the public API of the network namespace" and sees every exported declaration across 20 header files unified into one view. Some files use macros that expand to class members — Qt's `Q_OBJECT`, Windows' `__declspec(dllexport)`. The agent sees the macro invocation, knows structure may be hidden behind it, and says so. It never pretends to see what the preprocessor conceals. The codebase used no build system configuration. The agent understood the structure anyway — because structure that's syntactically visible is always queryable, and structure that isn't is always marked.

---

## Discovery

- An agent should be able to distinguish file roles from a headline alone — header, source, inline implementation, test, benchmark, generated code, build configuration
- An agent should be able to see what a file declares and which namespace it belongs to without opening it
- An agent should be able to tell a class definition from a function library from a type-alias header from a template specialization file from the headline
- An agent should be able to see key exported names in the headline — actual names, not just counts
- An agent should be able to distinguish C from C++ files and see the language standard implied by syntax
- An agent should be able to identify header-only libraries — headers that contain complete implementations, not just declarations

```
headline  →  "connection_pool.h | code.cpp-header | 180 ln, ~1.0k tok | ns:net | class ConnectionPool | connect, execute, disconnect"
headline  →  "connection_pool.cpp | code.cpp | 320 ln, ~1.8k tok | ns:net | implements ConnectionPool | 6 methods"
headline  →  "serializer.h | code.cpp-header | 450 ln, ~2.4k tok | ns:io | template<T> class Serializer | 4 specializations"
headline  →  "transport.h | code.cpp-header | 60 ln, ~0.4k tok | ns:net | abstract class Transport | send, receive, close (pure virtual)"
headline  →  "test_pool.cpp | code.cpp-test | 200 ln, ~1.2k tok | TEST_CASE: ConnectionPool | connect_succeeds, timeout_recovery, concurrent_access"
headline  →  "widget.h | code.cpp-header | 140 ln, ~0.8k tok | ns:ui | class Widget : public QObject | ⚠ Q_OBJECT macro (hidden members)"
```

---

## The Header/Source Split

- An agent should be able to see a type's complete definition by combining its header declaration and source-file implementations
- An agent should be able to find the header that declares a given function and the source file that defines it
- An agent should be able to find forward declarations and trace them to their full definitions
- An agent should be able to see include guards and `#pragma once` as structural metadata, not noise
- An agent should be able to find header-only implementations — templates and inline functions defined entirely in headers
- An agent should be able to distinguish a declaration from a definition for any symbol

C/C++ splits declarations and definitions across files by convention and necessity. A class declared in `pool.h` with method bodies in `pool.cpp` is one entity. An agent should see one complete type, not two file-scoped fragments.

```sql
-- Find the declaration and definition of ConnectionPool::connect
SELECT n.properties->>'name' AS name, n.kind, repository_uri_container(n.uri) AS file,
       CASE WHEN n.properties->>'is_definition' = 'true' THEN 'defined' ELSE 'declared' END AS role
FROM node n
WHERE n.properties->>'qualified_name' = 'net::ConnectionPool::connect'
  AND n.kind IN ('cpp.member', 'cpp.function')
```

---

## The Preprocessor Boundary

- An agent should be able to see every `#include` directive and trace the include graph between files
- An agent should be able to see `#define` macro definitions with their names, parameters, and replacement text
- An agent should be able to see `#ifdef`/`#ifndef`/`#if` conditional compilation blocks and their predicates — and know that the active branch is unknown without build configuration
- An agent should be able to find all files that define or use a given macro
- An agent should be able to see macro invocations at call sites — what macro was called and where
- An agent should be able to trust that when a macro hides structure, the graph marks the macro invocation and flags that expansion is invisible
- An agent should be able to distinguish between simple macros (constants, function-like macros with visible replacement text) and opaque macros (expanding to syntactic fragments that alter class or function structure)
- An agent should be able to recognize known categories of structural macros:
  - Framework member-injection macros that add hidden class members (Qt `Q_OBJECT`, MFC `DECLARE_MESSAGE_MAP`)
  - Export/visibility macros that precede declarations (`__declspec(dllexport)`, `__attribute__((visibility))`)
  - Test framework macros that define entire functions (`TEST`, `TEST_F`, `TEST_CASE`)
  - Namespace macros that wrap entire scopes (Pixar USD `PXR_NAMESPACE_USING_DIRECTIVE`)
  - The `#ifdef __cplusplus extern "C"` interop pattern in C/C++ boundary headers
- An agent should be able to find all locations where opaque macros may be hiding structure — and know that the graph's view is incomplete at those points

The preprocessor is C/C++'s metaprogramming mechanism. Like Rust's `macro_rules!`, the north star is not "capture the expanded output" — it's "capture what has a syntactic footprint, and be honest about what's invisible." An agent sees that `Q_OBJECT` is invoked inside a class body and that structure may be hidden behind it. It sees that `EXPORT_API` precedes a class declaration and that the parser may have misread the declaration. Honest gaps beat false completeness.

```sql
-- What macros are invoked inside class bodies?
SELECT m.name, m.file_uri, c.name AS containing_class
FROM cpp_macro_invocations m
JOIN cpp_classes c ON m.file_uri = c.file_uri
  AND m.start_line BETWEEN c.start_line AND c.end_line
WHERE m.context = 'class_body'

-- Find files with parse warnings from macro interference
SELECT repository_uri_container(doc.uri) AS file, an.message
FROM annotation an
JOIN node doc ON doc.id = an.scope_document_id
WHERE an.kind = 'lint' AND an.rule_id = 'cpp/macro_interference'
```

---

## Classes, Structs, and Unions

- An agent should be able to see every class, struct, and union with its members — fields, methods, nested types, friends
- An agent should be able to see access specifiers on every member — public, protected, private
- An agent should be able to find all constructors, the destructor, and assignment operators for a type
- An agent should be able to see which methods are virtual, pure virtual, override, or final
- An agent should be able to find abstract classes — those with at least one pure virtual method
- An agent should be able to see static members and distinguish them from instance members
- An agent should be able to see `const` and `noexcept` qualifiers on methods
- An agent should be able to find all operator overloads on a type — both member operators and friend free-function operators
- An agent should be able to see a struct's complete layout without reading the file — fields with types, in declaration order
- An agent should be able to see bitfield members in structs and unions with their width
- An agent should be able to find `friend` declarations and see which external functions or classes have access to a type's private members

```
structure →
  connection_pool.h (code.cpp-header)
    #pragma once
    #include <string>
    #include <memory>
    #include "transport.h"

    namespace net {

    + class ConnectionPool
      + using Ptr = std::shared_ptr<ConnectionPool>                  #symbol=ConnectionPool::Ptr
      + explicit ConnectionPool(Config config)                       #symbol=ConnectionPool::ConnectionPool
      + ~ConnectionPool()                                            #symbol=ConnectionPool::~ConnectionPool
      + Connection connect(const std::string& endpoint)              #symbol=ConnectionPool::connect
      + void disconnect(Connection& conn)                            #symbol=ConnectionPool::disconnect
      + size_t active_count() const noexcept                         #symbol=ConnectionPool::active_count
      - std::vector<Connection> pool_                                #symbol=ConnectionPool::pool_
      - Config config_                                               #symbol=ConnectionPool::config_
      - bool validate_(const Connection& conn)                       #symbol=ConnectionPool::validate_

    }  // namespace net
```

---

## Inheritance and Polymorphism

- An agent should be able to see every base class of a type, including access level (public, protected, private inheritance)
- An agent should be able to find every class that derives from a given base — the full inheritance tree
- An agent should be able to trace virtual function override chains per method — from base declaration through each overriding derived class
- An agent should be able to find all pure virtual functions across the codebase and see which classes implement them
- An agent should be able to see multiple inheritance and trace the diamond problem — which base appears through which paths
- An agent should be able to find classes that inherit virtually — `class D : virtual public B`
- An agent should be able to find ambiguous member lookups caused by multiple inheritance paths

C++ supports multiple inheritance. A type can inherit from several bases, each with its own access level. Virtual inheritance resolves the diamond problem but adds complexity. An agent that can trace the full inheritance graph — including virtual bases and access levels — understands polymorphism the way a C++ developer does.

```sql
-- What derives from Transport?
SELECT derived.properties->>'name' AS name, e.properties->>'access' AS inheritance_access
FROM edge e
JOIN node derived ON derived.id = e.source_node_id
JOIN node base ON base.id = e.destination_node_id
WHERE e.type = 'EXTENDS' AND base.properties->>'name' = 'Transport'

-- Find all abstract classes (have at least one pure virtual method)
SELECT DISTINCT c.name, c.file_uri
FROM cpp_classes c
JOIN cpp_functions f ON f.declaring_type = c.name AND f.file_uri = c.file_uri
WHERE f.is_pure_virtual
```

---

## Templates

- An agent should be able to see template declarations with their parameter lists — type parameters, non-type parameters, template template parameters
- An agent should be able to find all explicit specializations and partial specializations of a template
- An agent should be able to see template parameter constraints — `requires` clauses and concept constraints (C++20)
- An agent should be able to find all function templates and class templates in the codebase
- An agent should be able to see variadic template parameters (`typename... Args`) and distinguish them from non-variadic parameters
- An agent should be able to see fold expressions and parameter pack expansions as structural elements
- An agent should be able to find extern template declarations — explicit instantiation control
- An agent should be able to see `static_assert` declarations and their condition text
- An agent should be able to trust that template syntax is captured structurally even though instantiation is invisible

Templates are C++'s compile-time polymorphism. Like macros, instantiation happens at compile time and is invisible to syntactic analysis. An agent should see the template declaration, its parameters, its specializations, and its constraints — and know that instantiated code is not in the graph.

```sql
-- All specializations of Serializer
SELECT name, template_args, file_uri
FROM cpp_templates
WHERE base_template = 'Serializer' AND template_kind = 'specialization'
```

---

## Namespaces and Scope

- An agent should be able to traverse the namespace hierarchy from global scope to leaf
- An agent should be able to see every symbol declared within a given namespace across all files
- An agent should be able to find anonymous namespaces and understand they create file-local scope
- An agent should be able to see `using` declarations and `using namespace` directives
- An agent should be able to find inline namespaces and understand they transparently expose their contents to the parent
- An agent should be able to see the complete public API of a namespace — all non-anonymous, non-detail symbols — unified across every file that contributes to it

```sql
-- Everything in the net namespace
SELECT name, member_kind, file_uri
FROM cpp_namespace_members
WHERE namespace = 'net'

-- Find anonymous namespaces (file-local scope)
SELECT file_uri, COUNT(*) AS symbols
FROM cpp_namespace_members
WHERE namespace LIKE '%::(anonymous)'
GROUP BY file_uri
```

---

## Enums

- An agent should be able to see an enum's complete enumerator list with explicit values where specified
- An agent should be able to distinguish scoped enums (`enum class`) from unscoped C-style enums
- An agent should be able to see the underlying type of a scoped enum where specified
- An agent should be able to find all enums and query their members in one pass
- An agent should be able to find enums used as flags — multiple enumerators combined with bitwise operators

```sql
-- All scoped enums with their members
SELECT e.name, e.underlying_type, m.properties->>'name' AS member, m.properties->>'value' AS value
FROM cpp_enums e
JOIN edge edge ON edge.source_node_id = e.node_id AND edge.type = 'HAS_PART'
JOIN node m ON m.id = edge.destination_node_id
WHERE e.is_scoped = 'true'
```

---

## Functions

- An agent should be able to see every function with its full signature — parameters with types, return type, qualifiers
- An agent should be able to distinguish free functions from methods from static methods from friend functions
- An agent should be able to see `constexpr`, `consteval`, `inline`, `static`, `extern` specifiers
- An agent should be able to find all function overloads sharing a name within a scope
- An agent should be able to find all `extern "C"` declarations — the C/C++ interop boundary
- An agent should be able to find lambda expressions and see their capture lists and parameter types
- An agent should be able to find function pointer declarations and typedefs, and see the pointed-to signature
- An agent should be able to find `std::function` member declarations and see their signature types
- An agent should be able to find variadic functions (`...`, `va_list`) — common in C APIs and logging interfaces

---

## Type Aliases, Constants, and Storage

- An agent should be able to find all `typedef` declarations and `using` type aliases with what they resolve to
- An agent should be able to distinguish between type aliases and `using` declarations that import names
- An agent should be able to find template aliases — `using Vec = std::vector<T>` patterns
- An agent should be able to find all `constexpr` and `consteval` functions and variables
- An agent should be able to find `#define` constants and distinguish them from typed constants
- An agent should be able to find `static const` and `inline constexpr` variables at namespace scope
- An agent should be able to see `static_assert` conditions as structural diagnostics
- An agent should be able to find `thread_local` and `volatile` qualified declarations
- An agent should be able to find `static` local variables — those with initialization guarantees in C++11+

---

## The Include Graph

- An agent should be able to trace the include graph — what each file includes, directly and transitively
- An agent should be able to find all consumers of a header — everything that includes it
- An agent should be able to distinguish standard library includes (`<vector>`) from system includes (`<sys/types.h>`) from project-local includes (`"pool.h"`)
- An agent should be able to find include cycles — headers that transitively include each other
- An agent should be able to find headers that are included by many files — the high-impact headers where changes ripple widest
- An agent should be able to trust that the include graph reflects what's written in source, even when headers aren't available to resolve

```sql
-- Most-included headers (change impact analysis)
SELECT target_header, COUNT(*) AS includers
FROM cpp_includes
GROUP BY target_header
ORDER BY includers DESC
LIMIT 20

-- Trace what pool.cpp transitively includes
WITH RECURSIVE deps AS (
  SELECT target_header FROM cpp_includes WHERE source_uri LIKE '%pool.cpp'
  UNION
  SELECT i.target_header FROM cpp_includes i JOIN deps d ON i.source_uri LIKE '%' || d.target_header
)
SELECT * FROM deps
```

---

## C++20 Concepts

- An agent should be able to find all concept definitions with their constraint expressions
- An agent should be able to find templates constrained by a given concept
- An agent should be able to see `requires` clauses on functions and classes
- An agent should be able to query which concepts a template parameter must satisfy

---

## C++20 Modules

- An agent should be able to see module declarations, module partitions, and export declarations
- An agent should be able to distinguish module interface units from module implementation units
- An agent should be able to find `import` declarations and trace the module dependency graph
- An agent should be able to trust that module syntax parses without crashing, even when module adoption is low
- An agent should be able to see explicit annotations when module syntax is only partially supported — unsupported constructs marked, indexing continues

Module adoption is minimal today. The north star for modules is narrow: parse the syntax, don't crash, index what's declared. As adoption grows, the graph grows with it.

---

## Error Handling and Exceptions

- An agent should be able to find functions marked `noexcept` and distinguish them from potentially-throwing functions
- An agent should be able to see `try`/`catch` blocks and what exception types are caught
- An agent should be able to find `throw` expressions and see the exception type thrown
- An agent should be able to find exception type hierarchies — types deriving from `std::exception`
- An agent should be able to trace which functions declare or imply exception specifications

Exception handling is how agents trace error flow in C++ code. `noexcept` marks a safety boundary. `catch` blocks reveal what a function expects to go wrong. The exception type hierarchy reveals the error model — analogous to Rust's `Result` error types or Go's `error` interface implementations.

---

## Coroutines

- An agent should be able to find coroutine functions — those containing `co_await`, `co_yield`, or `co_return`
- An agent should be able to see the return type of a coroutine and its associated promise type
- An agent should be able to distinguish coroutine functions from regular async patterns

C++20 coroutines are increasingly used for asynchronous I/O and generator patterns. An agent should be able to find them as naturally as finding async functions in other languages.

---

## Attributes

- An agent should be able to see standard attributes on declarations as queryable metadata — `[[nodiscard]]`, `[[deprecated]]`, `[[maybe_unused]]`, `[[fallthrough]]`, `[[likely]]`, `[[unlikely]]`
- An agent should be able to query which functions or types are marked `[[deprecated]]` — the deprecation surface
- An agent should be able to see `[[nodiscard]]` on return types and find callers that may ignore results
- An agent should be able to see vendor-specific attributes (`__attribute__`, `__declspec`) as structured metadata where syntactically visible

Attributes carry semantic weight: `[[nodiscard]]` means callers must use the return value, `[[deprecated]]` means the API is slated for removal. An agent should query these as structured data, not search through attribute text.

---

## Documentation Comments

- An agent should be able to see Doxygen-style documentation comments (`/** */`, `///`) on functions, classes, and members as structured metadata
- An agent should be able to see `@param`, `@returns`, `@brief`, `@deprecated`, `@see` tags as queryable fields
- An agent should be able to query which public API items lack documentation comments
- An agent should be able to distinguish documentation comments from inline comments

Documentation comments are one of the most useful pieces of metadata for understanding what a function does without reading the body. Doxygen's tag syntax (`@param`, `@returns`) is more structured than most languages' doc comments and lends itself naturally to queryable fields.

---

## Memory and Resource Management

- An agent should be able to find destructors and see which classes manage resources
- An agent should be able to find smart pointer usage patterns — `std::unique_ptr`, `std::shared_ptr` — in member declarations
- An agent should be able to find classes that follow the Rule of Five — custom destructor, copy/move constructors, copy/move assignment operators
- An agent should be able to find `delete`d special members — `= delete` on copy or move operations

---

## Testing

- An agent should be able to find test functions and see what they test — linked by naming convention or framework markers
- An agent should be able to recognize test framework patterns from structure — Google Test (`TEST`, `TEST_F`, `TEST_P`), Catch2 (`TEST_CASE`, `SECTION`), doctest, CppUnit — without hardcoding framework knowledge
- An agent should be able to find test fixtures (classes deriving from `::testing::Test` or equivalent)
- An agent should be able to see which test macros produced parse errors and mark them as framework-specific limitations rather than broken code
- An agent should be able to find benchmarks (Google Benchmark, Catch2 `BENCHMARK`)

Test macros are the most visible impact of the preprocessor boundary. `TEST_F(PoolTest, ConnectSucceeds) { ... }` declares a test but the macro expands to a class definition invisible to syntactic analysis. An agent should see the test name, the fixture, and the body — and know the generated class is invisible.

---

## Build Configuration

- An agent should be able to find `CMakeLists.txt`, `Makefile`, `meson.build`, `BUILD`, and other build files and see them as build configuration, not generic text
- An agent should be able to find `compile_commands.json` and `compile_flags.txt` and know they enable richer analysis
- An agent should be able to see which source files are listed in build targets without parsing the full build system

---

## Integrity

- An agent should be able to find files with parse errors and see what structure was recoverable
- An agent should be able to classify parse errors by cause — macro interference, compiler extension syntax, preprocessor boundary patterns (`#ifdef __cplusplus extern "C"`), or genuine syntax errors
- An agent should be able to trust that complex syntax — deeply nested templates, variadic templates, fold expressions, structured bindings — parses correctly
- An agent should be able to trust that a single malformed file never prevents other files from being indexed
- An agent should be able to see where the graph's view is incomplete due to preprocessor limitations and trust that these gaps are explicitly marked
- An agent should be able to distinguish "this file has no structure" from "this file failed to parse" from "this file has structure hidden behind macros"

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| See a type's complete definition across header and source | The header/source split is C++'s defining convention — agents need the unified view |
| Trace the full inheritance tree including multiple inheritance | "What implements this abstract class?" is the central polymorphism question |
| Find all template specializations of a given template | Templates are C++'s compile-time polymorphism — specializations are the concrete implementations |
| See every symbol in a namespace across all files | Namespaces unify APIs that span dozens of headers — the namespace is the module |
| Trace the include graph from source to transitive headers | "What does changing this header affect?" answered without a build system |
| Know that `Q_OBJECT` hides generated members and mark the gap | False completeness is worse than a marked gap — agents must know what they can't see |
| See preprocessor directives as structured data | `#include`, `#define`, `#ifdef` carry structural weight — they're not comments |
| Find `noexcept` functions and trace exception type hierarchies | Error flow is what agents investigate first when debugging C++ code |
| See `[[deprecated]]` and `[[nodiscard]]` as queryable metadata | Attributes carry semantic weight that shapes how APIs are used |
| Find tests despite macro wrappers and see what they cover | Test frameworks use macros heavily — agents need test structure despite the preprocessor boundary |
| Query C and C++ through the same SQL surface as every other format | Learn once, query anything — C++ shouldn't require special tools |
| Trust that one bad file never breaks the index | Macro-heavy headers and malformed templates must not cascade |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Show declarations without their definitions | An agent should see the complete type across header and source |
| Flatten multiple inheritance into a list | An agent should traverse the inheritance graph and see access levels on each base |
| Ignore template parameters | An agent should see template parameters with their constraints |
| Pretend macros don't exist | An agent should see macro definitions and invocations — and know expansion is invisible |
| Silently drop structure behind macros | An agent should see a flag wherever macros may hide members or alter declarations |
| Stop at file boundaries for namespaces | An agent should see a namespace's complete contents across all contributing files |
| Require a build system to parse anything | An agent should extract syntactic structure with zero configuration |
| Treat headers and source as unrelated files | An agent should link declarations to their definitions across the header/source boundary |
| Hide preprocessor conditionals | An agent should see `#ifdef` blocks and know which code is conditionally compiled |
| Claim to know which `#if` branch is active without defines | An agent should mark active branch as unknown unless build configuration is available |
| Treat all parse errors as equivalent | An agent should distinguish macro-induced parse issues from genuine syntax errors |
| Require reading files to trace inheritance or specializations | An agent should find all derived classes and template specializations from the graph |
| Require Clang installed to understand C++ code | An agent should query C and C++ structure from the graph alone |

---

*An agent should be able to understand C and C++ code the way a C++ developer does — through classes, namespaces, templates, includes, and honest gaps where the preprocessor hides the rest.*
