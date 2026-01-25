# Read Find Flow

Semantic search within matched files, returning precise snippets.

## Why This Matters

Find locates where concepts appear even when terminology varies. Unlike grep, it understands synonyms and related terms. Unlike explore, it's scoped to specific files and returns precise locations.

| Without | With |
|---------|------|
| Grep misses "authenticate" when searching "login" | Semantic match finds related concepts |
| Explore searches whole repo | Scoped to selected files only |
| Chunk-level results require reading context | Precision narrowing shows exact relevant span |

## Trigger

`read("<pattern> => find: <keywords>", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to matching files
**Output**: Set of file URIs
**Failure**: Invalid pattern returns error with suggestion

### 2. Chunk Search
**Actor**: Read tool
**Action**: Search embedded chunks within matched files for semantic similarity to keywords
**Output**: Candidate chunks ranked by embedding similarity

Uses existing chunk embeddings—no new embedding generation for the query beyond the keywords.

### 3. Precision Narrowing
**Actor**: Read tool
**Action**: For high-scoring chunks, narrow to the most relevant span
**Output**: Precise spans within chunks

Narrowing identifies the specific lines that match, not just the chunk that contains a match.

### 4. Snippet Generation
**Actor**: Read tool
**Action**: Generate snippets centered on each match with surrounding context
**Output**: Code snippets with line numbers and URIs

Snippet elements:
- File URI with line fragment (`file:///path#line=N,M`)
- Line numbers matching actual file
- Match context (surrounding lines for understanding)
- Relevance indicator (score or rank)

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many snippets as fit within token budget
**Output**: Ranked snippets that fit, with count of omitted matches in footer

## Termination

Flow completes when:
- Snippets rendered for matches that fit within budget
- Footer reports total matches found, matches shown, and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs#line=42,52  [score: 0.89]

 40:     private readonly ITokenStore _store;
 41:
>42:     public async Task<Token> RefreshAsync(string refreshToken)
>43:     {
>44:         var existing = await _store.GetAsync(refreshToken);
>45:         if (existing?.IsExpired ?? true)
>46:             throw new TokenExpiredException();
>47:         return await GenerateTokenPair(existing.UserId);
>48:     }
 49:
 50:     private async Task<Token> GenerateTokenPair(Guid userId)

[3 matches shown, 2 more below budget threshold]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot search without files |
| No semantic matches found | Return "no matches" with files searched count |
| All matches below relevance threshold | Return "no strong matches" with best weak match shown |

## Verification

| Environment | How |
|-------------|-----|
| Local | Search for concept in known files; verify relevant code found even with different terminology |
| Automated tests | Assert: search "authentication" finds code using "login", "credentials", "session" |
| Production | Track match quality scores; monitor for low-confidence results |

## Related

- `grep.md` — literal string search
- `regex.md` — pattern-based search
- `question.md` — synthesis from content (not location finding)
- `explore(Locate)` — repo-wide semantic search
