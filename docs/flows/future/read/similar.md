# Read Similar Flow

Find code semantically similar to matched content via embeddings.

## Why This Matters

Similar answers "what code is like this?"—finding patterns to follow, discovering duplicates, and locating related implementations across the codebase.

| Without | With |
|---------|------|
| Manually search for patterns | Automatic discovery of similar code |
| Miss implementations with different names | Semantic similarity ignores naming |
| Reinvent patterns that exist | Find existing examples to follow |

## Trigger

`read("<pattern> => similar", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to source content
**Output**: Content to find similar code for
**Failure**: Invalid pattern returns error with suggestion

### 2. Embedding Comparison
**Actor**: Read tool
**Action**: Find code with similar embeddings to source content
**Output**: Ranked list of similar code segments

Similarity based on:
- Structural similarity (similar AST patterns)
- Semantic similarity (similar purpose/behavior)
- Excludes exact matches and trivial similarities

### 3. Result Filtering
**Actor**: Read tool
**Action**: Filter results to meaningful similarities
**Output**: Similar code above relevance threshold

Filtering removes:
- The source itself
- Near-duplicates from same file
- Boilerplate/generated code (unless source is boilerplate)

### 4. Result Formatting
**Actor**: Read tool
**Action**: Format similar code with similarity context
**Output**: Similar code snippets with relationship to source

Result elements:
- Similar code URI with headline
- Similarity score or ranking
- Brief indication of what's similar (structure, purpose, etc.)
- Snippet showing the similar code

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many similar results as fit within token budget
**Output**: Top similar results that fit, with count of omitted in footer

## Termination

Flow completes when:
- Similar code rendered with snippets
- Footer reports total similar found and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs#symbol=RefreshAsync => similar

file:///src/Auth/SessionService.cs#symbol=RenewAsync [similarity: 0.87]
  Similar: async refresh pattern with validation and regeneration

 42:     public async Task<Session> RenewAsync(string sessionId)
 43:     {
 44:         var existing = await _store.GetAsync(sessionId);
 45:         if (existing?.IsExpired ?? true)
 46:             throw new SessionExpiredException();
 47:         return await GenerateNewSession(existing.UserId);
 48:     }

file:///src/Api/ApiKeyService.cs#symbol=RotateAsync [similarity: 0.72]
  Similar: credential rotation with expiry check

 28:     public async Task<ApiKey> RotateAsync(Guid keyId)
 29:     {
 30:         var current = await _keys.GetAsync(keyId);
 31:         if (current == null) throw new KeyNotFoundException();
 32:         return await GenerateReplacement(current);
 33:     }

[2 similar shown, 3 below threshold]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot find similar without source |
| No similar code found | Return "no similar code found" (source may be unique) |
| Source too small for meaningful comparison | Return "insufficient content for similarity" |

## Verification

| Environment | How |
|-------------|-----|
| Local | Find similar for known pattern; verify related implementations appear |
| Automated tests | Assert: copied function with renamed variables shows as similar |
| Production | Track similarity quality scores; tune threshold based on usefulness |

## Related

- `find.md` — semantic search by keywords (not by example)
- `docs.md` — find related documentation
