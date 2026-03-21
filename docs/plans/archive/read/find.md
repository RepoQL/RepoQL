# Plan: find Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — FindHandler

## Scope

**Covers:**
- `FindHandler` implementing `IModifierHandler`
- Semantic search within matched files
- Use chunk embeddings for relevance ranking
- Narrow to precise spans within chunks
- Return snippets with URIs

**Does not cover:**
- Embedding generation (existing infrastructure)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can locate concepts even when terminology varies
- Precise span finding within files

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `search()` UDF
- Chunk embeddings indexed
- Embeddings infrastructure

## North Star

Semantic grep—find where concepts appear, not just literal strings. Zoom to exact relevant span.

## Done Criteria

### Handler Registration
- The FindHandler shall register with modifier name `find`
- The FindHandler shall handle `CanHandle("find")` returning true

### Parameter Parsing
- The parameter shall be the search keywords
- When parameter is empty, return error requesting keywords

### Execution
- The handler shall query chunk embeddings for matched file URIs
- The handler shall rank chunks by similarity to keywords
- The handler shall narrow high-scoring chunks to precise spans
- The handler shall generate snippets centered on matches

### Span Narrowing
- For high-scoring chunks, identify the most relevant lines
- Avoid returning entire chunks when only part is relevant
- Include context lines for understanding

### Output Format
```
file:///src/Auth/TokenService.cs#line=42,52  [score: 0.89]

 40:     private readonly ITokenStore _store;
 41:
>42:     public async Task<Token> RefreshAsync(string refreshToken)
>43:     {
>44:         var existing = await _store.GetAsync(refreshToken);
>45:         if (existing?.IsExpired ?? true)
>46:             throw new TokenExpiredException();
...

[3 matches shown, 2 more below threshold]
```

### Budget Handling
- Show matches that fit within budget
- Footer shows: `[N matches shown, M below threshold]`

## Constraints

- **Scope to matched files**: Only search within pattern-matched URIs
- **Use existing embeddings**: Don't generate new embeddings for query

## References

- [Flow: find.md](../../flows/future/read/find.md)
- `search()` UDF documentation

## Error Policy

- No embeddings: Return "Semantic search not ready, try again shortly"
- No matches: Return "No semantic matches for '{keywords}' in N files"
- Keywords too vague: Return results with low confidence warning
