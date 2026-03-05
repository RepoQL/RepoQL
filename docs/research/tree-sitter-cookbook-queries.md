---
description: Tree-sitter query patterns, ast-grep structural search, and tree traversal techniques.
tags: [tree-sitter, queries, ast-grep, traversal, reference]
audience: { human: 50, agent: 50 }
purpose: { reference: 90, research: 10 }
parent: tree-sitter-cookbook.md
---

# Tree-Sitter Cookbook: Queries & Traversal

Pattern matching, structural search, and navigating parsed trees.

---

## Queries

### Combined Queries

Concatenate multiple patterns into a single query string. Execute once with one cursor walk. Route results by pattern index.

```scheme
;; Pattern 0: function definitions
(function_definition
  name: (identifier) @fn.name) @fn.def

;; Pattern 1: class definitions
(class_definition
  name: (identifier) @class.name) @class.def

;; Pattern 2: imports
(import_statement
  name: (dotted_name) @import.path)
```

```csharp
var query = new Query(language, combinedPatternString);
foreach (var match in query.Execute(tree.RootNode).Matches)
{
    switch (match.PatternIndex)
    {
        case 0: HandleFunction(match); break;
        case 1: HandleClass(match); break;
        case 2: HandleImport(match); break;
    }
}
```

**Why this matters:** One depth-first walk instead of N. For a file with 1000 nodes and 10 patterns, this is 1000 node visits instead of 10,000.

> [Query API](https://tree-sitter.github.io/tree-sitter/using-parsers/queries/4-api.html)

---

### Field Negation

A `!` prefix before a field name asserts the field is **not present**. This matches structural absence, not empty values.

```scheme
;; Non-generic struct (no type parameters)
(struct_item
  name: (type_identifier) @name
  !type_parameters)

;; Arrow functions without parenthesized parameters
(arrow_function
  !parameters
  parameter: (identifier) @param)

;; Variable declarations without initializers
(variable_declarator
  name: (identifier) @name
  !value)
```

Field negation order relative to other children doesn't matter. You can place `!field` anywhere within the parent pattern.

> [PR #983](https://github.com/tree-sitter/tree-sitter/pull/983)

---

### Anchors

The `.` operator constrains child position. It only considers **named** nodes — anonymous nodes (keywords, operators) are ignored.

```scheme
;; First named child of arguments
(arguments . (identifier) @first_arg)

;; Last statement in a block
(block (expression_statement) @last .)

;; Two immediately adjacent siblings (no named nodes between them)
(block
  (expression_statement) @a
  .
  (expression_statement) @b)
```

**Use case — doc comments:** Match a comment only when it immediately precedes a function:

```scheme
((comment) @doc . (function_definition) @fn)
```

Without the anchor, this would match any comment and any function that happen to be siblings, regardless of what's between them.

> [Operators](https://tree-sitter.github.io/tree-sitter/using-parsers/queries/2-operators.html)

---

### Predicate Composition

Predicates filter matches after structural pattern matching. Layer them for precision.

**Text equality between captures:**

```scheme
;; Self-assignment detection (left and right are the same identifier)
(assignment_expression
  left: (identifier) @left
  right: (identifier) @right
  (#eq? @left @right))
```

**Regex on captured text:**

```scheme
;; Constants: identifiers in ALL_CAPS
((identifier) @constant
  (#match? @constant "^[A-Z][A-Z_0-9]*$"))
```

**Set membership (faster than multiple `#eq?`):**

```scheme
;; Built-in functions
((identifier) @function.builtin
  (#any-of? @function.builtin
    "print" "len" "range" "type" "isinstance"
    "getattr" "setattr" "hasattr" "delattr"))
```

**Quantified capture predicates:** By default, predicates on quantified captures (`+`, `*`) check **all** captured nodes. Use `#any-` prefix to match if **any** single node satisfies:

```scheme
;; Match decorator list where ANY decorator is @override
(decorated_definition
  (decorator (identifier) @dec)+
  (#any-eq? @dec "override"))
```

**Host-defined predicates vary.** The C library exposes predicates as data but does not evaluate them. Common extensions:

| Host | Custom predicates |
|------|------------------|
| Neovim | `#lua-match?`, `#vim-match?`, `#contains?` |
| Helix | Standard set only |
| Zed | Standard set only |

> [Predicates and Directives](https://tree-sitter.github.io/tree-sitter/using-parsers/queries/3-predicates-and-directives.html)

---

### Pattern Priority

Tree-sitter's official behavior is **first pattern wins** (earlier patterns take precedence). Neovim and Zed use **last wins** (later patterns override earlier ones).

When writing queries for a specific host, put your fallback patterns first and specific overrides last (for Neovim/Zed) or vice versa (for native tree-sitter highlighting).

When writing host-agnostic queries (e.g., for code analysis), avoid relying on priority — make patterns mutually exclusive through predicates or structural differences.

> [Helix issue #9436](https://github.com/helix-editor/helix/issues/9436)

---

### Query Performance

**Reuse `QueryCursor`:** Creating a cursor is not free. Create once, execute many times.

**Restrict matching range:** When you only need results for a visible region or a specific function body:

```csharp
cursor.SetByteRange(startByte, endByte);
cursor.SetPointRange(startPoint, endPoint);
// Returns matches that INTERSECT the range
```

**Limit match depth:** Prevent starting matches deep in the tree when top-level patterns suffice:

```csharp
cursor.SetMaxStartDepth(3); // Only match within 3 levels of root
```

**Cap in-progress matches:** The match limit defaults to 65536. Neovim's experience: raising from 32 to 64K caused severe performance issues for some highlighting queries. Tune for your use case:

```csharp
cursor.SetMatchLimit(256);
// Check if limit was hit:
if (cursor.DidExceedMatchLimit()) { /* handle */ }
```

**Disable unused patterns:** If a combined query has patterns you don't need for a particular execution:

```csharp
query.DisablePattern(patternIndex); // Cannot be undone on this Query instance
```

**`matches()` vs `captures()`:** `matches()` groups captures into their pattern matches (useful when captures relate to each other). `captures()` returns a flat ordered sequence. Use `captures()` when you just want all tagged nodes regardless of pattern grouping — it avoids the match-grouping overhead.

> [QueryCursor Rust docs](https://docs.rs/tree-sitter/latest/tree_sitter/struct.QueryCursor.html)
> [Neovim PR #14915](https://github.com/neovim/neovim/pull/14915)

---

### Common Query Patterns

**Doc comment attached to declaration:**

```scheme
((comment) @doc
  .
  [
    (function_definition name: (identifier) @name)
    (class_definition name: (identifier) @name)
  ] @definition)
```

**TODO/FIXME comments:**

```scheme
((comment) @comment.todo
  (#match? @comment "TODO|FIXME|HACK|XXX|BUG"))
```

**Method calls on a specific receiver:**

```scheme
(call_expression
  function: (member_expression
    object: (identifier) @receiver
    property: (property_identifier) @method)
  (#eq? @receiver "console"))
```

**Deeply nested access chains (arbitrary depth):**

```scheme
;; This only matches one level of member access. Tree-sitter queries
;; cannot express unbounded recursion. For deep chains, match individual
;; levels and reconstruct in application code.
(member_expression
  object: (member_expression
    object: (identifier) @root
    property: (property_identifier) @mid)
  property: (property_identifier) @leaf)
```

**Exports (JavaScript/TypeScript):**

```scheme
(export_statement
  declaration: [
    (function_declaration name: (identifier) @name)
    (class_declaration name: (identifier) @name)
    (lexical_declaration
      (variable_declarator name: (identifier) @name))
  ]) @export
```

---

## ast-grep Patterns

[ast-grep](https://ast-grep.github.io/) is a structural search/replace tool built on tree-sitter. Where tree-sitter's S-expression queries match syntax tree structure, ast-grep matches **source code patterns** — what you'd write to find code that looks like something.

### Meta-Variables

Meta-variables capture AST nodes during matching:

| Syntax | Captures | Example pattern | Matches |
|--------|----------|-----------------|---------|
| `$VAR` | Single node | `console.log($MSG)` | `console.log("hello")`, `console.log(x + 1)` |
| `$$ARGS` | Zero or more nodes | `foo($$ARGS)` | `foo()`, `foo(1)`, `foo(1, 2, 3)` |
| `$_` | Single node (unnamed) | `if ($_) { $$$ }` | Any if statement |
| `$$$` | Zero or more (unnamed) | `{ $$$ }` | Any block content |

`$VAR` is named — the same `$VAR` in a pattern must match structurally identical nodes. `$_` and `$$$` are anonymous — they match without binding.

```yaml
# Find duplicate object keys
rule:
  kind: pair
  pattern: "$KEY: $VAL"
  inside:
    kind: object
    has:
      kind: pair
      pattern: "$KEY: $_"  # Same $KEY, different value
      stopBy: end
```

> [ast-grep pattern syntax](https://ast-grep.github.io/guide/pattern-syntax.html)

---

### Rule Composition

YAML rules compose with `all`, `any`, `not`, and `matches` (named subrules):

```yaml
# Find async functions that don't have error handling
rule:
  all:
    - kind: function_declaration
    - has:
        kind: await_expression
    - not:
        has:
          kind: try_statement

# Match multiple patterns with 'any'
rule:
  any:
    - pattern: console.log($$$)
    - pattern: console.warn($$$)
    - pattern: console.error($$$)

# Named subrules via 'matches'
utils:
  is-test-file:
    rule:
      kind: call_expression
      pattern: describe($$$)
rule:
  matches: is-test-file
```

---

### Relational Rules

Constrain matches based on surrounding tree structure:

| Operator | Meaning | `stopBy` behavior |
|----------|---------|-------------------|
| `inside` | Match must be inside an ancestor | `end` = any ancestor, `neighbor` = direct parent |
| `has` | Match must have a descendant | `end` = any descendant, `neighbor` = direct child |
| `follows` | Match must follow a sibling | `end` = any preceding sibling, `neighbor` = immediately preceding |
| `precedes` | Match must precede a sibling | `end` = any following sibling, `neighbor` = immediately following |

```yaml
# Find 'await' inside non-async functions
rule:
  kind: await_expression
  inside:
    kind: function_declaration
    not:
      has:
        field: async
    stopBy: end
```

The `stopBy` field is critical for precision. Without it, `inside` walks up to the root.

> [ast-grep relational rules](https://ast-grep.github.io/guide/rule-config/relational-rule.html)

---

### Rewriting

The `fix` field provides structural replacement:

```yaml
id: no-console-log
language: javascript
rule:
  pattern: console.log($$$ARGS)
fix: "logger.debug($$$ARGS)"
```

**Transformations** manipulate captured text before insertion:

```yaml
id: snake-to-camel
rule:
  pattern: $FUNC($$$)
  regex: "^[a-z]+_[a-z]"
transform:
  CAMEL:
    convert:
      source: $FUNC
      toCase: camelCase
fix: "$CAMEL($$$)"
```

> [ast-grep rewriter](https://ast-grep.github.io/guide/rewrite.html)

---

### CI Integration

ast-grep outputs SARIF for CI pipelines:

```bash
# GitHub Actions
ast-grep scan --report-style sarif -o results.sarif
# JSON for custom processing
ast-grep scan --json
```

Rules live in `sgconfig.yml` at repo root. Each rule file is a YAML document in `rules/`.

> [ast-grep CLI reference](https://ast-grep.github.io/reference/cli.html)

---

## Tree Traversal

### Cursor-Based Traversal

`TSTreeCursor` is the efficient way to walk trees. Unlike repeated `ts_node_child()` calls (which allocate nodes per call), cursors navigate in-place.

```c
TSTreeCursor cursor = ts_tree_cursor_new(ts_tree_root_node(tree));

// Visitor pattern: depth-first with enter/exit callbacks
bool going_up = false;
while (true) {
    TSNode node = ts_tree_cursor_current_node(&cursor);

    if (!going_up) {
        // ENTER — process node before children
        on_enter(node);
    }

    if (!going_up && ts_tree_cursor_goto_first_child(&cursor)) {
        continue;  // Descend to first child
    }

    // EXIT — process node after all children
    on_exit(ts_tree_cursor_current_node(&cursor));

    if (ts_tree_cursor_goto_next_sibling(&cursor)) {
        going_up = false;  // Move to sibling, reset direction
    } else if (ts_tree_cursor_goto_parent(&cursor)) {
        going_up = true;   // No more siblings, ascend
    } else {
        break;  // Back at root, done
    }
}

ts_tree_cursor_delete(&cursor);
```

**Why this matters:** `ts_node_parent()` is O(depth) because nodes don't store parent pointers — tree-sitter re-walks from the root. Cursors maintain position, making parent traversal O(1).

> [TSTreeCursor API](https://tree-sitter.github.io/tree-sitter/using-parsers/2-basic-parsing.html#walking-trees-with-tree-cursors)

---

### Efficient Node Lookup

Jump directly to a node at a specific position without walking the full tree:

```c
// By byte offset — finds the smallest node spanning the range
TSNode node = ts_node_descendant_for_byte_range(root, start_byte, end_byte);

// By row/column point — same behavior, different coordinate space
TSPoint start = { .row = 10, .column = 5 };
TSPoint end = { .row = 10, .column = 5 };
TSNode node = ts_node_descendant_for_point_range(root, start, end);

// Named variant — skips anonymous nodes (operators, keywords)
TSNode named = ts_node_named_descendant_for_byte_range(root, start_byte, end_byte);
```

**Cursor equivalent** for walking to a position within children:

```c
TSTreeCursor cursor = ts_tree_cursor_new(parent_node);
// Jump directly to the child containing byte offset 500
int64_t child_index = ts_tree_cursor_goto_first_child_for_byte(&cursor, 500);
// Returns the child index, or -1 if no child contains that offset
```

**Use case:** Editor "go to definition" — user clicks at byte 1234, find the innermost node, determine its kind, run appropriate resolution logic. No tree walk needed.

---

### node-types.json Structure

Every grammar generates `node-types.json` — the complete type system of the syntax tree. Useful for code generation, validation, and building typed wrappers.

```json
[
  {
    "type": "function_definition",
    "named": true,
    "fields": {
      "name": { "multiple": false, "required": true, "types": [{"type": "identifier", "named": true}] },
      "parameters": { "multiple": false, "required": true, "types": [{"type": "parameters", "named": true}] },
      "body": { "multiple": false, "required": true, "types": [{"type": "block", "named": true}] },
      "return_type": { "multiple": false, "required": false, "types": [{"type": "type", "named": true}] }
    },
    "children": { "multiple": true, "required": false, "types": [{"type": "decorator", "named": true}] }
  },
  {
    "type": "identifier",
    "named": true
  },
  {
    "type": "+",
    "named": false
  }
]
```

**Three node categories:**

| Category | `named` | Has `fields` or `children` | Example |
|----------|---------|---------------------------|---------|
| Product type | `true` | Yes — `fields` and/or `children` | `function_definition`, `binary_expression` |
| Leaf node | `true` | No | `identifier`, `string_literal` |
| Anonymous | `false` | No | `+`, `if`, `{` |

**Supertype nodes** appear with `subtypes` listing all concrete alternatives:

```json
{
  "type": "_expression",
  "named": true,
  "subtypes": [
    {"type": "binary_expression", "named": true},
    {"type": "call_expression", "named": true},
    {"type": "identifier", "named": true}
  ]
}
```

**Application:** GitHub's Semantic, CodeQL, and [type-sitter](https://github.com/type-sitter/type-sitter) all generate typed APIs from `node-types.json`. If you're building a typed wrapper for tree-sitter in any language, this file is the schema.

> [node-types.json spec](https://tree-sitter.github.io/tree-sitter/using-parsers/6-static-node-types.html)
