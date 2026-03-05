---
description: Advanced tree-sitter techniques for queries, grammars, incremental parsing, code navigation, AI applications, and integration.
tags: [tree-sitter, queries, grammars, parsing, performance, ast-grep, highlighting, code-navigation, AI, reference]
audience: { human: 50, agent: 50 }
purpose: { reference: 85, research: 15 }
---

# Tree-Sitter Advanced Cookbook

Techniques for getting more out of tree-sitter. Assumes familiarity with basic parsing, queries, and grammar authoring. Covers the full spectrum from query patterns through grammar design, hard parsing problems, editor integration, code navigation, and AI/LLM applications.

## Files

| File | Content |
|------|---------|
| [Queries & Traversal](tree-sitter-cookbook-queries.md) | Query patterns, ast-grep structural search, tree traversal, node-types.json |
| [Grammar Authoring](tree-sitter-cookbook-grammars.md) | Grammar design, hard parsing problems, external scanners, grammar composition |
| [Integration](tree-sitter-cookbook-integration.md) | Incremental parsing, multi-language, highlighting, WASM, code navigation, AI/LLM, diffing, concurrency |
| [Operations](tree-sitter-cookbook-operations.md) | Error handling, performance, production edge cases, version migration |

## Quick Reference

| Need | Where |
|------|-------|
| Extract multiple node types in one pass | [Combined Queries](tree-sitter-cookbook-queries.md#combined-queries) |
| Match only when a field is absent | [Field Negation](tree-sitter-cookbook-queries.md#field-negation) |
| Match first/last/adjacent children | [Anchors](tree-sitter-cookbook-queries.md#anchors) |
| Filter by node text content | [Predicate Composition](tree-sitter-cookbook-queries.md#predicate-composition) |
| Make queries faster | [Query Performance](tree-sitter-cookbook-queries.md#query-performance) |
| Structural search/replace with YAML rules | [ast-grep Patterns](tree-sitter-cookbook-queries.md#ast-grep-patterns) |
| Walk trees efficiently without recursion | [Cursor-Based Traversal](tree-sitter-cookbook-queries.md#cursor-based-traversal) |
| Jump to a specific byte/point in the tree | [Efficient Node Lookup](tree-sitter-cookbook-queries.md#efficient-node-lookup) |
| Understand `node-types.json` for codegen | [node-types.json Structure](tree-sitter-cookbook-queries.md#node-typesjson-structure) |
| Resolve ambiguous operator precedence | [Flat Expression Hierarchies](tree-sitter-cookbook-grammars.md#flat-expression-hierarchies) |
| Know when to use `token(prec(...))` vs `prec(...)` | [Lexical vs Parse Precedence](tree-sitter-cookbook-grammars.md#lexical-vs-parse-precedence) |
| Debug grammar conflicts and parse issues | [Grammar Debugging](tree-sitter-cookbook-grammars.md#grammar-debugging) |
| Parse `>>` as both shift and nested generics | [Generics Closing Angle Brackets](tree-sitter-cookbook-grammars.md#generics-closing-angle-brackets) |
| Distinguish regex from division | [Regex vs Division](tree-sitter-cookbook-grammars.md#regex-vs-division) |
| Handle automatic semicolon insertion | [Automatic Semicolon Insertion](tree-sitter-cookbook-grammars.md#automatic-semicolon-insertion) |
| Parse string interpolation / template literals | [String Interpolation](tree-sitter-cookbook-grammars.md#string-interpolation) |
| Handle indentation-sensitive languages | [Indentation Tracking](tree-sitter-cookbook-grammars.md#indentation-tracking) |
| Handle template literal nesting | [Nesting Depth Tracking](tree-sitter-cookbook-grammars.md#nesting-depth-tracking) |
| Extend an existing grammar | [Extending Existing Grammars](tree-sitter-cookbook-grammars.md#extending-existing-grammars) |
| Re-parse efficiently after edits | [Edit Batching](tree-sitter-cookbook-integration.md#edit-batching) |
| Find what changed between parses | [Tree Diffing](tree-sitter-cookbook-integration.md#tree-diffing) |
| Parse multi-language files | [Language Injection](tree-sitter-cookbook-integration.md#language-injection) |
| Write highlight queries with correct priority | [Highlighting Queries](tree-sitter-cookbook-integration.md#highlighting-queries) |
| Track variable scopes for highlighting | [Locals and Scope Tracking](tree-sitter-cookbook-integration.md#locals-and-scope-tracking) |
| Author indent/fold/textobject queries | [Structural Queries Beyond Highlighting](tree-sitter-cookbook-integration.md#structural-queries-beyond-highlighting) |
| Use tree-sitter in the browser | [web-tree-sitter](tree-sitter-cookbook-integration.md#web-tree-sitter) |
| Extract definitions and references | [tags.scm](tree-sitter-cookbook-integration.md#tagsscm--definition-and-reference-extraction) |
| Chunk code for LLM context | [AST-Aware Chunking](tree-sitter-cookbook-integration.md#ast-aware-chunking) |
| Structural diffs instead of line diffs | [Structural Diffing](tree-sitter-cookbook-integration.md#structural-diffing-difftastic) |
| Assess parse quality | [Error Node Analysis](tree-sitter-cookbook-operations.md#error-node-analysis) |
| Pool parsers across threads | [Thread-Safe Parser Pools](tree-sitter-cookbook-operations.md#thread-safe-parser-pools) |
| Defend against pathological inputs | [Defensive Parsing](tree-sitter-cookbook-operations.md#defensive-parsing) |

---

## Gotchas

| Wrong | Right | Why |
|-------|-------|-----|
| Rows and columns are 1-based | Both are **0-based** in tree-sitter | Row = newlines before position; column = bytes from line start |
| Column counts characters | Column counts **bytes** | Multi-byte chars (UTF-8) shift column values |
| `ts_node_parent()` is cheap | It's **O(depth)** — traverses from root | Use `TSTreeCursor` for repeated parent lookups |
| Share `TSTree` across threads | Copy first: `ts_tree_copy()` | Trees are not thread-safe; copies are cheap (COW) |
| `prec(N, ...)` controls the lexer | It controls the **parser** | Use `token(prec(N, ...))` for lexical precedence |
| WASM objects are garbage-collected | They need **explicit `.delete()`** | WASM memory lives outside JS GC |
| Grammar ABI is forward-compatible | It's **backward-compatible only** | Library can load older ABI; cannot load newer |
| Query predicates work everywhere | C library **doesn't evaluate them** | Host application implements predicate evaluation |
| Pattern priority is consistent | **Varies by host**: native v0.21+ = last wins, pre-v0.21 = first wins | Test queries in target host |
| Error recovery is configurable | It's a **black box** | Cannot provide recovery hints or cost overrides |
| Parsing always terminates quickly | Pathological inputs can take **hours** | Use timeout/cancellation for untrusted input |
| External scanner can return without advancing | Scanner **must call `advance` at least once** to return a token | Returning without advancing causes infinite loops |
| During error recovery, only expected tokens are valid | **All `valid_symbols` are set to true** | Scanner must detect and handle this defensively |
| `serialize`/`deserialize` are optional | They're **required** for correct backtracking | Missing implementations cause subtle state corruption in GLR |
| Highlight queries are portable across editors | Capture names **differ** between Neovim, Helix, and Zed | Indent, fold, textobject queries need per-editor variants |
| UTF-8 BOM is handled automatically | Tree-sitter **does not strip BOM** | Consumer must handle U+FEFF at file start |
| Mixed cursor + node API is fine | Mixing **degrades performance** | Stick to cursor-based traversal or node-based, not both |

---

## Defaults

- Positions are zero-based (rows and columns)
- Columns are byte offsets, not character offsets
- `extras` defaults to whitespace regex only (add comments explicitly)
- Query match limit defaults to 65536
- The first rule in `grammar.js` is the start rule
- `_` prefix hides rules from the tree
- `prec()` without `.left`/`.right` defaults to no associativity
- Parse tables use ABI 15 when generated with tree-sitter CLI 0.25+
- Cancellation flag and progress callback are not set by default (parsing runs to completion)
- Highlight priority defaults to 100 (`#set! priority` overrides)
- Pattern priority is last-wins in tree-sitter v0.21+
- Serialization buffer is 1024 bytes (`TREE_SITTER_SERIALIZATION_BUFFER_SIZE`)
- `deserialize` with `length=0` must initialize to default state
- ast-grep `stopBy` defaults to nearest ancestor/descendant (use `end` for unbounded search)
- Grammar `reserved` field: omitted = no contextual keywords
- WASM core is ~252KB; grammar `.wasm` files loaded dynamically
