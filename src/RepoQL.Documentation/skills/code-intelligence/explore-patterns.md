---
description: "Patterns for using explore effectively: intent selection, scope filtering, keyword strategies, and multi-step discovery workflows."
tags: ["skill", "code-intelligence", "explore", "patterns", "intent", "discovery"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
---

# Explore Patterns

Patterns for common discovery scenarios. For the full explore parameter reference, see `help:///repoql/tools/explore/using-xray.md`.

---

## Intent Selection Patterns

### "I don't know what's in this codebase"

```
explore(intent="Inventory", tokenBudget=2000)
```

Returns headlines for all files, sorted by relevance. Gives you the shape of the repo.

### "I know the concept, not the location"

```
explore(intent="Locate", keywords="dependency injection", tokenBudget=1500)
```

Balanced: enough context on matches to decide what to read, plus awareness of the rest.

### "I know the file, I need details"

```
explore(intent="Inspect", uriGlob="file:///src/Auth/**", tokenBudget=3000)
```

Concentrates budget on a narrow scope. Returns structure and snippets.

### "Explain this to me"

```
explore(intent="Explain", keywords="how does the pipeline work", uriGlob="file:///src/Indexing/**", tokenBudget=2500)
```

LLM reads up to 50k tokens of content, returns focused prose synthesis. Requires OPENROUTER_API_KEY.

---

## Scope Filtering

### Single directory
```
explore(intent="Locate", uriGlob="file:///src/Services/**", keywords="caching", tokenBudget=1500)
```

### Multiple schemes
```
explore(intent="Locate", uriGlob="file:///**;help:///**", keywords="authentication", tokenBudget=2000)
```

### Exclude paths
```
explore(intent="Locate", uriGlob="file:///**;!file:///tests/**;!file:///node_modules/**", keywords="config", tokenBudget=1500)
```

### Help docs only
```
explore(intent="Locate", uriGlob="help://**", keywords="query views", tokenBudget=1500)
```

---

## Keyword Strategies

### Phrases → semantic search
```
explore(intent="Locate", keywords="how errors are handled in the pipeline", tokenBudget=1500)
```

### Symbol names → lexical search
```
explore(intent="Locate", keywords="IndexItem DuckDbDataStore", tokenBudget=1500)
```

### Combine both
```
explore(intent="Locate", keywords="retry logic IndexingEngine", tokenBudget=1500)
```

---

## Multi-Step Discovery

### Survey → narrow → deep

```
# Step 1: What exists?
explore(intent="Inventory", uriGlob="file:///src/**", tokenBudget=1500)

# Step 2: Find the specific area
explore(intent="Locate", keywords="error handling", tokenBudget=1500)

# Step 3: Get details on what you found
read("file:///src/Pipeline/ErrorHandler.cs#symbol=HandleError", 3000)
```

### Cross-scheme discovery

```
# Find implementations
explore(intent="Locate", uriGlob="file:///**", keywords="format loader", tokenBudget=1500)

# Find documentation
explore(intent="Locate", uriGlob="help://**", keywords="format loader", tokenBudget=1500)
```

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Reading files without exploring first | Explore to find targets, then read |
| Using Inspect intent for broad search | Use Inventory or Locate first |
| Budget too small for scope | 500 for one file, 1500 for a directory, 3000+ for the whole repo |
| No keywords with Locate | Keywords drive relevance ranking — without them you get alphabetical |
| Searching code for docs | Use `uriGlob="help://**"` to search embedded documentation |

---

*Explore wide, then focus deep.*
