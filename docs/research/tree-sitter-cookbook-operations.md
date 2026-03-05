---
description: Tree-sitter error handling, performance, production edge cases, and version migration.
tags: [tree-sitter, error-handling, performance, production, reference]
audience: { human: 50, agent: 50 }
purpose: { reference: 90, research: 10 }
parent: tree-sitter-cookbook.md
---

# Tree-Sitter Cookbook: Operations

Error handling, performance tuning, production edge cases, and running tree-sitter reliably.

---

## Error Handling

### Error Node Analysis

Tree-sitter always produces a complete tree. Errors appear as specific node types:

| Node | Detection | Meaning |
|------|-----------|---------|
| `ERROR` | `ts_node_is_error()` | Span of text the parser couldn't incorporate |
| `MISSING` | `ts_node_is_missing()` | Token expected but absent (zero-width) |
| Either | `ts_node_has_error()` | Node or any descendant contains errors |

**Quality assessment pattern:**

```c
void count_errors(TSNode node, int *error_count, int *missing_count) {
    if (ts_node_is_error(node)) (*error_count)++;
    if (ts_node_is_missing(node)) (*missing_count)++;

    uint32_t child_count = ts_node_child_count(node);
    for (uint32_t i = 0; i < child_count; i++) {
        count_errors(ts_node_child(node, i), error_count, missing_count);
    }
}
```

**In queries:** `(ERROR)` matches error nodes, `(MISSING)` matches missing nodes.

**What you can't control:** The error recovery algorithm's cost-based decisions. This is a black box. Well-structured grammars with more specific rules tend to recover better, but there is no mechanism for grammar authors to provide recovery hints.

> [Issue #1870](https://github.com/tree-sitter/tree-sitter/issues/1870)
> [Pulsar blog part 7](https://blog.pulsar-edit.dev/posts/20240902-savetheclocktower-modern-tree-sitter-part-7/)

---

### Working Around Poor Recovery

When error recovery produces unusable trees for common incomplete patterns:

**Strategy 1 — Multiple parse attempts.** Parse the original. If error rate is high, try inserting likely missing tokens (closing braces, semicolons) and re-parse. Compare error counts.

**Strategy 2 — Partial extraction.** Walk the tree and only extract from subtrees where `ts_node_has_error()` is false. Skip error-containing subtrees entirely.

**Strategy 3 — Scope narrowing.** Use `ts_node_descendant_for_byte_range()` to find the innermost error-free node containing a region of interest, then extract from there.

---

## Performance

### Thread-Safe Parser Pools

`TSParser` is not thread-safe. The standard pattern for concurrent parsing:

```csharp
// C# with TreeSitter.DotNet
private static readonly Language SharedLanguage = new Language("python");
private static readonly ThreadLocal<Parser> Parsers = new(
    () => new Parser(SharedLanguage));

public Tree Parse(string source) {
    return Parsers.Value!.Parse(source);
}
```

**Why `ThreadLocal`:** Language objects are thread-safe and should be shared (they're large, read-only data). Parsers are cheap to create but carry state — one per thread is the right granularity.

**Cross-thread tree access:** Use `ts_tree_copy()` (cheap, COW semantics) before handing a tree to another thread. Do not share trees across threads without copying.

---

### Memory Awareness

| Input | Memory concern |
|-------|---------------|
| < 100KB | No concern |
| 100KB - 1MB | Expect 50-200x tree-to-source ratio |
| 1MB - 10MB | Consider streaming or chunking |
| > 10MB | Tree-sitter may not be the right tool |

A 1.6MB JSON file reportedly produced ~300MB of tree memory. CSTs are verbose — every token gets a node. For large generated files, consider parsing only the portions you need via `ts_parser_set_included_ranges()`.

> [Issue #1277](https://github.com/tree-sitter/tree-sitter/issues/1277)

---

## Production Edge Cases

### Known Pathological Inputs

| Grammar | Trigger | Symptom | Workaround |
|---------|---------|---------|------------|
| Python | Tabs instead of spaces | Deadlock, hundreds of GB memory, OOM crash | Replace tabs with spaces before parsing |
| YAML (Zed) | Text insertion before comment/doc-start markers | Memory leak, retained by `ts_malloc_default` | Parser restart or file close |
| Ruby | Specific inputs via fuzzer | Memory leak in `parser__do_all_potential_reductions` | Fixed in tree-sitter core (issue #132) |
| Any | Fuzzer-generated input | Infinite error recovery loop, never makes progress | Timeout + cancellation flag |

> [tree-sitter-python #207](https://github.com/tree-sitter/tree-sitter-python/issues/207)
> [Zed #24742](https://github.com/zed-industries/zed/issues/24742)
> [tree-sitter #2073](https://github.com/tree-sitter/tree-sitter/discussions/2073)

### Defensive Parsing

For production systems parsing untrusted input:

```c
// 1. Set timeout
ts_parser_set_timeout_micros(parser, 50000); // 50ms

// 2. Set cancellation flag (for external thread control)
size_t cancel_flag = 0;
ts_parser_set_cancellation_flag(parser, &cancel_flag);

// 3. Parse — returns NULL if timeout/cancelled
TSTree *tree = ts_parser_parse_string(parser, old_tree, src, len);

if (tree == NULL) {
    // Can resume from where it stopped:
    tree = ts_parser_parse_string(parser, old_tree, src, len);

    // Or reset to start fresh:
    ts_parser_reset(parser);
    tree = ts_parser_parse_string(parser, NULL, src, len);
}
```

**Gotcha:** `ts_subtree_balance()` at parse completion does **not** check timeout or cancellation. Parsing can exceed the specified timeout during the final balancing phase.

> [Issue #4019](https://github.com/tree-sitter/tree-sitter/issues/4019)

---

### Version Migration

Tree-sitter has not reached v1.0. Key breaking changes:

| Version | Breaking change |
|---------|----------------|
| 0.21.0 | Removed `apply-all-captures` flag; last-wins precedence became default |
| 0.22.0 | node-tree-sitter switched from NAN to Node-API; requires `tree-sitter generate` re-run |
| 0.25.0 | ABI bumped to 15; added language name, version, supertypes, reserved words; requires `tree-sitter.json` config |
| 0.25.0 (Python) | `Query.match_limit` moved to `QueryCursor`; `LookaheadIterator.iter_names()` → `.names()` |

> [tree-sitter releases](https://github.com/tree-sitter/tree-sitter/releases)
