# Explore Patterns

Detailed patterns for using explore effectively.

## Intent Selection

### Inventory - "What exists here?"

Survey mode. See into files through headlines and tags without reading them. Breadth over depth.

```
# Orient in new codebase
explore(intent=Inventory, uriGlob="file:///src/**", tokenBudget=1000)

# Ranked survey (relevant stuff first, still see everything)
explore(intent=Inventory, uriGlob="file:///docs/**", keywords="failure", boost="(?i)error", tokenBudget=1000)

# See what types exist
explore(intent=Inventory, uriGlob="file:///src/**/*.cs", boost="(?i)class|interface", tokenBudget=1000)
```

### Locate - "You know the concept, not the location"

Balanced mode. Detail on matches, awareness of the rest. Enough context to decide what to read.

```
# Find authentication code
explore(intent=Locate, keywords="authentication validation", tokenBudget=1500)

# Find error handling patterns
explore(intent=Locate, keywords="exception handling retry", tokenBudget=1500)

# Find interfaces in a specific area
explore(intent=Locate, uriGlob="file:///src/Services/**", keywords="service", boost="(?i)interface|abstract", tokenBudget=1500)
```

### Inspect - "You know the target"

Depth mode. Concentrates tokens on relevant content and surroundings. Shows code snippets, line numbers.

```
# Deep dive into auth service
explore(intent=Inspect, uriGlob="file:///src/Auth/**", keywords="token validation", tokenBudget=2500)

# Examine a specific component
explore(intent=Inspect, uriGlob="file:///src/Services/PaymentService.cs", tokenBudget=2500)
```

### Explain - "You want understanding"

Synthesis mode. An LLM reads far more than you'd spend (often 50k -> 1k). Returns synthesis with citations.

```
# Understand a mechanism
explore(intent=Explain, keywords="How does JWT refresh work in TokenService?", tokenBudget=2500)

# Understand architecture
explore(intent=Explain, keywords="What is the dependency injection pattern used here?", tokenBudget=3000)
```

## Parameter Layering

Combine parameters for precision:

1. **uriGlob** filters WHERE (path matching)
2. **keywords** finds WHAT (semantic search)
3. **boost** ranks UP (elevate matches)
4. **penalize** ranks DOWN (demote matches)

### Examples

```
# Authentication implementations, not tests
explore(
  intent=Locate,
  uriGlob="file:///src/**",
  keywords="authentication",
  penalize="(?i)test|mock",
  tokenBudget=1500
)

# Find contracts/interfaces in services
explore(
  intent=Locate,
  uriGlob="file:///src/Services/**",
  keywords="service",
  boost="(?i)interface|abstract",
  tokenBudget=1500
)

# Find error handling, prioritize production code
explore(
  intent=Locate,
  keywords="error handling exception",
  boost="(?i)handler|middleware",
  penalize="(?i)test|spec|mock|fake",
  tokenBudget=1500
)
```

## URI Glob Patterns

```
file:///src/**           # Recursive - all files under src
file:///src/*.cs         # Non-recursive - .cs files in src only
file:///src/**/*.cs      # Recursive - all .cs files
file:///src/**;!**/tests/**  # Exclude tests
help://**                # Embedded documentation
```

## Explain Query Tips

Queries must be self-contained (no conversation context):

Good:
- "What is AuthService responsible for?"
- "Why does PaymentProcessor use idempotency keys?"
- "How does the caching layer handle invalidation?"

Bad:
- "Explain everything about authentication" (too broad)
- "What does this service do?" (no referent)
- "Why is it doing that?" (requires context)

Output includes:
- **Answer**: Synthesized explanation
- **Evidence**: Code snippets with `file:///path#line=N,M` citations
- **Nuance**: Caveats and related considerations

Always verify citations by reading the actual lines.
