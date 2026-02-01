# Globbing: What Great Looks Like

> Select anything with one pattern—files, symbols, line ranges, or any combination.

An agent points at a codebase and says "show me all the repository interfaces, except the ones in generated code, and tell me if they're ready to search." One pattern. One call. The agent doesn't need to know where files live, what symbols are called, or how the index works internally. It expresses intent, and the system finds exactly what matches—no false positives, no manual filtering, no learning different syntax for different contexts. The pattern language is the agent's native tongue for selecting anything in the graph.

---

## Pattern Syntax

- An agent should be able to select files using standard glob syntax (`*`, `**`, `?`, `[abc]`)
- An agent should be able to combine multiple patterns into a single selection
- An agent should be able to exclude unwanted matches without separate filtering
- An agent should be able to write exclusions in any order and get the same result

```
src/**/*.cs;tests/**/*.cs;!**/Generated/**
→ All C# in src/ and tests/, excluding Generated/
```

---

## Symbol Selection

- An agent should be able to select symbols by name pattern without knowing file locations
- An agent should be able to use wildcards at any position in symbol names
- An agent should be able to select all children or descendants of a symbol
- An agent should be able to trust that results reflect actual indexed symbols

```
**/*.cs#symbol=I*Repository
→ All IXxxRepository interfaces, wherever they live
```

---

## Cross-Fragment Constraints

- An agent should be able to filter symbols by their source location without knowing symbol names
- An agent should be able to exclude symbols in specific line ranges
- An agent should be able to skip file headers, license blocks, or generated regions with one pattern

```
src/Auth.cs#symbol=*;!#line=1,30
→ All symbols in Auth.cs except those in the file header
```

---

## Mixed Granularity

- An agent should be able to get both containers and contents from a single pattern
- An agent should be able to navigate from file to symbol or symbol to file seamlessly
- An agent should be able to choose the right granularity without requerying

```
src/**/*.cs#symbol=*Repository
→ Returns:
  file:///src/Data/UserRepository.cs           (container)
  file:///src/Data/UserRepository.cs#symbol=UserRepository
  file:///src/Data/UserRepository.cs#symbol=IUserRepository
```

---

## Unified Syntax

- An agent should be able to use the same pattern in search, read, query, and scope checks
- An agent should be able to learn one syntax and apply it everywhere
- An agent should be able to move patterns between contexts without modification

---

## Scope Readiness

- An agent should be able to verify a scope is ready before semantic search
- An agent should be able to see exactly how many files are pending or failed
- An agent should be able to decide to wait, narrow scope, or proceed with known gaps
- An agent should be able to distinguish "nothing matches" from "not ready yet"

```sql
SELECT * FROM scope_readiness('src/**/*.cs');
→ is_ready: true, total: 142, embedded: 142
```

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| An agent should be able to select files and symbols with one pattern | No manual filtering or multiple queries |
| An agent should be able to trust results reflect indexed reality | No guessing, no false positives |
| An agent should be able to exclude by location without knowing names | Filter headers, tests, generated code |
| An agent should be able to use the same syntax everywhere | Learn once, apply everywhere |
| An agent should be able to know readiness before searching | Confidence in results |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Search without checking scope | An agent should be able to verify readiness first |
| Multiple queries to filter | An agent should be able to express it in one pattern |
| Guess what symbols exist | An agent should be able to match against what's indexed |
| Learn different syntax per context | An agent should be able to use one syntax everywhere |

---

*An agent should be able to express any selection in one pattern and trust it matches reality.*

---

## Why This Works as Exemplar

**Paints the dream first**: The opening paragraph shows what the experience feels like—an agent expressing intent with one pattern, getting exactly what matches. No jargon, no internals.

**Then breaks into evaluable declarations**: Every statement passes the "I have a dream:" test:

- "I have a dream: An agent should be able to select symbols by name pattern without knowing file locations" ✓
- "I have a dream: An agent should be able to verify a scope is ready before semantic search" ✓
- "I have a dream: An agent should be able to use the same pattern in search, read, query, and scope checks" ✓

Statements that describe system behavior ("pattern matching works against...") or constraints ("no context-specific escaping") have been reframed as agent capabilities.
