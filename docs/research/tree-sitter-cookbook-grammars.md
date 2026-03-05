---
description: Tree-sitter grammar design, hard parsing problems, external scanners, and grammar composition.
tags: [tree-sitter, grammars, parsing, external-scanners, reference]
audience: { human: 50, agent: 50 }
purpose: { reference: 90, research: 10 }
parent: tree-sitter-cookbook.md
---

# Tree-Sitter Cookbook: Grammar Authoring

Designing grammars, solving hard parsing problems, writing external scanners, and extending existing grammars.

---

## Grammar Design

### Flat Expression Hierarchies

Language specs describe precedence via deeply nested rules (`multiplicative_expression` wrapping `additive_expression`). Tree-sitter works better with a flat structure plus explicit precedence:

```javascript
// Good: flat with explicit precedence
binary_expression: $ => choice(
  prec.left(1,  seq($.expression, '||', $.expression)),
  prec.left(2,  seq($.expression, '&&', $.expression)),
  prec.left(3,  seq($.expression, '==', $.expression)),
  prec.left(3,  seq($.expression, '!=', $.expression)),
  prec.left(4,  seq($.expression, '<',  $.expression)),
  prec.left(4,  seq($.expression, '>',  $.expression)),
  prec.left(5,  seq($.expression, '+',  $.expression)),
  prec.left(5,  seq($.expression, '-',  $.expression)),
  prec.left(6,  seq($.expression, '*',  $.expression)),
  prec.left(6,  seq($.expression, '/',  $.expression)),
),

// Bad: deep nesting
multiplicative_expression: $ => seq(
  $.unary_expression, repeat(seq(choice('*', '/'), $.unary_expression))
),
additive_expression: $ => seq(
  $.multiplicative_expression, repeat(seq(choice('+', '-'), $.multiplicative_expression))
),
```

**Why:** Flat hierarchies produce smaller parse tables, flatter trees (easier to query), and better error recovery. The precedence system handles disambiguation.

> [Writing the Grammar](https://tree-sitter.github.io/tree-sitter/creating-parsers/3-writing-the-grammar.html)

---

### Lexical vs Parse Precedence

The most common source of grammar bugs. Two different systems, confusingly similar syntax.

| Syntax | Controls | When applied |
|--------|----------|-------------|
| `prec(N, rule)` | Which **parse rule** wins | At parse time, resolving shift/reduce conflicts |
| `token(prec(N, rule))` | Which **token** the lexer emits | At lex time, when multiple tokens match at the same position |

```javascript
// PARSE precedence: multiplication binds tighter than addition
binary_expression: $ => choice(
  prec.left(1, seq($.expression, '+', $.expression)),
  prec.left(2, seq($.expression, '*', $.expression)),
),

// LEXICAL precedence: '//' is a comment, not two division operators
comment: $ => token(prec(1, seq('//', /.*/))),
```

**Lexical precedence tie-breaking order:**
1. Higher explicit lexical precedence wins
2. Longer match wins
3. More specific match wins (string literal > regex)
4. Earlier rule definition wins

> [Issue #372](https://github.com/tree-sitter/tree-sitter/issues/372)
> [Tips and Tricks wiki](https://github.com/tree-sitter/tree-sitter/wiki/Tips-and-Tricks-for-a-grammar-author)

---

### Keyword Extraction

Set `word: $ => $.identifier` to enable an optimization for keyword-heavy languages. Tree-sitter detects all keyword strings that also match the word token, and generates a dedicated keyword lookup function instead of individual lexer rules per keyword.

```javascript
module.exports = grammar({
  name: 'my_language',
  word: $ => $.identifier,
  rules: {
    identifier: $ => /[a-zA-Z_]\w*/,
    if_statement: $ => seq('if', ...),     // 'if' auto-detected as keyword
    while_statement: $ => seq('while', ...), // 'while' auto-detected
    // ... dozens of keywords handled efficiently
  },
});
```

**Impact:** Dramatically reduces generated lexer size and compilation time for languages with many keywords. Without this, each keyword string generates its own lexer state.

**Constraint:** The word token must be a unique rule not reused by another rule name.

---

### Conflict Resolution

When tree-sitter reports LR conflicts during generation, you have three options:

**1. Precedence (compile-time):** Most conflicts are operator precedence. Use `prec()`, `prec.left()`, `prec.right()`.

**2. `conflicts` field (runtime GLR):** For genuine ambiguities that can only be resolved at parse time. The parser forks and tries both interpretations simultaneously.

```javascript
module.exports = grammar({
  conflicts: $ => [
    [$.expression, $.type_expression],  // C-style cast ambiguity
  ],
  // ...
});
```

**3. `prec.dynamic()` (runtime preference):** Controls which GLR branch is preferred when both parse successfully. Higher dynamic precedence wins.

```javascript
expression: $ => choice(
  prec.dynamic(1, $.function_call),   // Prefer call over subscript
  prec.dynamic(0, $.subscript),
),
```

**Decision tree:**

```
Is the conflict an operator precedence issue?
├─ Yes → Use prec() / prec.left() / prec.right()
└─ No → Is the ambiguity real (both interpretations are valid parses)?
         ├─ Yes → Add to conflicts[], use prec.dynamic() to prefer one
         └─ No → Restructure the grammar to eliminate the conflict
```

---

### Hidden Rules and Supertypes

**Hidden rules** (`_expression`, `_statement`) are elided from the tree. Use for wrapper rules that just delegate:

```javascript
_expression: $ => choice(
  $.binary_expression,
  $.unary_expression,
  $.call_expression,
  $.identifier,
  $.number,
),
```

**Supertypes** declare abstract categories visible in `node-types.json` but hidden from the tree:

```javascript
supertypes: $ => [$._expression, $._statement],
```

Supertypes enable queries like `(_expression)` to match any expression type. They also improve static analysis: `node-types.json` records which concrete types a supertype can be.

---

### Reserved Words

For languages with contextual keywords (e.g., `async` is a keyword in function declarations but a valid identifier elsewhere):

```javascript
module.exports = grammar({
  reserved: {
    default: ['if', 'else', 'while', 'for', 'return'],
    no_reserved: [],  // Context where no words are reserved
  },
  rules: {
    // 'async' is reserved in function context...
    function_declaration: $ => seq(
      optional(reserved('default', 'async')),
      'function',
      field('name', $.identifier),
      ...
    ),
    // ...but valid as an identifier elsewhere
    _contextual_identifier: $ => reserved('no_reserved', $.identifier),
  },
});
```

This is relatively new. The `reserved` field and `reserved()` function override the global reserved word set per-context.

---

### Grammar Debugging

**Parse graph visualization:**

```bash
tree-sitter parse test.py --debug-graph
# Produces a DOT file showing every parse state transition
```

**State count optimization** — identify which rules bloat the parse table:

```bash
tree-sitter generate --report-states-for-rule expression
# Shows how many parse states that rule is responsible for
```

High state counts per rule indicate either excessive alternatives or precedence ambiguity. Common fix: split the rule or add explicit precedence.

**Understanding conflict messages:**

```
Unresolved conflict for symbol 'expression' detected.
  Possible interpretations:
    1: (call_expression function: expression . '(' arguments ')')
    2: (subscript_expression value: expression . '[' index: expression ']')
```

The `.` shows where the parser cursor is when the conflict occurs. Both interpretations want to proceed from the same state. Resolution: add to `conflicts` field or use `prec.dynamic()`.

**Parser logger** — install a callback that receives every lex/parse event:

```c
void log_callback(void *payload, TSLogType type, const char *message) {
    fprintf(stderr, "[%s] %s\n",
        type == TSLogTypeParse ? "parse" : "lex", message);
}
ts_parser_set_logger(parser, (TSLogger){.payload = NULL, .log = log_callback});
```

**DOT graph output** — visualize the parse stack as a graph:

```c
int fd = open("parse.dot", O_WRONLY | O_CREAT | O_TRUNC, 0644);
ts_parser_print_dot_graphs(parser, fd);
// Then: dot -Tsvg parse.dot > parse.svg
```

**The playground** — `tree-sitter playground` launches a local web server for interactive query debugging. Press `I` to show injection languages, `O` for a query scratchpad.

> [tree-sitter playground](https://tree-sitter.github.io/tree-sitter/7-playground.html)
> [Advanced Parsing](https://tree-sitter.github.io/tree-sitter/using-parsers/3-advanced-parsing.html)

---

## Hard Parsing Problems

Real-world languages have constructs that challenge tree-sitter's GLR parser. These are the proven solutions from production grammars.

### Generics Closing Angle Brackets

C++, Java, TypeScript: `>>` must be treated as two closing `>` in generic contexts but as a right-shift operator elsewhere. `List<Map<K, V>>` vs `x >> 2`.

**Solution — external scanner with context tracking:**

The TypeScript grammar's external scanner tracks angle bracket nesting depth. When parsing `<` inside a type context, it increments a counter. When it sees `>`, it decrements. When it sees `>>`, it can split it into two `>` tokens by advancing only one character and emitting a single `>`.

```c
if (valid_symbols[CLOSE_ANGLE] && lexer->lookahead == '>') {
    lexer->advance(lexer, false);
    if (lexer->lookahead == '>') {
        // Don't consume the second '>' — leave it for the next scan
        lexer->mark_end(lexer);
    }
    lexer->result_symbol = CLOSE_ANGLE;
    return true;
}
```

**Alternative — GLR ambiguity:** Declare the conflict and let both interpretations proceed. Dynamic precedence resolves which wins. More fragile but avoids a scanner.

> [tree-sitter-typescript scanner.c](https://github.com/tree-sitter/tree-sitter-typescript/blob/master/src/scanner.c)

---

### Regex vs Division

JavaScript, Ruby, Perl: `/pattern/flags` vs `a / b`. The lexer can't distinguish without parse context.

**Solution — `valid_symbols` in external scanner:**

The scanner checks `valid_symbols[REGEX]` — the parser knows from grammar context whether a regex is valid at the current position (after `=`, `(`, `,`, `return`, etc. but not after `)`, `]`, identifier, number).

```c
if (valid_symbols[REGEX] && lexer->lookahead == '/') {
    // Parse as regex
    lexer->advance(lexer, false);
    while (lexer->lookahead != '/' && !lexer->eof(lexer)) {
        if (lexer->lookahead == '\\') lexer->advance(lexer, false); // Skip escape
        lexer->advance(lexer, false);
    }
    if (lexer->lookahead == '/') {
        lexer->advance(lexer, false);
        // Consume flags
        while (isalpha(lexer->lookahead)) lexer->advance(lexer, false);
        lexer->result_symbol = REGEX;
        return true;
    }
}
```

The grammar itself is structured so that `REGEX` only appears in `valid_symbols` where an expression start is expected — the parser communicates context to the scanner.

> [tree-sitter-javascript scanner.c](https://github.com/tree-sitter/tree-sitter-javascript/blob/master/src/scanner.c)

---

### Automatic Semicolon Insertion

JavaScript: statements can end with a semicolon, newline (ASI), or certain tokens. The scanner must detect ASI opportunities.

**Solution — external scanner tracking whitespace:**

```c
if (valid_symbols[AUTOMATIC_SEMICOLON]) {
    // ASI triggers on: newline before next token, '}' next, or EOF
    bool newline_seen = false;
    while (lexer->lookahead == ' ' || lexer->lookahead == '\t' ||
           lexer->lookahead == '\r' || lexer->lookahead == '\n') {
        if (lexer->lookahead == '\n') newline_seen = true;
        lexer->advance(lexer, true);  // Skip as whitespace
    }

    if (newline_seen || lexer->lookahead == '}' || lexer->eof(lexer)) {
        lexer->result_symbol = AUTOMATIC_SEMICOLON;
        return true;
    }
}
```

The grammar declares `AUTOMATIC_SEMICOLON` as an alternative to `;` in statement-ending positions:

```javascript
_semicolon: $ => choice(';', $._automatic_semicolon),
```

> [tree-sitter-javascript grammar.js](https://github.com/tree-sitter/tree-sitter-javascript/blob/master/grammar.js)

---

### Dangling Else

C, Java, JavaScript: `if (a) if (b) x; else y;` — which `if` owns the `else`?

**Solution — `prec.right()`:**

```javascript
if_statement: $ => prec.right(seq(
  'if',
  field('condition', $.parenthesized_expression),
  field('consequence', $.statement),
  optional(seq('else', field('alternative', $.statement)))
)),
```

`prec.right()` makes the parser prefer to shift (associate the `else` with the inner `if`) rather than reduce. This matches the standard language semantics.

---

### String Interpolation

Template literals with nested expressions: `` `hello ${name + `nested ${x}`}` `` — arbitrary nesting of interpolation and template boundaries.

**JavaScript approach — grammar recursion:**

```javascript
template_string: $ => seq(
  '`',
  repeat(choice(
    $._template_chars,           // Literal text
    $.template_substitution,     // ${...}
    $.escape_sequence,
  )),
  '`'
),

template_substitution: $ => seq(
  '${',
  $.expression,  // Can contain another template_string
  '}',
),
```

The external scanner handles `_template_chars` (everything between `${` and `` ` `` boundaries) while the grammar handles the recursive structure. The scanner tracks nesting depth to know which `}` closes an interpolation vs a block.

**Ruby approach — literal stack in scanner:**

Ruby's scanner maintains a stack of literal contexts (single-quoted, double-quoted, heredoc, regex, etc.), each with its own nesting depth and delimiter tracking. This is necessary because Ruby has more string literal types than most languages.

> [tree-sitter-ruby scanner.c](https://github.com/tree-sitter/tree-sitter-ruby/blob/master/src/scanner.c)

---

### Context-Sensitive Keywords

Languages where `async`, `yield`, `get`, `set` are keywords only in certain positions.

**Modern approach — `reserved()` function (v0.25+):**

See [Reserved Words](#reserved-words) section above.

**Older approach — separate rules per context:**

```javascript
// 'get' and 'set' are keywords in property definitions but identifiers elsewhere
method_definition: $ => seq(
  optional(choice('get', 'set', 'async')),
  field('name', $.property_name),
  ...
),

// identifier rule matches 'get', 'set', 'async' as regular identifiers
identifier: $ => /[a-zA-Z_$][\w$]*/,
```

The lexical precedence system ensures that when the parser expects a keyword, the keyword token wins. When the parser expects an identifier (and no keyword is valid), the identifier token wins.

---

### Macro Systems

The hardest parsing problem. C/C++ preprocessor and Rust macros create syntax that isn't valid in the base grammar.

**C/C++ approach — preprocessing as grammar rules:**

The tree-sitter-c grammar defines preprocessor directives as grammar rules that can appear in many positions:

```javascript
_top_level_item: $ => choice(
  $.function_definition,
  $.declaration,
  $.preproc_include,
  $.preproc_def,
  $.preproc_function_def,
  $.preproc_if,    // Can wrap other top-level items
  // ...
),
```

`preproc_if` wraps sequences of other items, creating a tree that represents the preprocessor structure. This means the tree doesn't resolve macros — it represents them structurally.

**Rust approach — `token_tree`:**

The Rust grammar defines `token_tree` as a balanced-delimiter expression that captures everything inside macro invocations without parsing it:

```javascript
macro_invocation: $ => seq(
  field('macro', $.identifier),
  '!',
  $.token_tree,
),

token_tree: $ => choice(
  seq('(', repeat($.token_tree), ')'),
  seq('[', repeat($.token_tree), ']'),
  seq('{', repeat($.token_tree), '}'),
  $._non_delimiter_token,
),
```

This is the only practical approach — macro bodies follow arbitrary syntax that the grammar can't know.

> [tree-sitter-rust grammar.js](https://github.com/tree-sitter/tree-sitter-rust/blob/master/grammar.js)
> [tree-sitter-c grammar.js](https://github.com/tree-sitter/tree-sitter-c/blob/master/grammar.js)

---

## External Scanners

### Indentation Tracking

For Python, YAML, Haskell-style languages. The scanner maintains an indent stack.

**Core state:**

```c
typedef struct {
    int32_t *indent_stack;
    uint32_t indent_length;
    uint32_t indent_capacity;
} Scanner;
```

**Serialization** must capture the full indent stack:

```c
unsigned serialize(void *payload, char *buffer) {
    Scanner *s = (Scanner *)payload;
    uint32_t size = s->indent_length * sizeof(int32_t);
    if (size > TREE_SITTER_SERIALIZATION_BUFFER_SIZE) return 0;
    memcpy(buffer, s->indent_stack, size);
    return size;
}
```

**The scan function** emits `INDENT`, `DEDENT`, and `NEWLINE` tokens by comparing current column to the top of the indent stack:

```c
bool scan(void *payload, TSLexer *lexer, const bool *valid_symbols) {
    Scanner *s = (Scanner *)payload;
    // Skip whitespace at start of line
    while (lexer->lookahead == ' ' || lexer->lookahead == '\t')
        lexer->advance(lexer, true);

    uint32_t col = lexer->get_column(lexer);
    int32_t current_indent = s->indent_stack[s->indent_length - 1];

    if (valid_symbols[INDENT] && col > current_indent) {
        push_indent(s, col);
        lexer->result_symbol = INDENT;
        return true;
    }
    if (valid_symbols[DEDENT] && col < current_indent) {
        pop_indent(s);
        lexer->result_symbol = DEDENT;
        return true;
    }
    // ...
}
```

**Gotcha:** During GLR parsing, tree-sitter may call the scanner with **every symbol marked as valid**. Your scan function must handle this gracefully — check `valid_symbols` defensively and don't assume only expected tokens are valid.

> [External Scanners](https://tree-sitter.github.io/tree-sitter/creating-parsers/4-external-scanners.html)
> [tree-sitter-python scanner](https://github.com/tree-sitter/tree-sitter-python/blob/master/src/scanner.c)
> [Discussion #1215](https://github.com/tree-sitter/tree-sitter/discussions/1215)

---

### Nesting Depth Tracking

For template literals with interpolation (`${...}`), heredocs, and similar constructs. The scanner tracks nesting depth to distinguish closing delimiters from nested ones.

```c
typedef struct {
    uint8_t template_depth;  // Current ${...} nesting level
} Scanner;

bool scan(void *payload, TSLexer *lexer, const bool *valid_symbols) {
    Scanner *s = (Scanner *)payload;

    if (valid_symbols[TEMPLATE_CHARS]) {
        bool has_content = false;
        while (true) {
            if (lexer->lookahead == '$') {
                lexer->mark_end(lexer);
                lexer->advance(lexer, false);
                if (lexer->lookahead == '{') {
                    // Start of interpolation — don't consume
                    break;
                }
                has_content = true;
            } else if (lexer->lookahead == '`') {
                // End of template — don't consume
                lexer->mark_end(lexer);
                break;
            } else if (lexer->eof(lexer)) {
                break;
            } else {
                has_content = true;
                lexer->advance(lexer, false);
            }
        }
        if (has_content) {
            lexer->result_symbol = TEMPLATE_CHARS;
            return true;
        }
    }
    return false;
}
```

**Key technique:** `lexer->mark_end(lexer)` lets you peek ahead without committing. Mark the end at a safe point, then advance to inspect what follows. If the lookahead isn't what you want, the token ends at the mark.

> [tree-sitter-javascript scanner.c](https://github.com/tree-sitter/tree-sitter-javascript/blob/master/src/scanner.c)

---

### Scanner State Serialization

The buffer is limited to `TREE_SITTER_SERIALIZATION_BUFFER_SIZE` (1024 bytes). Serialization must be **deterministic** — same state must produce identical bytes. This is used for backtracking during GLR parsing.

**Rules:**
- `deserialize` with `length=0` must initialize to default state
- Serialize/deserialize must be inverses
- Use `ts_malloc`/`ts_free` from `tree_sitter/alloc.h`, not libc versions
- Use array macros from `tree_sitter/array.h` for dynamic collections
- Write C, not C++. The Haskell grammar saw a 52.8x speedup by moving from C++ to C because `std::function` constructors triggered excessive `malloc` calls.

> [owen.cafe: tree-sitter-haskell perf](https://owen.cafe/posts/tree-sitter-haskell-perf/)

---

## Grammar Composition

### Extending Existing Grammars

The TypeScript grammar is the canonical example — it imports the JavaScript grammar and extends it:

```javascript
// common/define-grammar.js
const base = require('tree-sitter-javascript/grammar');

module.exports = grammar(base, {
  name: 'typescript',

  rules: {
    // Override: add type annotation to function parameters
    formal_parameters: ($, previous) => seq(
      '(',
      commaSep(choice(
        $.required_parameter,
        $.optional_parameter,
        $.rest_parameter,
      )),
      ')'
    ),

    // Extend: add new alternatives to expression
    expression: ($, previous) => choice(
      ...previous.members,     // Spread all JavaScript expressions
      $.as_expression,         // Add TypeScript-specific ones
      $.type_assertion,
    ),
  },

  // Externals are concatenated with parent's
  externals: ($, previous) => previous.concat([
    $.automatic_semicolon,
  ]),

  // Conflicts are concatenated
  conflicts: ($, previous) => previous.concat([
    [$.expression, $.type_expression],
  ]),
});
```

**Extension mechanisms:**

| Field | Behavior | Access to parent |
|-------|----------|-----------------|
| `rules` | Override or add rules | `($, previous)` — `previous` is the old rule |
| `externals` | Concatenated with parent's | `($, previous)` — `previous` is parent array |
| `conflicts` | Concatenated | `($, previous)` — `previous` is parent array |
| `supertypes` | Appended | Direct list |
| `precedences` | Appended | Direct list |

**Pitfalls:**
- Parsing conflicts must be declared exhaustively — the GLR algorithm handles ambiguity but missing conflict declarations cause generation errors
- Grammars using `require()` need the parent grammar npm-installed; broken paths cause cryptic errors
- Grammar extension is [undocumented](https://github.com/tree-sitter/tree-sitter/issues/645) — the TypeScript and C++ grammars are the de facto reference

> [tree-sitter-typescript](https://github.com/tree-sitter/tree-sitter-typescript)
> [tree-sitter-cpp](https://github.com/tree-sitter/tree-sitter-cpp)
