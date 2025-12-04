# RepoQL.Rendering Design Document

## Ethos

This is not a formatter. It's a **translation layer between structured data and agent cognition**.

Every character is currency. Every decision trades coverage against depth. The engine doesn't make semantic decisions—it handles the mechanics of fitting information into a budget while respecting the agent's declared intent.

> The gap between intent and understanding should disappear.

No surprises. No wasted tokens. No missing insights.

---

## Core Concepts

### The Three Inputs

The rendering engine is a pure function with three key inputs:

1. **Intent** - What is the agent trying to do?
2. **Results** - What did the search return? (with confidence scores)
3. **Budget** - How many tokens can we spend?

### The Three Intents

| Intent | Goal | Preference |
|--------|------|------------|
| **Explore** | Map territory | Breadth over depth |
| **Find** | Locate specific things | Adapts to distribution |
| **Read** | See code | Depth over breadth |

**Explore** always wants headlines. Show as much as possible so the agent knows what exists.

**Find** is the only intent that changes strategy based on distribution shape. Standouts get rich treatment; no standouts means show inventory for refinement.

**Read** always wants snippets. Fewer items is fine if they have code.

### The Four Representations

| Level | Content | Tokens |
|-------|---------|--------|
| **Minimal** | headline only (no URI) | ~5-20 |
| **Compact** | uri + headline | ~20-80 |
| **Standard** | uri + headline + structure | ~70-280 |
| **Rich** | uri + snippet | ~60-530 |

**Minimal** is used for wide Explore results (>100) without search criteria. Saves tokens by omitting URIs.

**Rich** omits headline because the snippet IS the content. No redundancy.

**Headline is always single-line**. Multi-line headlines are truncated at the first newline.

---

## Distribution-Aware Rendering

The shape of the confidence distribution determines strategy.

### Step 1: Classify Results into Tiers

| Tier | Criteria |
|------|----------|
| **Top** | Confidence >= 80% OR >= 75th percentile |
| **Middle** | Between top and bottom |
| **Bottom** | Confidence < 50% AND < 25th percentile |

Use both absolute AND percentile thresholds. A 90% match is always strong, even if everything scored 85%+.

### Step 2: Detect Distribution Shape

**Lumpy** (standouts exist):
- Top tier is small (< 20% of results)
- Clear gap between top tier and rest

**Even** (no standouts):
- Scores clustered within ~20% range
- No clear separation between tiers

### Step 3: Calculate Optimal Limit (if not provided)

When limit is omitted, calculate it based on distribution and intent:

**Lumpy distribution**:
```
optimal_limit = top_tier_count + min(middle_tier_count, 5)
```
Focus on standouts. Include a few middle-tier for context.

**Even distribution**:
```
optimal_limit = min(total_results, budget / avg_compact_cost)
```
Maximize coverage since no standouts exist.

**Intent adjustment**:
- Explore: Bias toward higher limit (breadth)
- Find: Use calculated limit
- Read: Bias toward lower limit (depth)

### Step 4: Calculate Pressure

```
pressure = estimated_preferred_cost / budget
```

| Pressure | Meaning |
|----------|---------|
| < 0.7 | Low - room for preferred representations |
| >= 0.7 | High - must make tradeoffs |

### Step 5: Select Strategy

**Explore Intent**

| Distribution | Low Pressure | High Pressure |
|--------------|--------------|---------------|
| Lumpy | Top: Standard, Middle: Compact, Bottom: Minimal | Top/Middle: Compact, Bottom: Minimal |
| Even | Top/Middle: Compact, Bottom: Minimal | Top/Middle: Compact, Bottom: Minimal |

*Always breadth. Even with standouts, headlines map the territory.*

**Wide Explore Rule**: When Explore + no search criteria + >100 results, use **Minimal** for all items (headline only, no URIs).

**Find Intent**

| Distribution | Low Pressure | High Pressure |
|--------------|--------------|---------------|
| Lumpy | Top: Rich, Middle: Standard, Bottom: Minimal | Top: Rich, Rest: **Omit** |
| Even | Top: Standard, Middle: Compact, Bottom: Minimal | Top/Middle: Compact, Bottom: Minimal |

*Adapts to shape. Standouts → depth. No standouts → breadth for discovery.*

**Read Intent**

| Distribution | Low Pressure | High Pressure |
|--------------|--------------|---------------|
| Lumpy | Top/Middle: Rich, Bottom: Minimal | Top: Rich, Rest: **Omit** |
| Even | Top: Rich, Middle: Standard, Bottom: Minimal | Top: Rich, Middle: Compact, Bottom: Minimal |

*Always depth. Fewer items but always with code.*

### Bottom Tier Rule

**Bottom tier always uses Minimal** (headline only, no URI). Low-confidence results are shown for context only - they don't need URIs wasting tokens.

### What Gets Sacrificed Under Pressure

| Intent | Sacrifice |
|--------|-----------|
| Explore | Nothing (already minimal) - just truncate |
| Find (lumpy) | Quantity - drop weak, keep rich for strong |
| Find (even) | Richness - all headlines for coverage |
| Read | Quantity - fewer items, but always snippets |

---

## Output Format

### Confidence Display

```
 98% [method] file:///src/Auth/JwtService.cs#line=42,58
```

- First on line, right-aligned to 4 characters with `%` suffix
- Examples: ` 98%`, `100%`, `  5%`
- **Omit when no search criteria** (no question, no patterns) - all scores would be equal

### Kind Badges

- Objects: `[class]`, `[method]`, `[interface]`, `[heading]`, etc.
- Documents: No badge (file-ness is implied)

### Blank Lines

- **Multi-line items** (structure, snippet): Blank line before and after
- **Single-line items** (headline): Pack tight, no blank lines

### By Representation

**Minimal** (wide Explore, no search criteria):
```
JwtService - JWT token generation and validation
AuthController - 8 endpoints for user authentication
TokenCache - Distributed token caching
AuthOptions - Authentication configuration
```
Just headlines, no URIs. Maximum density for inventorying.

**Compact**:
```
 85% file:///src/Auth/AuthController.cs
AuthController - 8 endpoints for user authentication
```

**Standard**:
```
 85% file:///src/Auth/AuthController.cs
AuthController - 8 endpoints for user authentication
- Login, Logout, Refresh, Register
- Password reset flow
```

**Rich**:
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
```

### Truncation Summary

When items are omitted:
```
[N more, X-Y%]
```

When no search criteria (no confidence):
```
[N more]
```

---

## Complete Examples

### Find Intent, Lumpy Distribution, High Pressure

5 results at 95%+, 15 at 60-70%, 80 at 30-40%. Budget: 800 tokens.

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

 96% [method] file:///src/Auth/TokenValidator.cs#line=15,28
```csharp
public bool IsTokenExpired(JwtSecurityToken token)
{
    return token.ValidTo < DateTime.UtcNow;
}
```

 95% [method] file:///src/Auth/ClaimsBuilder.cs#line=8,22
```csharp
public IEnumerable<Claim> BuildClaims(User user)
{
    yield return new Claim(ClaimTypes.Name, user.Name);
    yield return new Claim(ClaimTypes.Email, user.Email);
}
```

[97 more, 30-70%]
```

Top 3 get rich snippets. The 97 weaker matches are summarized.

### Find Intent, Even Distribution, High Pressure

25 results all between 55-70%. Budget: 800 tokens.

```
 70% file:///src/Auth/AuthController.cs
AuthController - 8 endpoints for user authentication
 68% file:///src/Auth/JwtService.cs
JwtService - JWT token generation and validation
 65% file:///src/Auth/TokenCache.cs
TokenCache - Distributed token caching
 63% file:///src/Auth/AuthOptions.cs
AuthOptions - JWT configuration options
 61% file:///src/Auth/ClaimsTransformer.cs
ClaimsTransformer - Custom claims transformation
 59% file:///src/Auth/AuthMiddleware.cs
AuthMiddleware - Authentication pipeline middleware
[19 more, 55-58%]
```

No standouts, so show headlines for discovery. Agent can refine search.

### Explore Intent, No Search Criteria

Pure inventory. No confidence shown.

```
file:///src/Auth/JwtService.cs
JwtService - JWT token generation and validation
file:///src/Auth/AuthController.cs
AuthController - Authentication API endpoints
file:///src/Auth/TokenCache.cs
TokenCache - Distributed token caching
file:///src/Auth/AuthOptions.cs
AuthOptions - Authentication configuration
[15 more]
```

---

## Dynamic Snippet Sizing

Snippets adapt to remaining budget:

1. **Focus on relevance**: Center on `best_chunk_start`/`best_chunk_end` from semantic search
2. **Minimum**: 5 lines around the relevant region
3. **Maximum**: Whatever fits in remaining budget
4. **Language**: Extracted from mime type

### Truncation Indicators

When snippet is trimmed:
```
[... 12 lines above]
public void RelevantMethod()
{
    // the matching code
}
[... 8 lines below]
```

### Heuristic

- Chunk < 20 lines: Show full chunk + 2 lines context
- Chunk >= 20 lines: Show center ±10 lines with truncation indicators

---

## Content Fallback

When preferred content is missing, fall back gracefully:

```
snippet → structure → headline → filename
```

Filename always exists, so rendering never fails.

---

## Implementation

### Project Structure

```
src/RepoQL.Rendering/
├── Design.md                    # This document
├── XrayRenderingEngine.cs       # Main rendering logic
├── DistributionAnalyzer.cs      # Tier classification, shape detection
├── BudgetAllocator.cs           # Strategy selection, pressure calculation
├── RepresentationRenderer.cs    # Format each representation level
├── SnippetFocuser.cs            # Extract relevant snippet region
└── TokenEstimator.cs            # Token counting
```

### Key Interface

```csharp
public interface IXrayRenderingEngine
{
    string Render(IReadOnlyList<XrayResult> results, RenderingContext context);
}

public record RenderingContext(
    Intent Intent,
    int TokenBudget,           // Required
    int? Limit,                // Optional - calculated if omitted
    bool HasSearchCriteria     // question or patterns provided
);

public enum Intent { Explore, Find, Read }
```

### Design Properties

- **Pure function**: No side effects, no state, deterministic
- **Budget guarantee**: Never exceeds declared token budget
- **Graceful degradation**: Always produces useful output
- **Testable**: Hardcoded rules with comprehensive unit tests

---

## Testing Strategy

1. **Representation tests**: Verify format of each level
2. **Budget compliance**: Never exceed, edge cases at exactly budget
3. **Distribution detection**: Lumpy vs even classification
4. **Strategy selection**: Intent × Distribution × Pressure matrix
5. **Fallback behavior**: Missing content handled gracefully
6. **Snapshot tests**: Catch unintended format changes

---

## Decisions

| Decision | Choice |
|----------|--------|
| Token estimation | Heuristic (chars/4), upgrade to tiktoken later |
| Rendering rules | Hardcoded, unit tested |
| Snippet language | From mime type |
| Content fallback | snippet → structure → headline → filename |
| Distribution thresholds | Tune with real-world experiments |
