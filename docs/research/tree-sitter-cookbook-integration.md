---
description: Tree-sitter integration patterns for incremental parsing, multi-language, highlighting, WASM, code navigation, AI/LLM, and concurrency.
tags: [tree-sitter, incremental-parsing, highlighting, WASM, code-navigation, AI, reference]
audience: { human: 50, agent: 50 }
purpose: { reference: 90, research: 10 }
parent: tree-sitter-cookbook.md
---

# Tree-Sitter Cookbook: Integration

Using tree-sitter in editors, tools, AI pipelines, and browser environments.

---

## Incremental Parsing

### Edit Batching

Multiple edits can be applied before re-parsing, but they must be applied **bottom-up** (from the end of the file toward the beginning):

```c
// Given edits at line 50 and line 10:
// Apply line 50 edit FIRST, then line 10
ts_tree_edit(tree, &edit_at_line_50);
ts_tree_edit(tree, &edit_at_line_10);

// Now re-parse once
TSTree *new_tree = ts_parser_parse_string(parser, tree, new_source, new_len);
```

**Why bottom-up:** Each edit shifts byte offsets for everything after it. Applying edits from the end means earlier edits aren't affected by later offset shifts.

After `ts_tree_edit`, any externally stored `TSNode` instances must be updated with `ts_node_edit` using the same `TSInputEdit` values — otherwise their cached positions are stale.

---

### Tree Diffing

After re-parsing, find exactly what changed:

```c
uint32_t range_count;
TSRange *changed = ts_tree_get_changed_ranges(old_tree, new_tree, &range_count);

for (uint32_t i = 0; i < range_count; i++) {
    // Only re-process these ranges (re-highlight, re-lint, etc.)
    process_range(changed[i].start_byte, changed[i].end_byte);
}

free(changed);  // Caller must free
```

**Requirements:**
- The old tree must have been edited via `ts_tree_edit()` before this call
- Ranges may be slightly larger than the exact changes
- The returned array is `malloc`-allocated; caller frees

**Application:** Limit re-highlighting, re-linting, or any tree-derived computation to changed regions instead of re-processing the entire file.

---

### Custom Input Sources

For editors using ropes or piece tables instead of flat strings:

```c
typedef struct {
    Rope *rope;
} InputPayload;

const char *read_from_rope(void *payload, uint32_t byte_index,
                            TSPoint position, uint32_t *bytes_read) {
    InputPayload *p = (InputPayload *)payload;
    // Return a pointer to contiguous bytes starting at byte_index
    // Set *bytes_read to how many contiguous bytes are available
    Chunk chunk = rope_chunk_at(p->rope, byte_index);
    *bytes_read = chunk.length;
    return chunk.data;
}

TSInput input = {
    .payload = &my_payload,
    .read = read_from_rope,
    .encoding = TSInputEncodingUTF8,
};

TSTree *tree = ts_parser_parse(parser, old_tree, input);
```

The read function is called repeatedly. It can return any size chunk — tree-sitter handles buffering. This avoids materializing the entire file into a contiguous buffer.

---

### Timeout and Cancellation

**Timeout (deprecated in favor of progress callback):**

```c
ts_parser_set_timeout_micros(parser, 5000); // 5ms
TSTree *tree = ts_parser_parse_string(parser, old_tree, src, len);
if (tree == NULL) {
    // Timed out — can resume by calling parse again with same args
}
```

**Cancellation flag (cross-thread):**

```c
size_t cancel_flag = 0;
ts_parser_set_cancellation_flag(parser, &cancel_flag);

// From another thread:
cancel_flag = 1; // Causes parse to return NULL
```

**Progress callback (v0.25+, recommended):**

The new approach. The callback fires periodically during parsing and can return `true` to cancel.

**Gotcha:** `ts_subtree_balance()` at parse completion does not check timeout or cancellation. One report showed it processing 1.45M subtrees in 83ms even with a 3ms timeout.

> [tree-sitter issue #4019](https://github.com/tree-sitter/tree-sitter/issues/4019)

---

## Multi-Language Documents

### Language Injection

The application manages injections. Tree-sitter provides the ranges API; the application provides the logic.

**Pattern:**

1. Parse the outer document (e.g., HTML)
2. Run an injection query to find embedded regions:

```scheme
;; injections.scm for HTML
(script_element (raw_text) @injection.content
  (#set! injection.language "javascript"))

(style_element (raw_text) @injection.content
  (#set! injection.language "css"))
```

3. Extract ranges from captures
4. Configure a second parser with those ranges:

```c
TSParser *js_parser = ts_parser_new();
ts_parser_set_language(js_parser, tree_sitter_javascript());
ts_parser_set_included_ranges(js_parser, ranges, range_count);
TSTree *js_tree = ts_parser_parse_string(js_parser, NULL, full_source, full_len);
```

**Rules:**
- Ranges must be ordered (earliest to latest) and non-overlapping
- Each injection layer has its own parser and tree
- Each layer can be incrementally re-parsed independently
- The application must update injection ranges when the outer document changes

**Range boundary detection in external scanners:** When using `ts_parser_set_included_ranges`, the scanner can call `lexer->is_at_included_range_start()` to detect when it has jumped to a disjoint range. The Ruby and JavaScript parsers use this to insert automatic statement terminators between code snippets in ERB templates.

**Limitation:** Injections are always parsed as top-level constructs in the target grammar. There is no built-in way to parse an injection as a specific non-root production. This is an open feature request.

> [Advanced Parsing](https://tree-sitter.github.io/tree-sitter/using-parsers/3-advanced-parsing.html)
> [tree-sitter issue #3625](https://github.com/tree-sitter/tree-sitter/issues/3625)

---

## Highlighting and Injection Authoring

### Highlighting Queries

Highlight queries map AST patterns to highlight groups. The naming convention follows a dotted hierarchy:

```scheme
;; highlights.scm
(function_definition name: (identifier) @function)
(call_expression function: (identifier) @function.call)
(parameter name: (identifier) @variable.parameter)
((identifier) @constant
  (#match? @constant "^[A-Z][A-Z_0-9]*$"))
(comment) @comment
(string) @string
"if" @keyword.conditional
"return" @keyword.return
```

**Priority behavior varies by host:**

| Host | Priority rule | Override pattern |
|------|--------------|-----------------|
| Tree-sitter native (v0.21+) | Last pattern wins (reversed from pre-v0.21) | Put specific overrides after general patterns |
| Neovim | Last pattern wins (matches native v0.21+) | Same |
| Helix | Last pattern wins | Same |
| Zed | Last pattern wins | Same |

**Explicit priority** when order isn't enough:

```scheme
;; Force this pattern to win regardless of position
((identifier) @variable.builtin
  (#any-of? @variable.builtin "self" "cls")
  (#set! priority 200))
```

Default priority is 100. Higher wins. This is the recommended approach when query file organization makes ordering impractical.

> [Syntax Highlighting](https://tree-sitter.github.io/tree-sitter/3-syntax-highlighting.html)

---

### Locals and Scope Tracking

`locals.scm` enables scope-aware highlighting — distinguishing a local variable `x` from a parameter `x` from a global `x`.

```scheme
;; locals.scm for Python
(function_definition) @local.scope
(function_definition
  parameters: (parameters
    (identifier) @local.definition))
(assignment
  left: (identifier) @local.definition)
(identifier) @local.reference
```

**How it works:** The highlighting engine matches references to definitions within their containing scope. If `@local.reference` text-matches a `@local.definition` within the same `@local.scope`, it inherits the definition's highlight group instead of its own.

**Practical use:** Without `locals.scm`, all `identifier` nodes get the same highlight. With it, parameters, local variables, and free variables can be distinguished.

**Modelines for extending:**

```scheme
;; At the top of a query file:
;; extends    — adds to the base queries (doesn't replace them)
;; inherits: python  — imports queries from another language
```

`;extends` is critical for language extensions (e.g., JSX extending JavaScript) — without it, the derived language's queries completely replace the base.

> [Neovim treesitter locals](https://neovim.io/doc/user/treesitter.html#treesitter-query-modeline)

---

### Injection Authoring

Injection queries tell the highlighting engine which ranges contain embedded languages:

```scheme
;; injections.scm for HTML
(script_element
  (raw_text) @injection.content
  (#set! injection.language "javascript"))

(style_element
  (raw_text) @injection.content
  (#set! injection.language "css"))

;; Language from attribute
(script_element
  (attribute
    (attribute_name) @_attr
    (quoted_attribute_value (attribute_value) @injection.language))
  (raw_text) @injection.content
  (#eq? @_attr "type"))
```

**`injection.combined`** — merges multiple disjoint ranges into a single parse. Use for languages scattered across a file (e.g., CSS-in-JS where multiple `css\`...\`` template literals should be parsed as one CSS document).

**`injection.include-children`** — includes child node ranges in the injection. Without this, children of the captured node are excluded from the injected range.

**Known issues:** `injection.combined` has bugs in some hosts — Helix and Neovim have had issues where combined injections cause incorrect highlighting or parse failures. Test thoroughly.

---

### Structural Queries Beyond Highlighting

Tree-sitter queries power more than highlighting in editors:

**Indent queries** (`indents.scm`):

```scheme
;; Neovim convention
(if_statement) @indent.begin
"}" @indent.end
(comment) @indent.auto

;; Helix convention (different capture names)
(if_statement) @indent
"}" @outdent
```

Neovim and Helix use **different capture names** for the same concepts. Query files are not portable between them without translation.

**Fold queries** (`folds.scm`):

```scheme
(function_definition) @fold
(class_definition) @fold
(if_statement) @fold
```

**Textobject queries** (`textobjects.scm`):

```scheme
;; Neovim convention: .inner / .outer
(function_definition body: (_) @function.inner) @function.outer
(class_definition body: (_) @class.inner) @class.outer
(parameter) @parameter.inner

;; Helix convention: .inside / .around
(function_definition body: (_) @function.inside) @function.around
```

Again, naming conventions differ between editors.

---

### Testing Highlight Queries

Tree-sitter has a built-in test format for highlight queries:

```python
# test/highlight/test.py
def foo(x):
#   ^ function
#        ^ variable.parameter
    return x + 1
#   ^^^^^^ keyword.return
#            ^ number
```

**Arrow (`^`) assertions** point at specific characters on the line above. **Caret width** can span multiple characters. The test runner verifies that the character at each caret position has the expected highlight group.

```bash
tree-sitter test   # Runs all tests including highlight tests
```

> [Testing Grammars](https://tree-sitter.github.io/tree-sitter/creating-parsers/5-writing-tests.html)

---

## WASM and Browser Integration

### web-tree-sitter

The WASM build of tree-sitter core is ~252KB. Grammar `.wasm` files are loaded dynamically. The API mirrors the native API but initialization is async:

```javascript
const Parser = require('web-tree-sitter');
await Parser.init();
const parser = new Parser();

const Lang = await Parser.Language.load('/tree-sitter-python.wasm');
parser.setLanguage(Lang);

const tree = parser.parse('def foo(): pass');
console.log(tree.rootNode.toString());

// CRITICAL: explicit cleanup required
tree.delete();
parser.delete();
```

**Memory management is the critical difference.** WebAssembly operates outside the JavaScript GC. Every `Tree`, `TreeCursor`, and `Query` must be explicitly `.delete()`'d. Failure to do so causes memory leaks that are silent on small files but catastrophic at scale.

**Real-world example:** Cosine.sh's indexing service had a silent memory leak for months. The `CallbackInput` class in tree-sitter's `src/parser.cc` lacked a destructor, so `callback` and `partial_string` were never freed. On small projects (~200 lines) this was invisible; at scale it consumed all available memory. The fix was a single destructor addition.

> [web-tree-sitter npm](https://www.npmjs.com/package/web-tree-sitter)
> [Cosine.sh: A silent killer](https://cosine.sh/blog/tree-sitter-memory-leak)

---

### Performance: WASM vs Native

General WASM benchmarks show native is approximately **1.75x–2.5x faster** than WASM. No tree-sitter-specific benchmark exists, but this ratio is a reasonable estimate.

**Zed's hybrid approach:** Load tree-sitter parsers from WASM files but copy static data into native structures, using the WASM engine **only for lexing functions** while the rest runs natively. Gets closer to native performance while maintaining WASM's portability for grammar distribution.

> [Zed blog: Syntax-aware editing](https://zed.dev/blog/syntax-aware-editing)

---

### ABI Compatibility in WASM

ABI version 15 (tree-sitter 0.25) added language name, version, supertype info, and reserved words. WASM modules built with CLI 0.20.x are **incompatible** with web-tree-sitter 0.26.x. A bug in 0.25 caused out-of-bounds memory access when loading older WASM modules because it didn't check ABI version before reading supertype data.

VS Code maintains its own `@vscode/tree-sitter-wasm` package with pre-built WASM files.

> [Issue #5171](https://github.com/tree-sitter/tree-sitter/issues/5171)
> [PR #4195](https://github.com/tree-sitter/tree-sitter/pull/4195)

---

## Code Navigation

### tags.scm — Definition and Reference Extraction

Tag queries live at `queries/tags.scm` in each grammar's repository:

```scheme
(function_definition name: (identifier) @name) @definition.function
(class_definition name: (identifier) @name) @definition.class
(call_expression function: (identifier) @name) @reference.call
(assignment left: (identifier) @name) @definition.variable
```

The `tree-sitter tags` CLI command emits tagged syntactic nodes. GitHub uses this for search-based code navigation (file outlines, jump-to-definition).

**Limitation:** Tags-based navigation is purely textual matching — it cannot resolve imports, follow type hierarchies, or handle dynamic dispatch.

> [Code Navigation](https://tree-sitter.github.io/tree-sitter/4-code-navigation.html)

---

### Stack Graphs — Archived (September 2025)

GitHub's `stack-graphs` crate built on tree-sitter for precise code navigation without a build system. It used `tree-sitter-graph`, a DSL where stanzas match CST patterns and emit graph nodes/edges:

```
(function_definition
  name: (identifier) @name) @func
{
  node @func.def
  attr (@func.def) kind = "definition"
  attr (@func.def) symbol = @name
  edge @func.containing_scope -> @func.def
}
```

**The project was archived September 9, 2025.** GitHub unshipped Precise Code Navigation. The complexity of maintaining `.tsg` grammar files for each language proved unsustainable.

**What survives:** `tree-sitter-graph` was extracted as a separate project because graph construction from syntax trees has general utility beyond name resolution. It remains maintained.

> [github/stack-graphs releases](https://github.com/github/stack-graphs/releases)
> [tree-sitter-graph](https://github.com/tree-sitter/tree-sitter-graph)

---

## AI / LLM Applications

Tree-sitter is the standard tool for structure-aware code processing in AI pipelines.

### AST-Aware Chunking

The dominant pattern: parse with tree-sitter, split along syntactic boundaries, embed the chunks.

**Aider's repo map** — the canonical example:
1. Tree-sitter AST parsing for definition/reference extraction across 40+ languages
2. NetworkX graph with PageRank, personalized to weight files in editing context
3. Token-optimized output using binary search to fit within configurable budgets
4. Sends only the most-referenced identifiers, not full file contents. No GPU, no embeddings, no vector DB required.

> [Aider repo map](https://aider.chat/2023/10/22/repomap.html)

**Cursor's indexing pipeline:**
1. Parse file into AST
2. Traverse depth-first, splitting into sub-trees that fit within token limits
3. Merge sibling nodes into larger chunks when they stay under the limit
4. Compute embeddings with metadata (start/end lines, file path)

> [How Cursor Indexes Codebases Fast](https://read.engineerscodex.com/p/how-cursor-indexes-codebases-fast)

**LlamaIndex CodeSplitter** — the standard RAG framework integration. Uses tree-sitter internally, supports many languages, configurable chunk size.

> [LlamaIndex CodeSplitter docs](https://developers.llamaindex.ai/python/framework-api-reference/node_parsers/code/)

### Tree-Sitter as MCP Index

A developer documented cutting AI context usage by 50x using a tree-sitter code index served via MCP. Precise queries return results like "Engine.cs:45, Player.cs:23" consuming ~50 tokens in 3ms, versus grep returning 200 matches consuming 2000+ tokens with 5+ tool calls in 10+ seconds.

> [DEV Community: How I cut AI context usage by 50x](https://dev.to/uwe_c_39d9ab7d16ff8dfe67e/how-i-cut-ai-context-usage-by-50x-with-a-tree-sitter-code-index-plm)

### Measured Impact

The cAST paper (2025) demonstrates AST-based chunking provides measurable improvements: StarCoder2-7B gains an average of 5.5 points on RepoEval, up to 4.3 points on CrossCodeEval, compared to naive line-based chunking.

> [cAST: AST-based code chunking](https://arxiv.org/html/2506.15655v1)

---

## Structural Diffing (Difftastic)

[Difftastic](https://difftastic.wilfred.me.uk/) uses tree-sitter as a parsing frontend for structural diffs:

1. Tree-sitter produces a CST from source
2. Difftastic converts the CST into a simplified "Syntax" tree: everything is either an **atom** (literals, comments, identifiers) or a **list** (open delimiter + children + close delimiter)
3. Language-agnostic diff algorithm operates on the uniform representation

**Safety valves:**
- Files exceeding `DiffOptions::byte_limit` → falls back to line-oriented diff
- Trees with more than `DiffOptions::parse_error_limit` ERROR nodes → falls back to line-oriented diff

**Application for RepoQL:** Structural diffing could power change analysis — understanding what *kind* of change happened (renamed function, added parameter, changed return type) rather than just line deltas.

> [Difftastic: Tree Diffing](https://difftastic.wilfred.me.uk/tree_diffing.html)

---

## Concurrency Patterns

### Editor Architectures

**Zed's approach** (gold standard for concurrent tree-sitter):
1. Rope data structure backed by `SumTree` — a thread-safe, copy-on-write B+ tree
2. On every edit, a snapshot of buffer text is sent to a background thread for re-parsing
3. Snapshots are cheap — only an `Arc` reference count bump, no data copy
4. Background threads reparse while the main thread continues using the old tree

**Neovim's async parsing** (merged early 2025, PR #31631):
- `vim.treesitter.get_parser()` and `vim.treesitter.start()` no longer block
- Parsing accepts a callback invoked on completion
- Startup time "massively improved" but cursor movement can stall near parse completion

> [Zed blog: Rope & SumTree](https://zed.dev/blog/zed-decoded-rope-sumtree)
> [Neovim PR #31631](https://github.com/neovim/neovim/pull/31631)
