---
description: Plan for C/C++ format loader — macro interference detection, ERROR node classification, preprocessor nodes, template parameters, and single-file analysis
tags: [format, cpp, c, plan, macros, analysis, error-handling]
audience: { human: 40, agent: 60 }
purpose: { plan: 95, design: 5 }
---

# Plan: C/C++ Loader — Error Handling, Macro Detection, and Single-File Analysis

Implements: [C/C++ Format Design](../designs/future/cpp-format-loader.md) — Macro Interference Detection, Multi-File Analysis (single-file portions), Edge Types (REFERS_TO for includes and friends)

## Scope

**Covers:**
- `MacroInterferenceDetector` — ERROR and MISSING node classification
- Known macro family list (Qt, Windows SDK, Google Test, Catch2, Boost)
- Macro interference annotations (`cpp/macro_interference`, `cpp/syntax_error`, `cpp/template_complexity`, `cpp/preprocessor_boundary`)
- Macro warning in headline when interference detected
- `CppSingleFileAnalyzer` pipeline processor
- Include graph edges — direct `REFERS_TO` from `#include` nodes to target URIs
- Documentation comment extraction — `/** */` and `///` as node properties
- Attribute extraction — `[[nodiscard]]`, `[[deprecated]]` etc. as node properties
- Test framework detection — `TEST`, `TEST_F`, `TEST_CASE` macro patterns
- Additional node types: `cpp.include`, `cpp.macro`, `cpp.using`
- Template parameter extraction — `template_params` and `is_template` properties
- Conditional compilation annotations — `#ifdef`/`#if` block boundaries
- `concept_definition` → `cpp.type` with `kind=concept`
- `module_declaration` → `cpp.module` with `kind=module`
- Friend declarations — `REFERS_TO` edges with `relationship=friend`
- Coroutine detection — `co_await`, `co_yield`, `co_return` marking functions as coroutines
- Exception handling structure — `noexcept` (already on functions), `try`/`catch` block annotations, `throw` type detection
- Bitfield members — width property on struct/union fields
- Type alias extraction — `typedef` and `using` type aliases as properties or nodes
- Function pointer and variadic function detection
- Tests for macro detection, analysis steps, preprocessor constructs, and new constructs

**Does not cover:**
- Multi-file analysis — header/source linking, inheritance graph, transitive includes (Plan: cpp-03-cross-file-intelligence)
- SQL views (Plan: cpp-03-cross-file-intelligence)
- `help://` documentation (Plan: cpp-03-cross-file-intelligence)

## Enables

Once this exists:
- **Agents know where macros hide structure** — macro interference annotations with known macro family identification make gaps visible
- **Agents can trace direct includes** — `#include "pool.h"` creates an edge to the target header
- **Agents can see documentation comments** — `@param`, `@returns` tags as queryable properties on functions and classes
- **Agents can find deprecated and nodiscard APIs** — `[[deprecated]]` and `[[nodiscard]]` as structured metadata
- **Agents can find tests** — test framework macros recognized and annotated despite being preprocessor constructs
- **Agents can see templates** — template parameters, constraints, and specialization markers on nodes
- **Agents can distinguish parse errors from macro interference** — error classification prevents false alarm
- **Plan 03 can proceed** — single-file analysis produces the include edges and annotations that multi-file analysis enriches

This increment makes the tool *honest*. Plan 01 gives structure; this plan marks where structure is hidden.

## Prerequisites

- Plan: cpp-01-grammar-and-basic-parsing complete — grammar loading, classifier, materializer, core node types, x-ray templates
- Validation experiment #2 results available — macro interference rates on real codebases inform detection thresholds

## North Star

Index a Qt project with heavy macro usage. Every `Q_OBJECT`, `Q_PROPERTY`, and `Q_SIGNAL` invocation should be flagged with "macro may hide structure." Every `#include` should create a traceable edge. Every `/** @param */` comment should be a queryable property. The agent never pretends to see what the preprocessor conceals — and it never calls a macro-induced parse error a syntax error.

## Done Criteria

### Macro Interference Detection

- The `MacroInterferenceDetector` shall classify ERROR and MISSING nodes by context:
  - When an ERROR node follows an ALL_CAPS identifier inside a class body → `macro_interference` with high confidence
  - When an ERROR node follows an ALL_CAPS identifier before a class declaration → `macro_interference` (export/visibility macro) with high confidence
  - When a MISSING `#endif` appears near `extern "C"` → `preprocessor_boundary` with high confidence
  - When an ERROR node follows an identifier matching a known macro family pattern → `macro_interference` with very high confidence
  - When an ERROR node appears in template `<>` context → `template_complexity` with medium confidence
  - When other ERROR nodes appear → `syntax_error` with low confidence
  - When other MISSING nodes appear (semicolons, braces) → `syntax_error`
- Each classification shall emit an annotation with:
  - `kind = "lint"`
  - `severity = "info"` (for macro interference) or `"warning"` (for syntax errors)
  - `rule_id` matching the classification (`cpp/macro_interference`, `cpp/syntax_error`, `cpp/template_complexity`, `cpp/preprocessor_boundary`)
  - `message` including the macro name when identifiable
  - `data` JSON with `macro_name`, `context`, `confidence`, `start_line`, `end_line`
  - `scope_document_id` set to the document node's ID (required for SQL view joins)
  - `source = "cpp-analyzer"`
- The known macro families list shall include at minimum:
  - Qt: `Q_OBJECT`, `Q_PROPERTY`, `Q_SIGNAL`, `Q_SLOT`, `Q_EMIT`, `Q_INVOKABLE`
  - Windows SDK: `__declspec`, `EXPORT_API`, `DLLEXPORT`, `STDMETHODCALLTYPE`
  - Google Test: `TEST`, `TEST_F`, `TEST_P`, `EXPECT_*`, `ASSERT_*`
  - Catch2: `TEST_CASE`, `SECTION`, `BENCHMARK`
  - Boost: `BOOST_AUTO_TEST_CASE`, `BOOST_FIXTURE_TEST_CASE`
- ERROR nodes shall NOT prevent extraction of structure from the rest of the file

### Headline Enrichment

- When macro interference annotations exist for a file, the headline shall include a warning:
  - Example: `widget.h | code.cpp-header | 140 ln, ~0.8k tok | ns:ui | class Widget : public QObject | ⚠ Q_OBJECT (hidden members)`
- When no macro interference is detected, the headline shall remain unchanged from Plan 01

### Preprocessor Nodes

- The materializer shall extract `cpp.include` nodes for `preproc_include` CST nodes:
  - `target` property — the included path (e.g., `"pool.h"` or `<vector>`)
  - `style` property — `<>` or `""`
  - `HAS_PART` edge from document
- The materializer shall extract `cpp.macro` nodes for `preproc_def` CST nodes:
  - `name`, `parameters` (if function-like), `replacement` (replacement text) properties
  - `HAS_PART` edge from document
- The materializer shall extract `cpp.using` nodes for `using_declaration` CST nodes:
  - `target` property — what's being imported
  - `REFERS_TO` edge to target (if resolvable within the file)
- The materializer shall emit annotations for `preproc_ifdef`/`preproc_if` CST nodes:
  - `kind = "lint"`, `severity = "info"`, `rule_id = "cpp/conditional_compilation"`
  - `data` JSON with `predicate` (the condition text), `start_line`, `end_line`

### Template Parameters

- When a declaration is preceded by `template_declaration`, the materializer shall:
  - Set `is_template = "true"` on the inner declaration's node
  - Set `template_params` property to the parameter list text (e.g., `"typename T, int N"`)
- For explicit specializations, the materializer shall set:
  - `base_template` — the name of the primary template
  - `specialization_args` — the specialization arguments (e.g., `"int"`, `"<int, 3>"`)
- For partial specializations, the materializer shall set both `template_params` and `specialization_args`

### C++20 Constructs

- The materializer shall extract `cpp.type` nodes with `kind=concept` for `concept_definition` CST nodes:
  - `constraint` property — the constraint expression text
- The materializer shall extract `cpp.module` nodes with `kind=module` for `module_declaration` CST nodes:
  - `partition` property — the module partition name (if present)
  - `is_export` property — `"true"` if this is an export declaration
- When module syntax is only partially parsed (ERROR nodes in module context), the materializer shall annotate with `cpp/unsupported_module_syntax`

### Friend Declarations

- The materializer shall create `REFERS_TO` edges for `friend_declaration` CST nodes:
  - `relationship = "friend"` in edge properties
  - Source: the class declaring the friend
  - Destination: the friend function or class node (if resolvable within the file)
  - When the friend target is not resolvable, emit the edge with the target name in properties

### Coroutine Detection

- When a function body contains `co_await`, `co_yield`, or `co_return`, the materializer shall set `is_coroutine = "true"` on the function node
- The materializer shall record the function's return type (already captured) — the promise type is not extractable syntactically

### Exception Handling Structure

- The analyzer shall detect `try`/`catch` blocks within function bodies and emit annotations:
  - `rule_id = "cpp/exception_handler"`, `data` with `caught_types` (JSON array of exception types caught)
- The analyzer shall detect `throw` expressions and annotate with:
  - `rule_id = "cpp/throw_expression"`, `data` with `thrown_type`
- `noexcept` is already captured as a property on functions (Plan 01)

### Bitfield Members

- When a struct/union member has a bitfield width specifier (`: N`), the materializer shall set `bitfield_width` property on the `cpp.member` node
- The member's `kind` remains `field`

### Type Aliases and Constants

- The materializer shall extract `typedef` declarations as `cpp.member` nodes with `kind=typedef` and `target_type` property
- The materializer shall extract `using` type aliases (`using Vec = std::vector<T>`) as `cpp.member` nodes with `kind=type_alias` and `target_type` property
  - These are distinct from `cpp.using` nodes which import names (`using std::string`)
- The materializer shall detect `constexpr` variables at namespace scope and set `is_constexpr = "true"` on the node

### Function Pointers and Variadic Functions

- The materializer shall detect function pointer declarations and set `is_function_pointer = "true"` with `pointed_signature` property
- The materializer shall detect variadic functions (parameter list ending in `...`) and set `is_variadic = "true"` on the function node

### Single-File Analysis

- The `CppSingleFileAnalyzer` shall create `REFERS_TO` edges from `cpp.include` nodes to target document nodes:
  - For `#include "local.h"`, resolve the target path relative to the source file's directory
  - For `#include <system>`, record the edge but mark `is_resolved = "false"` in properties
  - When the target file is not in the index, mark `is_resolved = "false"`
- The analyzer shall extract documentation comments:
  - `/** */` and `///` comments preceding declarations shall be attached as a `doc_comment` property
  - `@param`, `@returns`, `@brief`, `@deprecated`, `@see` tags shall be parsed and stored as structured JSON in `doc_tags` property
- The analyzer shall extract standard attributes:
  - `[[nodiscard]]`, `[[deprecated]]`, `[[maybe_unused]]`, `[[fallthrough]]`, `[[likely]]`, `[[unlikely]]` → `attributes` property (JSON array)
  - `[[deprecated("reason")]]` → include the reason string
  - Vendor attributes (`__attribute__`, `__declspec`) → `vendor_attributes` property
- The analyzer shall detect test framework patterns:
  - `TEST(suite, name)`, `TEST_F(fixture, name)`, `TEST_P(fixture, name)` → annotate as test with `test_suite` and `test_name` properties
  - `TEST_CASE("name")`, `SECTION("name")` → annotate as test with `test_name` property
  - Test nodes shall have `is_test = "true"` property despite being macro-generated
- Each analysis step shall be independent — failure in one shall not prevent others from running
  - When an analysis step fails, emit a warning annotation and continue

### Tests

- Test macro detection — parse a file with `Q_OBJECT` → verify `cpp/macro_interference` annotation with macro name
- Test macro detection — parse a file with `__declspec(dllexport)` → verify export macro detection
- Test macro detection — parse a file with `TEST_F` → verify test macro detection
- Test ERROR node classification — parse malformed C++ → verify `cpp/syntax_error` classification
- Test MISSING node classification — parse C++ with missing semicolon → verify `cpp/syntax_error` with "Missing expected token"
- Test template context errors — parse deeply nested template → verify `cpp/template_complexity`
- Test preprocessor boundary — parse `extern "C"` pattern → verify `cpp/preprocessor_boundary`
- Test headline with macro warning — verify `⚠` prefix when macro interference detected
- Test include edge creation — verify `REFERS_TO` edge from `cpp.include` to target document
- Test include edge for system headers — verify `is_resolved = "false"` for `<vector>`
- Test doc comment extraction — verify `doc_comment` and `doc_tags` properties on function node
- Test attribute extraction — verify `attributes` property contains `[[nodiscard]]`
- Test deprecated attribute — verify reason string extracted
- Test test framework detection — verify `is_test = "true"` on `TEST_F` node
- Test template parameter extraction — verify `is_template`, `template_params` properties
- Test concept extraction — verify `cpp.type` with `kind=concept`
- Test module extraction — verify `cpp.module` with partition info
- Test friend declaration — verify `REFERS_TO` edge with `relationship=friend`
- Test `cpp.macro` node extraction — verify `name`, `parameters`, `replacement` properties
- Test `cpp.using` node extraction — verify `target` property and `REFERS_TO` edge
- Test conditional compilation annotation — verify `cpp/conditional_compilation` for `#ifdef`
- Test module unsupported syntax annotation — verify `cpp/unsupported_module_syntax` when ERROR in module context
- Test coroutine detection — verify `is_coroutine = "true"` for function with `co_await`
- Test try/catch annotation — verify `cpp/exception_handler` with `caught_types` array
- Test throw annotation — verify `cpp/throw_expression` with `thrown_type`
- Test bitfield member — verify `bitfield_width` property on struct field with `: 4`
- Test typedef extraction — verify `cpp.member` with `kind=typedef` and `target_type`
- Test using type alias — verify `cpp.member` with `kind=type_alias` and `target_type`
- Test function pointer detection — verify `is_function_pointer` and `pointed_signature`
- Test variadic function detection — verify `is_variadic = "true"` for `printf`-style function
- Test analysis isolation — one analysis step failing shall not prevent others
- Tests shall use TUnit (`[Test]`), AwesomeAssertions, and FakeItEasy per project conventions

## Constraints

- **Heuristic macro detection only** — no preprocessor expansion; classification is best-effort (design decision: "trustworthy or loudly not")
- **Known macro list is a heuristic aid, not a filter** — unknown macros still detected by ALL_CAPS pattern
- **Include edges are direct only** — transitive includes computed in Plan 03's multi-file analysis
- **Limited cross-file resolution** — include targets resolve to whatever files are already in the index at analysis time; friend and using targets resolve within the same file only; full cross-file resolution (inheritance, header/source linking) is Plan 03
- **Annotation severity is `info` for macro interference** — false positives are low-harm compared to silently missing structure
- **Doc comment parsing is best-effort** — malformed Doxygen tags store raw text rather than failing

## References

- [C/C++ Format Design](../designs/future/cpp-format-loader.md) — macro interference detection patterns, annotation format, analysis steps
- [C/C++ Format North Star](../north-star/formats/cpp.md) — preprocessor boundary, attributes, documentation comments, testing sections
- [C/C++ Indexing Flow](../flows/future/cpp-indexing.md) — ERROR node classification table, single-file analysis stage
- [C/C++ Parsing Research](../research/cpp-parsing-options.md) — macro interference analysis on Qt, Windows SDK, Google Test
- Plan: cpp-01-grammar-and-basic-parsing — materializer, node types, x-ray templates this plan extends
- [Processor Guide](../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — pipeline processor patterns
- [Testing Guidelines](../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy conventions

## Error Policy

Analysis errors must not cascade. When a single analysis step fails:
1. Log warning with file URI, analysis step name, and exception details
2. Emit a warning annotation with `rule_id = "cpp/analysis_failure"` and the step name
3. Continue with remaining analysis steps
4. The file is still queryable with whatever structure was extracted

Macro interference detection is inherently heuristic — false positives (flagging a non-macro as macro) are acceptable; false negatives (missing a macro that hides structure) are acceptable too. The goal is useful signal, not perfect classification.
