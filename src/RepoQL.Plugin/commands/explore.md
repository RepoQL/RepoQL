---
description: Full discovery workflow - understand codebase structure without reading files
allowed-tools: mcp__repoql__explore, mcp__repoql__read
---

# RepoQL Explore

Discover what exists and where things are. Uses the indexed repository graph for fast, token-efficient exploration.

## Your Task

$ARGUMENTS

## Workflow

1. **Inventory first** - see what exists before diving deep
2. **Locate** - find where specific concepts live
3. **Inspect** - get detailed context on targets
4. **Explain** - LLM synthesis with citations when understanding is the goal

## Intent Selection

| Intent | Use When | Example |
|--------|----------|---------|
| Inventory | "What's here?" | Survey src/, see file types, get headlines |
| Locate | Know concept, not location | "Where is authentication handled?" |
| Inspect | Know target, need depth | "Show me everything in AuthService" |
| Explain | Need understanding | "How does token refresh work?" |

## Quick Patterns

```
# Orient in new codebase
explore(intent=Inventory, uriGlob="file:///src/**", tokenBudget=1000)

# Find where something is
explore(intent=Locate, keywords="caching layer", tokenBudget=1500)

# Understand specific code
explore(intent=Inspect, uriGlob="file:///src/Cache/**", keywords="invalidation", tokenBudget=2500)

# Get explanation with evidence
explore(intent=Explain, keywords="How does the caching layer handle invalidation?", tokenBudget=2500)
```

## Parameters

- **tokenBudget** (required): Tokens to invest. Start low (500-1000), increase as needed.
- **intent**: Inventory | Locate | Inspect | Explain
- **keywords**: Search terms. Full sentences work best for Explain.
- **uriGlob**: Filter paths. `file:///src/**;!**/tests/**`
- **boost**: Regex to elevate. `(?i)interface|abstract`
- **penalize**: Regex to demote. `(?i)test|mock`

## After Explore

Use `read()` to fetch content for URIs you discover:
```
read("file:///src/Auth.cs#symbol=ValidateToken", 2000)
```
