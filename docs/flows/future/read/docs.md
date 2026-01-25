# Read Docs Flow

Find documentation that describes or relates to matched code.

## Why This Matters

Docs answers "what documentation exists for this code?"—connecting implementation to explanation, finding design rationale, and locating user-facing documentation.

| Without | With |
|---------|------|
| Search docs folder for class name | Semantic discovery of related docs |
| Miss docs that describe behavior without naming code | Find docs by concept, not just name |
| Code and docs disconnected | Clear links between implementation and explanation |

## Trigger

`read("<pattern> => docs", tokenBudget)`

## Stages

### 1. Pattern Resolution
**Actor**: Read tool
**Action**: Resolve glob/URI pattern to code content
**Output**: Code to find documentation for
**Failure**: Invalid pattern returns error with suggestion

### 2. Documentation Discovery
**Actor**: Read tool
**Action**: Find documentation related to the code
**Output**: Ranked list of related documentation

Discovery methods:
- Explicit references: docs that link to or mention the code
- Semantic similarity: docs describing similar concepts
- Structural: docs in conventional locations (README near code, /docs folder)
- Edge-based: REFERS_TO edges from docs to code

### 3. Relevance Ranking
**Actor**: Read tool
**Action**: Rank documentation by relevance to code
**Output**: Prioritized documentation list

Ranking factors:
- Explicit reference (highest)
- Semantic similarity to code purpose
- Proximity in file structure
- Recency of documentation

### 4. Result Formatting
**Actor**: Read tool
**Action**: Format documentation with relationship context
**Output**: Documentation headlines with relevance indicators

Result elements:
- Documentation URI with headline
- Relationship type (explicit reference, semantic, proximity)
- Relevant excerpt or section heading
- Link type if explicit (mentions, describes, etc.)

### 5. Budget Fitting
**Actor**: Read tool
**Action**: Include as many docs as fit within token budget
**Output**: Top relevant docs that fit, with count of omitted in footer

## Termination

Flow completes when:
- Related documentation rendered with relevance context
- Footer reports total docs found and tokens used

## Example Output

```
file:///src/Auth/TokenService.cs => docs

Explicit references:
  file:///docs/architecture/authentication.md#section=token-lifecycle
    "Token Lifecycle" section describes refresh flow
  file:///docs/api/auth-endpoints.md#section=refresh
    API documentation for /auth/refresh endpoint

Semantic matches:
  file:///docs/security/session-management.md
    Discusses session vs token tradeoffs (related concepts)

Proximity:
  file:///src/Auth/README.md
    Auth module overview

[3 explicit, 1 semantic, 1 proximity]
```

## Error Handling

| Condition | Behaviour |
|-----------|-----------|
| No files match pattern | Return error—cannot find docs without target |
| No documentation found | Return "no related documentation found" |
| Target is documentation | Return related documentation (docs about docs) |

## Verification

| Environment | How |
|-------------|-----|
| Local | Find docs for code with known documentation; verify docs appear |
| Automated tests | Assert: code mentioned in doc shows that doc in results |
| Production | Track documentation coverage; surface code without docs |

## Related

- `similar.md` — find similar code (not documentation)
- `find.md` — search within documentation by keywords
- `question.md` — synthesize answer from documentation
