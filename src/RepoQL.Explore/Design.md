# Explore Design

## Two Inputs

The explore engine is a pure function of two inputs:

1. **Budget** — how many tokens to spend (default 2000)
2. **Breadth** — how to distribute them (1-10, default 5)

Everything else derives from these two numbers.

---

## What Breadth Controls

**One thing:** the allocation curve steepness. Everything else is emergent.

| Breadth | Style | Per-result depth | Result count | Search aggression |
|---------|-------|------------------|--------------|-------------------|
| 1-2 | Deep inspection | High — structure + addressable children with signatures | Few (10-15 docs) | Strict thresholds, semantic search |
| 3-4 | Focused locate | Moderate — structure + top children | Moderate (15-20 docs) | Balanced |
| 5-6 | Balanced (default) | Standard — headline + selective structure | Medium (20-25 docs) | Balanced |
| 7-8 | Wide survey | Compact — URI + headline | Many (25-35 docs) | Broad net |
| 9-10 | Full inventory | Minimal — headline only (no URI at 10) | Maximum (35-50 docs) | Broadest, documents only |

### The Formula

```
modifier = 0.6 + (breadth - 1) * 0.08
```

| Breadth | Modifier | Effect |
|---------|----------|--------|
| 1 | 0.60 | Steep curve — concentrates tokens on top results |
| 5 | 0.92 | Balanced distribution |
| 10 | 1.32 | Flat curve — spreads tokens evenly |

The modifier scales expected value in the allocation algorithm. Lower modifier = steeper curve = more concentration on top results.

---

## Allocation Algorithm

Two-level hierarchical budget allocation.

### Level 1: Files Compete for Budget

Each file's expected value:
```
fileEV = max(fileConfidence, bestChildConfidence) × modifier
```

Budget allocated proportionally:
```
fileBudget = totalBudget × (fileEV / sumOfAllEV)
```

If minimum costs exceed budget, drop lowest-EV files until they fit, then reallocate.

### Level 2: Items Compete Within Each File

Within each file's budget, the file itself and its children compete:

1. **Proportional allocation** — each item gets budget proportional to its EV
2. **Pick best fit** — richest representation that fits the allocation
3. **Drop pass** — if still over budget, drop lowest-EV children (never the file itself)
4. **Upgrade pass** — spend remaining budget upgrading highest-EV items

### Children Limits

Breadth controls max children per file:

| Breadth | Max Children |
|---------|-------------|
| 1-2 | 8 |
| 3-4 | 6 |
| 5-6 | 5 |
| 7-8 | 3 |
| 9-10 | 2 |

Children fill available per-result budget progressively — not all-or-nothing. When children are omitted, `OmittedChildrenCount` shows how many were cut.

---

## Per-Result Depth Ceiling

Explore never shows raw code that can't be followed up on. Everything in the output must be a URI or contain the ingredients to construct one.

Maximum useful depth per result: ~500-600 tokens (full structure + all children as addressable `#symbol=Name` URIs with signatures). Beyond that is `read`'s job.

At low breadth with high budget, surplus tokens go to **more results at moderate depth** — not deeper content on the top result.

---

## Representation Levels

Four levels, driven by per-result budget — not by a global mode.

| Level | Content | Tokens |
|-------|---------|--------|
| **Minimal** | headline only (no URI) | ~5-20 |
| **Compact** | URI + headline | ~20-80 |
| **Standard** | URI + headline + structure | ~70-280 |
| **Rich** | URI + snippet (code) | ~60-530 |

**Minimal** is only used at high breadth (>=8). At lower breadth, URIs are too valuable to omit — Compact is the floor.

**Rich** omits headline because the snippet IS the content. No redundancy.

### Content Fallback

```
snippet → structure → headline → filename
```

Filename always exists, so rendering never fails.

---

## Search Strategy

Breadth drives search aggressiveness via `QueryStrategy`:

| Breadth | Document Limit | Objects Per Doc | Behavior |
|---------|---------------|-----------------|----------|
| 1-2 | 15 | 8 | Deep — few docs, many objects |
| 3-4 | 20 | 6 | Moderate |
| 5-6 | 25 | 5 | Balanced |
| 7-8 | 35 | 3 | Wide — many docs, few objects |
| 9-10 | 50 | 0 | Inventory — documents only, no objects |

High breadth (>=8) without keywords: documents only (inventory scan). Objects are only fetched when there's a question to match against.

### Snippet Limits

Dynamic, derived from breadth and token budget:

```
perFileBudget = tokenBudget / boundedResultCount
snippetsFromBudget = perFileBudget / averageSnippetCost
dynamicLimit = max(minSnippetsPerFile, snippetsFromBudget)
limit = min(maxForBreadth, dynamicLimit)
```

---

## Output Format

### Confidence Display

```
 98% [method] file:///src/Auth/JwtService.cs#line=42,58
```

Right-aligned to 4 chars. Omitted when no search criteria (all scores equal).

### Blank Lines

- Multi-line items (structure, snippet): blank line before and after
- Single-line items (headline): packed tight

### Truncation Summary

```
[N more, X-Y%]        # with search criteria
[N more]               # without
```

### Status Footer

```
[1.8k tok | 1.1s | index: ready | semantic: ready]
```

Always present. Shows tokens used, latency, index/semantic readiness.

---

## Examples

### Breadth 2, Budget 2000 (Deep Inspection)

```
 98% [method] file:///src/Auth/JwtService.cs#line=42,58
```csharp
public ClaimsPrincipal ValidateToken(string token)
{
    var handler = new JwtSecurityTokenHandler();
    var principal = handler.ValidateToken(token, _validationParams, out _);
    return principal;
}
```

 92% file:///src/Auth/AuthService.cs
AuthService - Authentication orchestration
  +ValidateCredentials(string, string) → Task<AuthResult>
  +RevokeToken(string) → Task<bool>
   85% [method] #symbol=ValidateCredentials
    Validates user credentials against the store
   78% [method] #symbol=RevokeToken
    Revokes a JWT refresh token

[3 more, 40-55%]
[1.9k tok | 0.8s | index: ready | semantic: ready]
```

### Breadth 5, Budget 2000 (Balanced)

```
 95% file:///src/Auth/AuthService.cs
AuthService - Authentication orchestration
   92% [method] #symbol=ValidateCredentials
   85% [method] #symbol=RevokeToken

 88% file:///src/Auth/JwtService.cs
JwtService - JWT token generation and validation

 82% file:///src/Auth/TokenCache.cs
TokenCache - Distributed token caching

 75% file:///src/Middleware/AuthMiddleware.cs
AuthMiddleware - Authentication pipeline middleware

[8 more, 40-65%]
[1.8k tok | 0.6s | index: ready | semantic: ready]
```

### Breadth 9, Budget 1000 (Wide Inventory)

```
file:///src/Auth/AuthService.cs
AuthService - Authentication orchestration
file:///src/Auth/JwtService.cs
JwtService - JWT token generation and validation
file:///src/Auth/TokenCache.cs
TokenCache - Distributed token caching
file:///src/Auth/AuthOptions.cs
AuthOptions - Authentication configuration
file:///src/Middleware/AuthMiddleware.cs
AuthMiddleware - Authentication pipeline middleware
file:///src/Auth/ClaimsTransformer.cs
ClaimsTransformer - Custom claims transformation
[12 more]
[0.9k tok | 0.4s | index: ready | semantic: ready]
```

---

## Architecture

```
explore(breadth, keywords, scope, budget)
    │
    ▼
ExploreOrchestrator
    ├── SearchEngine (hybrid: BM25 + fuzzy + semantic)
    │   ├── QueryStrategy (breadth → fetch limits)
    │   └── FileGrouper (breadth → snippet limits)
    ├── ValueBasedAllocator (breadth → curve → per-result budget)
    └── OutputComposer
        └── RepresentationFormatter (budget → representation level)
```

### Key Files

| Component | File |
|-----------|------|
| Orchestrator | `ExploreOrchestrator.cs` |
| Allocator | `ValueBasedAllocator.cs` |
| Composer | `OutputComposer.cs` |
| Formatter | `RepresentationFormatter.cs` |
| Estimator | `ExploreTokenEstimator.cs` |
| Search | `Search/IExploreSearchEngine.cs` |
| Query Strategy | `Search/QueryStrategy.cs` |
| File Grouper | `Search/FileGrouper.cs` |
| Types | `Search/SearchTypes.cs` |

---

## Design Properties

- **Pure function**: No side effects, no state, deterministic
- **Budget guarantee**: Never exceeds declared token budget
- **Graceful degradation**: Always produces useful output
- **Composable**: breadth × budget produces the full behavior space
- **No global modes**: Representation driven by per-result budget, not a flag
