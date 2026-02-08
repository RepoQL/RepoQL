---
description: Quick semantic search - find where concepts are implemented
allowed-tools: mcp__repoql__explore
---

# RepoQL Find

Fast semantic search to locate where concepts are implemented.

## Your Search

$ARGUMENTS

## Approach

Use `explore` with Locate intent for balanced discovery:

```
explore(
  intent=Locate,
  keywords="$ARGUMENTS",
  tokenBudget=1500
)
```

## Refining Results

**Narrow by path:**
```
explore(intent=Locate, keywords="...", uriGlob="file:///src/Services/**", tokenBudget=1500)
```

**Boost patterns:**
```
explore(intent=Locate, keywords="...", boost="(?i)interface|abstract", tokenBudget=1500)
```

**Exclude tests:**
```
explore(intent=Locate, keywords="...", penalize="(?i)test|mock|spec", tokenBudget=1500)
```

## Next Steps

Once you have URIs, read the content:
```
read("file:///path#symbol=Name", 2000)
```
