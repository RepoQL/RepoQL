# Explore Tool

> **Scope**: Token-budgeted codebase exploration. Orchestrates search and renders results within a token budget.

---

## Capsule: ExploreCore

**Invariant**
Given keywords + budget + breadth, explore searches, allocates tokens across results using a breadth-controlled curve, and renders at appropriate detail levels.

**Example**
```
-- Wide inventory (breadth 9)
explore(uriGlob="src/**", tokenBudget=1500, breadth=9)

-- Balanced search (default breadth 5)
explore(keywords="authentication", tokenBudget=2000)

-- Deep inspection (breadth 2)
explore(keywords="authentication", tokenBudget=2000, breadth=2)
```

**Depth**
- Breadth 1-2: Deep inspection — few results, full structure + addressable children
- Breadth 5: Balanced — moderate results with selective structure
- Breadth 9-10: Wide inventory — many results, URI + headline

---

## Architecture

```
explore(breadth, keywords, scope, budget)
    │
    ▼
ExploreOrchestrator
    ├── SearchEngine (hybrid: BM25 + fuzzy + semantic)
    │   ├── QueryStrategy (breadth → document/object fetch limits)
    │   └── FileGrouper (breadth → snippet limits per file)
    ├── ValueBasedAllocator (breadth → allocation curve → per-result budget)
    └── OutputComposer
        └── RepresentationFormatter (per-result budget → representation level)
```

---

## Breadth Parameter

### Capsule: BreadthBehavior

**Invariant**
Breadth (1-10, default 5) controls the allocation curve steepness. Everything else derives: result count, per-result depth, search aggressiveness, children shown.

**Example**
| Breadth | Modifier | Max Children | Doc Limit | Objects/Doc |
|---------|----------|-------------|-----------|-------------|
| 1 | 0.60 | 8 | 15 | 8 |
| 5 | 0.92 | 5 | 25 | 5 |
| 10 | 1.32 | 2 | 50 | 0 |

**Depth**
- `modifier = 0.6 + (breadth - 1) * 0.08`
- Lower modifier = steeper curve = more concentration on top results
- High breadth (>=8) without keywords: documents only (inventory scan)
- Minimal representation (no URI) only allowed at breadth >= 8

---

## Token Allocation

### Capsule: TwoLevelAllocation

**Invariant**
Level 1: Files compete for budget based on expected value. Level 2: Items within each file compete for representation level.

**Example**
```
Budget: 2000 tokens, Breadth: 5
├─ File A (EV=0.9): 900 tokens
│   ├─ Document: Standard (150 tok)
│   ├─ Method1: Rich (300 tok)
│   └─ Method2: Compact (50 tok)
├─ File B (EV=0.6): 600 tokens
│   ├─ Document: Standard (150 tok)
│   └─ Class1: Standard (100 tok)
└─ File C (EV=0.3): 500 tokens
    └─ Document: Compact (50 tok)
```

**Depth**
- File EV = max(fileConfidence, bestChildConfidence) x breadthModifier
- Proportional allocation: `fileBudget = totalBudget x (fileEV / sumEV)`
- Drop lowest-EV files if minimum costs exceed budget
- Upgrade pass uses remaining budget to improve representations
- Children fill available budget progressively (not all-or-nothing)

**Location**: `src/RepoQL.Explore/ValueBasedAllocator.cs`

### Allocation Flow

1. Calculate file-level expected values
2. Allocate budget proportionally to files
3. Drop lowest-EV files if over budget
4. For each file: allocate among file + children
5. Pick richest representation that fits allocation
6. Upgrade pass: improve stragglers with remaining budget

---

## Representation Levels

### Capsule: RepresentationLevels

**Invariant**
Four levels with increasing token cost: Minimal (headline) -> Compact (+URI) -> Standard (+structure) -> Rich (+snippet).

**Example**
```
Minimal (~10 tok):
  AuthService.cs | class | 450 LOC

Compact (~40 tok):
  93% file:///src/Auth/AuthService.cs
    AuthService.cs | class | 450 LOC

Standard (~150 tok):
  93% file:///src/Auth/AuthService.cs
    AuthService.cs | class | 450 LOC
    namespace RepoQL.Auth
      public class AuthService : IAuthService
        +ValidateCredentials(string, string) -> Task<AuthResult>

Rich (~300 tok):
  93% [csharp.method] file:///src/Auth.cs#line=42,60&symbol=ValidateCredentials
  ```csharp
  public async Task<AuthResult> ValidateCredentials(string user, string pass)
  {
      // implementation
  }
  ```
```

**Depth**
- Token estimates via `ExploreTokenEstimator` (heuristic, not actual tokenization)
- Rich requires snippet content; falls back to Standard if missing
- Minimal only allowed at breadth >= 8 (URI is high-value at lower breadth)

**Location**: `src/RepoQL.Explore/RepresentationFormatter.cs`

---

## Output Composition

### Capsule: OutputComposition

**Invariant**
Render decisions with proper spacing, add truncation summary for omitted items, append status footer.

**Example**
```
 95% file:///src/Auth/AuthService.cs
  AuthService.cs | class AuthService | 1250 tokens
   78% [csharp.method] file:///src/Auth/AuthService.cs#line=42,80
    ValidateCredentials(string, string) -> Task<AuthResult>

 82% file:///src/Auth/TokenService.cs
  TokenService.cs | class TokenService | 890 tokens

[More: 5 docs, 12 symbols (8x csharp.method, 4x csharp.class)]

[1.8k tok | 1.1s | index: ready | semantic: ready]
```

**Depth**
- Blank lines between multi-line items for readability
- Truncation summary groups omitted items by kind
- Status footer shows tokens used, latency, index status
- Indentation reflects parent-child hierarchy

**Location**: `src/RepoQL.Explore/OutputComposer.cs`

---

## Data Types

### ExploreQuery

```csharp
public sealed record ExploreQuery(
    int TokenBudget,
    int Breadth = 5,
    string? Scope = null,
    string? Keywords = null,
    string? Boost = null,
    string? Penalize = null,
    int? Limit = null);
```

### ExploreResult

```csharp
public record ExploreResult(
    string Uri,
    int Confidence,
    string? Kind,
    string? Headline,
    string? Structure,
    string? Snippet,
    string? Lang,
    string? SemanticType,
    IReadOnlyList<ExploreResult>? ChildObjects,
    string? Provenance);
```

### RenderingDecision

```csharp
public record RenderingDecision(
    ExploreResult Result,
    Representation Level,
    int EstimatedTokens,
    IReadOnlyList<RenderingDecision>? ChildDecisions,
    int OmittedChildrenCount);
```

---

## Key Locations

| Component | File |
|-----------|------|
| Orchestrator | `src/RepoQL.Explore/ExploreOrchestrator.cs` |
| Allocator | `src/RepoQL.Explore/ValueBasedAllocator.cs` |
| Composer | `src/RepoQL.Explore/OutputComposer.cs` |
| Formatter | `src/RepoQL.Explore/RepresentationFormatter.cs` |
| Estimator | `src/RepoQL.Explore/ExploreTokenEstimator.cs` |
| Search Engine | `src/RepoQL.Explore/Search/IExploreSearchEngine.cs` |
| Query Strategy | `src/RepoQL.Explore/Search/QueryStrategy.cs` |
| File Grouper | `src/RepoQL.Explore/Search/FileGrouper.cs` |
| Types | `src/RepoQL.Explore/Search/SearchTypes.cs` |

---

## See Also

- `docs/current-state/search.md` — Search infrastructure used by explore
- `docs/current-state/indexing.md` — How files become searchable
- `src/RepoQL.Documentation/repoql/tools/explore/using-xray.md` — User-facing documentation
