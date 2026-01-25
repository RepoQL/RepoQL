# Read Tool: What Great Looks Like

> Select content with patterns, transform with modifiers, control scope with token budget.

The read tool is the agent's primary interface for retrieving and displaying repository content. It combines three elements into a unified syntax:

```
read("<pattern> => <modifier>: <parameter>", tokenBudget)
```

- **Pattern**: URI or glob selecting files/symbols
- **Modifier**: How to transform or query the selected content
- **Token budget**: Controls output size and representation depth

When the modifier is omitted, read auto-selects the richest representation that fits the budget.

---

## The Core Contract

**Pattern selects. Modifier transforms. Budget constrains.**

```
Pattern                         What it selects
─────────────────────────────   ─────────────────────────────
file:///src/Auth.cs             Single file
file:///src/**/*.cs             All C# files under src/
file:///src/Auth.cs#symbol=Foo  Specific symbol in file
file:///src/**/*.cs#symbol=**   All symbols in matched files
```

When results won't fit the budget, read should:
1. For representation modifiers: Request confirmation before truncating
2. For search modifiers: Show top results with count of omitted matches
3. For graph modifiers: Show nearest nodes with indication of depth omitted

---

## Modifier Categories

### Representation Modifiers

Control how selected content is displayed.

| Modifier | Purpose |
|----------|---------|
| *(default)* | Auto-select representation based on budget |
| `=> headline` | Single-line summary per file |
| `=> structure` | Hierarchical outline with signatures and fragments |
| `=> content` | Full source code |
| `=> tree` | Directory tree with progressive verbosity |

**What "great" looks like:**

| Modifier | Great Outcome |
|----------|---------------|
| **default** | Agent gets maximum insight without specifying format; never pays for representation they don't need |
| **headline** | Scanning 100 files costs ~500 tokens; every element aids filtering decisions |
| **structure** | Agent navigates directly to the right symbol without reading surrounding code |
| **content** | Agent sees exactly what they need; line numbers enable precise follow-up |
| **tree** | Agent understands module organization at a glance; verbosity adapts to budget (full headlines → filenames → folder counts) |

---

### Search Modifiers

Find content within selected files.

| Modifier | Purpose |
|----------|---------|
| `=> question: <q>` | LLM-synthesized answer with citations |
| `=> find: <keywords>` | Semantic search—locate where concepts appear |
| `=> grep: <pattern>` | Literal string match |
| `=> regex: <pattern>` | Regular expression match |
| `=> astgrep: <pattern>` | Syntax-aware structural match |

**What "great" looks like:**

| Modifier | Great Outcome |
|----------|---------------|
| **question:** | Agent gets accurate answer synthesized from multiple locations; citations are precise URIs they can follow |
| **find:** | Agent locates semantic matches even when terminology varies; results zoom to the exact relevant span, not whole chunks |
| **grep:** | Agent finds all literal occurrences; results include surrounding context for understanding |
| **regex:** | Agent matches complex patterns; captures are highlighted in results |
| **astgrep:** | Agent finds structural patterns (all functions returning Task, all try-catch blocks) regardless of formatting/naming |

---

### Graph Modifiers

Traverse relationships from selected content.

| Modifier | Purpose |
|----------|---------|
| `=> <edge_type>` | Follow edges of specified type (callers, callees, uses, usedBy, etc.) |
| `=> roots` | Walk up call/use graph to find entry points |
| `=> leaves` | Walk down call/use graph to find terminal nodes |
| `=> tests` | Find tests that cover this code |
| `=> similar` | Find semantically similar code via embeddings |
| `=> docs` | Find documentation that describes this code |

**What "great" looks like:**

| Modifier | Great Outcome |
|----------|---------------|
| **edge traversal** | Agent sees what calls/uses this and what it calls/uses; chain is clear, not a flat list |
| **roots** | Agent identifies entry points and discovers "only used by tests" code; dead code detection is reliable |
| **leaves** | Agent sees what this code ultimately depends on; understands the full dependency depth |
| **tests** | Agent finds test coverage for any code; knows what to run after changes |
| **similar** | Agent discovers patterns to follow; finds code that solves similar problems |
| **docs** | Agent connects code to its documentation; finds explanatory context |

---

### Diagnostics Modifiers

Surface problems in selected content.

| Modifier | Purpose |
|----------|---------|
| `=> lint` | Show all diagnostics (warnings and errors) |
| `=> lint: errors` | Show only errors |
| `=> lint: warnings` | Show only warnings |

**What "great" looks like:**

| Modifier | Great Outcome |
|----------|---------------|
| **lint** | Agent sees all problems in scope with precise locations; can prioritize fixes; understands severity distribution |

---

### History Modifiers

Understand how content evolved.

| Modifier | Purpose |
|----------|---------|
| `=> history` | Git history for selected files |
| `=> history: <keywords>` | Git history sorted by relevance to keywords |
| `=> changes` | Working copy changes grouped by changelist |
| `=> blame` | Per-line attribution (who, when, why) |

**What "great" looks like:**

| Modifier | Great Outcome |
|----------|---------------|
| **history** | Agent sees what changed and why; commit messages provide context; most relevant changes surface first |
| **history: keywords** | Agent finds when specific behavior was introduced/modified; doesn't wade through unrelated commits |
| **changes** | Agent sees pending work organized by changelist (staged, unstaged); understands what's about to be committed |
| **blame** | Agent traces any line to its origin; understands the reasoning behind current code |

---

## Interaction with Token Budget

The token budget indicates how many tokens the agent is willing to spend. Read should maximize value within that budget—choosing representations, result counts, and detail levels that deliver the most insight for the tokens spent.

**When results exceed the budget:**

- Representation modifiers request confirmation before truncating
- Search/graph modifiers show what fits with a footer indicating what's omitted
- Agent can increase budget and retry for more detail
- Agent can repeat the exact request to bypass the budget and get full results

---

## Composition with Other Tools

Read is the "retrieve and transform" tool. It composes with:

| Tool | Composition Pattern |
|------|---------------------|
| **explore** | Explore finds relevant files → read examines them |
| **query** | Query computes aggregates → read shows specific items |
| **Edit** | Read shows current state → Edit modifies → read verifies |

```
explore (Locate)  →  "authentication is in src/Auth/"
read (structure)  →  "TokenService has ValidateToken, RefreshToken, RevokeToken"
read (content)    →  "here's the ValidateToken implementation"
Edit              →  make changes
read (changes)    →  "here's what you modified"
```

---

## Anti-Patterns

| Don't | Do Instead |
|-------|------------|
| `read` with huge glob + small budget | Use `=> tree` first to understand scope |
| `read => content` for 50 files | Use `=> headline` to filter, then `=> content` for the few you need |
| `read => grep:` for conceptual search | Use `=> find:` which understands synonyms |
| `read => history` looking for specific change | Use `=> history: <keywords>` to surface relevant commits |
| Multiple reads to find something | Use `explore` to locate, then `read` to examine |

---

## What "Great" Looks Like Overall

An agent using a great read implementation should be able to:

1. **See exactly what they need** — No overfetching, no underfetching; budget controls granularity
2. **Find within scope** — Search modifiers locate content within selected files precisely
3. **Traverse relationships** — Understand what uses this, what this uses, and the full graph
4. **Understand history** — See what changed, when, why, and by whom
5. **Discover related content** — Find tests, docs, and similar code from any starting point
6. **Work efficiently** — One read with the right modifier replaces multiple queries

The pattern → modifier → budget model should feel natural: "Give me these files, show me this aspect, with this level of detail."

---

*Select precisely. Transform purposefully. Budget consciously.*
