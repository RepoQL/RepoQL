# Plan: similar Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — SimilarHandler

## Scope

**Covers:**
- `SimilarHandler` implementing `IModifierHandler`
- Find code semantically similar to matched content
- Use embeddings for similarity
- Return similar code with snippets

**Does not cover:**
- Embedding generation (existing infrastructure)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can discover patterns to follow
- Find duplicates and related implementations

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `related()` UDF
- Embeddings infrastructure

## North Star

Find code that's like this code. Show similar patterns even when names differ.

## Done Criteria

### Handler Registration
- The SimilarHandler shall register with modifier name `similar`
- The SimilarHandler shall handle `CanHandle("similar")` returning true

### Execution
- The handler shall use `related()` UDF on matched content
- The handler shall exclude the source itself from results
- The handler shall exclude trivial similarities (same file, boilerplate)
- The handler shall return similar code with snippets and similarity score

### Output Format
```
file:///src/Auth/TokenService.cs#symbol=RefreshAsync => similar

87% file:///src/Auth/SessionService.cs#symbol=RenewAsync
Similar: async refresh pattern with validation and regeneration

 42:     public async Task<Session> RenewAsync(string sessionId)
 43:     {
 44:         var existing = await _store.GetAsync(sessionId);
...

[2 similar shown, 3 below threshold]
```

### Budget Handling
- Show similar results that fit within budget
- Footer shows: `[N similar shown, M below threshold]`

## Constraints

- **Use existing UDF**: `related()` for similarity search
- **Relevance threshold**: Filter out low-similarity noise

## References

- [Flow: similar.md](../../flows/future/read/similar.md)
- `related()` UDF documentation

## Error Policy

- No embeddings: Return "Embeddings not ready, try again shortly"
- No similar found: Return "No similar code found (source may be unique)"
- Content too small: Return "Insufficient content for similarity comparison"
