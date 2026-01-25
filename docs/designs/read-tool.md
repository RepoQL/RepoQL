# Read Tool Design

## North Star

One tool to retrieve and display repository content. Pattern selects, modifier transforms, budget constrains. The agent gets exactly what they need in the tokens they're willing to spend.

## Context

The read tool is the agent's primary interface for examining repository content. It consolidates what would otherwise be many separate tools (view file, search in file, show history, traverse graph) into a unified syntax that composes naturally.

**Enables flows:** All 19 read modifier flows documented in `docs/flows/future/read/`

**Informed by:** North star `docs/north-star/read-tool.md`

## Existing Infrastructure

Significant infrastructure already exists and must be leveraged:

| Component | Location | Capability |
|-----------|----------|------------|
| `RepoUriGlobMatcher` | `RepoQL.Contracts` | Git-style glob matching with fragment support (`#symbol=`, `#line=`) |
| `UriPatternMatcher` | `RepoQL.Contracts` | Compound patterns (`;` delimited, `!` for exclusion) |
| `ReadOrchestrator` | `RepoQL.Explore` | Progressive disclosure, tree format, question synthesis |
| `IReadContentProvider` | `RepoQL.Explore` | Document fetching with headline/structure/content |
| `glob_files()` | DuckDB UDF | SQL-level glob resolution |
| `matches_glob()` | DuckDB UDF | SQL-level glob matching |

**Current syntax already supported:**
- `<uri>` — direct read with auto representation selection
- `<glob>` — multi-file read distributing budget
- `<glob> => tree` — directory tree format
- `<uri> // <question>` — LLM synthesis (being replaced by `=> question:`)

**This design extends** the existing `ReadOrchestrator` with additional modifiers rather than replacing it.

**Syntax migration:** The `// question` syntax is replaced by `=> question: <q>` for consistency with other modifiers. Existing behavior is preserved during transition.

## Constraints

- Single tool surface—agents learn one syntax
- Token budget is hard constraint by default, soft with confirmation
- Build on existing infrastructure (pattern matching, progressive disclosure, content provider)
- Output suitable for LLM consumption (structured, token-efficient)
- Performance acceptable for interactive use (<2s for common operations)

---

## Components

The design extends `ReadOrchestrator` with a modifier dispatch layer:

```
┌─────────────────────────────────────────────────────────────────────┐
│                      ReadOrchestrator (existing)                     │
│  - Pattern parsing (glob, URI, fragments)                            │
│  - Budget-based representation selection                             │
│  - Tree format, question synthesis                                   │
└─────────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    ModifierDispatcher (new)                          │
│  - Parse => <modifier>: <param> syntax                               │
│  - Route to appropriate handler                                      │
│  - Fall through to existing behavior when no modifier                │
└─────────────────────────────────────────────────────────────────────┘
                                   │
         ┌─────────────┬──────────┼──────────┬─────────────┐
         ▼             ▼          ▼          ▼             ▼
┌─────────────┐ ┌───────────┐ ┌────────┐ ┌──────────┐ ┌─────────┐
│Representation│ │  Search   │ │ Graph  │ │Diagnostics│ │ History │
│  Handlers   │ │ Handlers  │ │Handlers│ │ Handlers │ │ Handlers│
└─────────────┘ └───────────┘ └────────┘ └──────────┘ └─────────┘
         │             │          │          │             │
         └─────────────┴──────────┼──────────┴─────────────┘
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│                  RepresentationFormatter (existing)                  │
│  - Status footer with token count                                    │
│  - Representation hints                                              │
└─────────────────────────────────────────────────────────────────────┘
```

**Key principle:** No modifier = existing `ReadOrchestrator` behavior unchanged. Modifiers extend, not replace.

---

## Contracts

### Existing (reuse)

| Contract | Location | Used For |
|----------|----------|----------|
| `RepoUriGlobMatcher.IsMatch` | `RepoQL.Contracts` | Pattern matching |
| `UriPatternMatcher.Matches` | `RepoQL.Contracts` | Compound patterns |
| `IReadContentProvider` | `RepoQL.Explore` | Document fetching |
| `ReadDocument` | `RepoQL.Explore` | Document with headline/structure/content |
| `ReadExecutionResult` | `RepoQL.Explore` | Result with metadata |

### New Contracts

#### ParsedReadRequest

```csharp
public record ParsedReadRequest(
    string Pattern,              // Original pattern (glob or URI)
    string? Modifier,            // null for default, or "headline", "find", etc.
    string? Parameter,           // Modifier parameter (question text, keywords, etc.)
    int TokenBudget
);
```

#### IModifierHandler

```csharp
public interface IModifierHandler
{
    string ModifierName { get; }
    bool CanHandle(string? modifier);
    Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct
    );
}

public record ModifierResult(
    string Content,
    int TokenCount,
    int TotalAvailable,          // Total results if budget were unlimited
    int Shown,                   // Results included in content
    bool ExceedsBudget,
    ResultMetadata Metadata
);

public record ResultMetadata(
    IReadOnlyList<string> FilesConsulted,
    string? Warning,
    Dictionary<string, object> Extra  // Handler-specific metadata
);
```

**Note:** Pattern resolution uses existing `IReadContentProvider.FetchGlobAsync` and `FetchDocumentAsync`—no new resolver needed.

Budget enforcement is simple business logic within `ModifierDispatcher`:
- Check if `result.TokenCount > budget`
- If exceeded, cache result keyed by request hash, return confirmation message
- On next request, check cache first—if hit, return cached result

---

## Modifier Handlers

### Representation Handlers

| Handler | Modifier | Existing Infrastructure Used |
|---------|----------|------------------------------|
| `DefaultHandler` | *(none)* | `ReadOrchestrator.SelectRepresentation` (existing) |
| `HeadlineHandler` | `headline` | `ReadDocument.Headline` |
| `StructureHandler` | `structure` | `ReadDocument.Structure` |
| `ContentHandler` | `content` | `ReadDocument.TextContent` |
| `TreeHandler` | `tree` | `IReadContentProvider.FormatAsTreeAsync` (existing) |

These handlers primarily select from existing `ReadDocument` representations—minimal new code.

### Search Handlers

| Handler | Modifier | Existing Infrastructure Used |
|---------|----------|------------------------------|
| `QuestionHandler` | `question:` | `ILlmProvider.SummarizeAsync`, `ExploreOrchestrator` (existing) |
| `FindHandler` | `find:` | `search()` UDF, chunk embeddings (existing) |
| `GrepHandler` | `grep:` | Text search on `ReadDocument.TextContent` |
| `RegexHandler` | `regex:` | RE2 via .NET `Regex` |
| `AstGrepHandler` | `astgrep:` | ast-grep binary (new optional dependency) |

### Graph Handlers

| Handler | Modifier | Existing Infrastructure Used |
|---------|----------|------------------------------|
| `EdgeHandler` | `<edge_type>` | `edge` table, DuckDB queries (existing) |
| `RootsHandler` | `roots` | `edge` table traversal, `node.kind` for classification |
| `LeavesHandler` | `leaves` | `edge` table traversal |
| `TestsHandler` | `tests` | `edge` traversal + test file detection via path/annotations |
| `SimilarHandler` | `similar` | `related()` UDF (existing) |
| `DocsHandler` | `docs` | `edge` REFERS_TO queries, `Files` view for markdown |

### Diagnostics Handlers

| Handler | Modifier | Existing Infrastructure Used |
|---------|----------|------------------------------|
| `LintHandler` | `lint`, `lint:` | `Annotations` view (existing) |

### History Handlers

| Handler | Modifier | Existing Infrastructure Used |
|---------|----------|------------------------------|
| `HistoryHandler` | `history`, `history:` | `git_log()`, `git_diff()` UDFs (existing), embeddings for keyword ranking |
| `ChangesHandler` | `changes` | `git_status()`, `git_diff()` UDFs (existing) |
| `BlameHandler` | `blame` | `git_blame()` UDF (existing) |

---

## Data Flow

### Request with Modifier

```
read("src/**/*.cs => find: authentication", 2000)
    │
    ▼
ModifierDispatcher.Parse(input)
    → ParsedReadRequest(Pattern="src/**/*.cs", Modifier="find", Parameter="authentication")
    │
    ▼
IReadContentProvider.FetchGlobAsync("src/**/*.cs")  [existing]
    → IReadOnlyList<ReadDocument> (42 documents)
    │
    ▼
ModifierDispatcher.Dispatch("find")
    → FindHandler
    │
    ▼
FindHandler.ExecuteAsync(documents, "authentication", 2000)
    │
    ├─► Query chunk embeddings for matched file URIs
    ├─► Rank by similarity to "authentication"
    ├─► Narrow to precise spans
    ├─► Generate snippets
    │
    → ModifierResult(Content="...", TokenCount=1850, Shown=8, TotalAvailable=12)
    │
    ▼
[1850 < 2000, within budget]
    │
    ▼
RepresentationFormatter.FormatStatusFooter(...)  [existing]
    → Final output with snippets and footer
```

### Request without Modifier (existing behavior)

```
read("src/Auth/TokenService.cs", 2000)
    │
    ▼
ModifierDispatcher.Parse(input)
    → ParsedReadRequest(Pattern="src/Auth/TokenService.cs", Modifier=null)
    │
    ▼
[Fall through to existing ReadOrchestrator.ExecuteDirectAsync]
    → Existing progressive disclosure behavior unchanged
```

### Over-Budget Request

```
read("src/**/*.cs => content", 1000)
    │
    ▼
... pattern resolution, handler execution ...
    │
    → ModifierResult(TokenCount=15000)
    │
    ▼
[15000 > 1000, exceeds budget]
    │
    ├─► Cache result keyed by request hash
    │
    ▼
Return confirmation message:
    → "Results would use ~15000 tokens (budget: 1000).
        42 files matched. Repeat request to proceed."
```

### Repeat-to-Confirm

```
read("src/**/*.cs => content", 1000)  [exact repeat]
    │
    ▼
[Check cache for request hash → hit]
    │
    ▼
Return cached result directly (no re-execution)
    → Full content, footer notes budget was overridden
```

---

## Cross-Cutting Concerns

### Token Budget Enforcement

All handlers receive the token budget and must respect it:

1. **Representation handlers**: Select representation level that fits, or request confirmation
2. **Search handlers**: Return top N results that fit, footer shows omitted count
3. **Graph handlers**: Traverse to depth that fits, indicate truncation
4. **History handlers**: Return recent/relevant entries that fit

Budget enforcement happens in two places:
- Within handlers (for progressive selection)
- In dispatcher (for final confirmation check and caching)

### Repeat-to-Confirm State

When a request exceeds budget, cache the full computed result keyed by request hash. If same request arrives again within window (e.g., 60 seconds), return the cached result directly—no re-execution needed.

State stored in memory (not persisted). Hash includes: pattern + modifier + parameter + budget. Cache entries expire after window or on any file system change to matched files.

### Output Format Consistency

All handlers produce output following consistent patterns:

```
[File/location header]

[Content appropriate to modifier]

[Footer: tokens used, items shown/total, warnings]
```

Footer always present. Enables agent to understand what they got vs. what exists.

### Error Handling

| Error Type | Behavior |
|------------|----------|
| Invalid pattern syntax | Return error with corrected suggestion |
| No files match | Return "no matches" with pattern echoed |
| Modifier unknown | Return error listing valid modifiers |
| Handler failure | Return error with diagnostic, don't crash |
| Partial failure (some files) | Return what succeeded, note failures in footer |

Errors never silent. Agent always knows what happened.

### Caching

| Operation | Cache Strategy |
|-----------|----------------|
| Pattern resolution | Short TTL (file system can change) |
| X-Ray representations | Indexed, always fresh |
| Embeddings | Indexed, epoch-tracked |
| Git operations | No cache (always current) |
| LLM synthesis | No cache (depends on context) |

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Single unified syntax | Separate tools per modifier | Learnability; one pattern to understand |
| Budget as soft constraint | Hard limit always | Agents need escape hatch for deliberate large reads |
| Repeat-to-confirm | Explicit confirm parameter | Natural interaction; no syntax addition |
| Modifier handlers as plugins | Hardcoded switch | Extensibility; new modifiers don't touch core |
| Token counting at output | Per-item estimation | Accuracy; actual cost matters |
| Footer always present | Clean output | Agent needs metadata to make decisions |

## Alternatives Considered

**Separate tools per modifier type**: More granular but agents must learn many tools. Unified syntax is more discoverable.

**Streaming output**: Could stream large results. Adds complexity; agents typically process complete responses. Defer until needed.

**Budget as hard limit only**: Simpler but frustrating when agent needs full content. Repeat-to-confirm balances safety with flexibility.

**Modifier chaining** (e.g., `=> find: auth => structure`): Powerful but complex. Query tool handles composition; read stays focused.

## Risks

| Risk | Mitigation |
|------|------------|
| Handler performance varies widely | Document expected latency per modifier; async execution |
| ast-grep binary dependency | Optional; graceful fallback if not available |
| LLM synthesis cost | Question handler uses scoped content; budget limits context |
| Graph traversal explosion | Max depth limits; early termination with indication |
| Git operations on large repos | Scope to matched files; limit history depth |

## Extension Points

- **IModifierHandler**: Add new modifiers without core changes
- **OutputFormatter**: Customize output format for different consumers

---

## Dependencies

### Existing Infrastructure (no changes needed)

| Component | Purpose |
|-----------|---------|
| `RepoUriGlobMatcher` | Pattern matching with fragment support |
| `IReadContentProvider` | Document fetching, tree formatting |
| `ReadOrchestrator` | Progressive disclosure, question synthesis |
| `RepresentationFormatter` | Status footer formatting |
| X-Ray representations | Headline, structure generation |
| Embeddings infrastructure | find, similar, history keyword ranking |
| DuckDB graph queries | Edge traversal, annotation queries |
| Git functions | history, changes, blame |
| `ILlmProvider` | question modifier |

### New Dependencies

| Component | Purpose | Notes |
|-----------|---------|-------|
| ast-grep binary | astgrep modifier | Optional; graceful fallback if unavailable |

**Key insight:** All modifier handlers compose existing capabilities. The design adds dispatch and budget enforcement, not new data infrastructure.
