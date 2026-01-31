---
description: Design for RepoQL web UI - observability and testing dashboard
tags: [ui, design, blazor, observability, testing]
audience: { human: 50, agent: 50 }
purpose: { design: 90, reference: 10 }
---

# RepoQL Web UI — Design

## North Star

See everything RepoQL knows, test everything it does, diagnose everything that breaks — without leaving the browser.

> Reference: `docs/north-star/web-ui.md`

## Context

RepoQL needs a web interface for developers working on or with RepoQL. The UI serves two purposes:

1. **Observability** — Is it working? Is it done? What's wrong?
2. **Testing** — Do queries work? Does search find things? Is parsing correct?

**This design enables these flows:**
- Status Streaming (`docs/flows/ui/status-streaming.md`)
- Query Execution (`docs/flows/ui/query-execution.md`)
- Search Testing (`docs/flows/ui/search-testing.md`)
- Read Testing (`docs/flows/ui/read-testing.md`)
- File Inspection (`docs/flows/ui/file-inspection.md`)
- Edge Traversal (`docs/flows/ui/edge-traversal.md`)
- Diagnosis (`docs/flows/ui/diagnosis.md`)
- Annotations Browsing (`docs/flows/ui/annotations-browsing.md`)
- Imports Management (`docs/flows/ui/imports-management.md`)
- Git Integration (`docs/flows/ui/git-integration.md`)

## Constraints

| Constraint | Implication |
|------------|-------------|
| Local development tool | No auth, no multi-user, no persistence beyond session |
| Must run on developer laptop | Lightweight, no external dependencies |
| RepoQL host communicates via Unix socket / named pipe | Need gRPC client that works with local sockets |
| Real-time status updates required | Push-based architecture, not polling |
| Developers test with arbitrary SQL | Must handle any query, any result shape |
| Cross-platform (Windows, macOS, Linux) | No platform-specific UI dependencies |

---

## Components

```
┌─────────────────────────────────────────────────────────────────────┐
│                            Browser                                   │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │                         Shell                                  │  │
│  │  ┌─────────┐ ┌─────────────────────────────────────────────┐  │  │
│  │  │ Status  │ │                  Content                     │  │  │
│  │  │   Bar   │ │  ┌─────────────────────────────────────────┐ │  │  │
│  │  └─────────┘ │  │              Active View                 │ │  │  │
│  │  ┌─────────┐ │  │                                         │ │  │  │
│  │  │   Nav   │ │  │  (Query | Search | Read | Inspect | ...) │ │  │  │
│  │  │         │ │  │                                         │ │  │  │
│  │  └─────────┘ │  └─────────────────────────────────────────┘ │  │  │
│  │              └─────────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
                                   │
                                   │ SignalR (WebSocket)
                                   ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         Blazor Server                                │
│                                                                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐              │
│  │ StatusStore  │  │  Navigation  │  │   Services   │              │
│  │              │  │    State     │  │              │              │
│  │ - Snapshot   │  │              │  │ - Query      │              │
│  │ - Pipeline   │  │ - History    │  │ - Search     │              │
│  │ - Health     │  │ - Current    │  │ - Read       │              │
│  │ - Stats      │  │              │  │ - Inspect    │              │
│  └──────────────┘  └──────────────┘  │ - Git        │              │
│         ▲                            │ - Imports    │              │
│         │                            └──────────────┘              │
│         │                                   │                       │
│  ┌──────────────┐                          │                       │
│  │ StatusStream │◄─────────────────────────┤                       │
│  │   Service    │                          │                       │
│  └──────────────┘                          │                       │
│         │                                   │                       │
└─────────│───────────────────────────────────│───────────────────────┘
          │                                   │
          │ gRPC (Unix socket)                │ gRPC (Unix socket)
          ▼                                   ▼
┌─────────────────────────────────────────────────────────────────────┐
│                          RepoQL Host                                 │
│                                                                      │
│  WatchStatus (stream)  │  ExecuteRawQuery  │  Explore  │  Read     │
│                        │  ImportRepository │  ...      │  ...      │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Views

Each view maps to one or more flows. Views are the primary unit of navigation.

| View | Primary Flow | Purpose |
|------|--------------|---------|
| **Status** | Status Streaming, Diagnosis | Health at a glance, problem surfacing |
| **Query** | Query Execution | Run SQL, see results |
| **Search** | Search Testing | Test explore, see scores |
| **Read** | Read Testing | Test read with URIs/modifiers |
| **Inspect** | File Inspection, Edge Traversal | See everything about a file |
| **Annotations** | Annotations Browsing | Errors across repo |
| **Imports** | Imports Management | External repositories |
| **Git** | Git Integration | Blame, history, hotspots |

### View: Status

The landing page. Answers: "Is it working? What's wrong?"

```
┌─────────────────────────────────────────────────────────────────┐
│ [●] Online — Idle — 4,231 files — Embeddings ready              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  PIPELINE                          HEALTH                        │
│  ┌────────────────────────┐       ┌────────────────────────┐    │
│  │ Hot Path: Idle         │       │ ✓ All systems healthy  │    │
│  │ ░░░░░░░░░░░░░░░░░░░░░░ │       │                        │    │
│  │                        │       │ (or problem cards)     │    │
│  │ Idle Processing: Idle  │       │                        │    │
│  │ ░░░░░░░░░░░░░░░░░░░░░░ │       └────────────────────────┘    │
│  └────────────────────────┘                                      │
│                                                                  │
│  QUICK STATS                                                     │
│  Files: 4,231 │ Nodes: 47,892 │ Edges: 31,456 │ Annotations: 847│
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

When problems exist, **Problem Cards** appear (per Diagnosis flow):
- Stuck file card
- Embeddings stalled card
- Pipeline backpressure card
- Connection lost card

### View: Query

SQL execution. Answers: "Does this query work?"

```
┌─────────────────────────────────────────────────────────────────┐
│ SQL                                                    [Run ▶]  │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ SELECT * FROM Files WHERE error_count > 0 LIMIT 20;        │ │
│ └─────────────────────────────────────────────────────────────┘ │
│ Row limit: [200    ]                                            │
├─────────────────────────────────────────────────────────────────┤
│ Results (18 rows, 23ms)                                         │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ uri                    │ error_count │ warning_count │ ...  │ │
│ ├────────────────────────┼─────────────┼───────────────┼──────┤ │
│ │ file:///src/Legacy/... │ 3           │ 12            │ ...  │ │
│ │ file:///src/Data/...   │ 1           │ 5             │ ...  │ │
│ └─────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

**Help panel** (collapsible): Lists available views, common macros, example queries.

### View: Search

Explore tool testing. Answers: "Does search find things? Why this ranking?"

```
┌─────────────────────────────────────────────────────────────────┐
│ SEARCH PARAMETERS                                               │
│                                                                  │
│ Keywords: [authentication token refresh              ]          │
│ Intent:   [Locate ▼]     Budget: [====|====] 2000              │
│ Scope:    [file:///src/**                            ]          │
│ Boost:    [Auth.*                                    ]          │
│ Penalize: [(?i)test|mock                             ]          │
│                                                     [Search ▶]  │
├─────────────────────────────────────────────────────────────────┤
│ Readiness: ✓ Ready (4,231 files, all embedded)      47ms       │
├─────────────────────────────────────────────────────────────────┤
│ RESULTS                                                         │
│                                                                  │
│ 1. src/Auth/AuthService.cs                          Score: 0.89│
│    JWT authentication and token refresh logic                   │
│    ├─ Semantic: 0.94  BM25: 0.41  Fuzzy: 0.22                  │
│    └─ Boosted by: Auth.* pattern                               │
│                                                    [Inspect →] │
│                                                                  │
│ 2. src/Auth/TokenValidator.cs                       Score: 0.81│
│    Token validation and claims extraction                       │
│    ├─ Semantic: 0.87  BM25: 0.38  Fuzzy: 0.18                  │
│    └─ Boosted by: Auth.* pattern                               │
│                                                    [Inspect →] │
└─────────────────────────────────────────────────────────────────┘
```

**Key feature**: Score breakdown visible per result. This is the north star requirement.

### View: Read

Read tool testing. Answers: "What does read return for this URI?"

```
┌─────────────────────────────────────────────────────────────────┐
│ READ PARAMETERS                                                  │
│                                                                  │
│ URI:      [file:///src/Auth/**/*.cs#symbol=*Service ]           │
│ Budget:   [====|========] 3000                                  │
│ Modifier: [tree: headlines ▼]                                   │
│ Question: [                                          ] (optional)│
│                                                      [Read ▶]   │
├─────────────────────────────────────────────────────────────────┤
│ OUTPUT (1,247 tokens / 3,000 budget)                            │
│ Detail level: structure (full would be 4,892 tokens)            │
├─────────────────────────────────────────────────────────────────┤
│ src/Auth/                                                        │
│ ├── AuthService.cs — JWT authentication and token refresh       │
│ │   ├─ ValidateToken(string token)                              │
│ │   ├─ RefreshToken(string refresh)                             │
│ │   └─ RevokeToken(string token)                                │
│ ├── TokenValidator.cs — Token validation logic                  │
│ │   ├─ IsExpired(JwtToken token)                                │
│ │   └─ HasClaim(JwtToken token, string claim)                   │
│ └── Claims.cs — Claims parsing utilities                        │
└─────────────────────────────────────────────────────────────────┘
```

**Key feature**: Shows detail level and why. Budget slider lets user explore progressive disclosure.

### View: Inspect

File inspection. Answers: "What does RepoQL know about this file?"

```
┌─────────────────────────────────────────────────────────────────┐
│ file:///src/Auth/AuthService.cs                    [← Back]    │
│ C# │ 342 lines │ ~2.1k tokens │ Embeddings: ✓                  │
├─────────────────────────────────────────────────────────────────┤
│ HEADLINE                                                         │
│ AuthService — JWT authentication and token refresh              │
├─────────────────────────────────────────────────────────────────┤
│ ▶ STRUCTURE (click to expand)                                   │
├─────────────────────────────────────────────────────────────────┤
│ NODES (12)                                                       │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ Classes                                                      │ │
│ │   └─ AuthService [15-342]                                    │ │
│ │ Methods                                                      │ │
│ │   ├─ ValidateToken [47-89]                                   │ │
│ │   ├─ RefreshToken [91-156]                                   │ │
│ │   └─ RevokeToken [158-203]                                   │ │
│ └─────────────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────┤
│ EDGES                                                            │
│ ┌─────────────────────────┐ ┌─────────────────────────────────┐ │
│ │ Outgoing (→)            │ │ Incoming (←)                    │ │
│ │ CALLS UserRepo [52]  →  │ │ ← LoginController               │ │
│ │ IMPORTS JwtLib [3]   →  │ │ ← ApiGateway                    │ │
│ └─────────────────────────┘ └─────────────────────────────────┘ │
├─────────────────────────────────────────────────────────────────┤
│ ANNOTATIONS (2)                                                  │
│ ⚠ CA1822: Consider static [line 112]                            │
│ ⚠ IDE0060: Unused parameter 'options' [line 47]                 │
├─────────────────────────────────────────────────────────────────┤
│ [Blame] [History] [Similar Files]                               │
└─────────────────────────────────────────────────────────────────┘
```

Edges are clickable → triggers Edge Traversal flow → loads target in Inspect view.

### View: Annotations

Repo-wide diagnostics. Answers: "What's broken across the codebase?"

```
┌─────────────────────────────────────────────────────────────────┐
│ ANNOTATIONS                                                      │
│ Errors: 12 │ Warnings: 847 │ Info: 2,341                        │
├─────────────────────────────────────────────────────────────────┤
│ Severity: [Warning ▼]  Rule: [All ▼]  Pattern: [          ]    │
│ Group by: ○ File  ● Rule  ○ None                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ CA1822: Consider making method static (23)                      │
│ ├─ src/Auth/AuthService.cs:112                         [→]     │
│ ├─ src/Auth/AuthService.cs:156                         [→]     │
│ └─ [+21 more]                                                   │
│                                                                  │
│ IDE0060: Remove unused parameter (18)                           │
│ ├─ src/Auth/AuthService.cs:47 — parameter 'options'    [→]     │
│ └─ [+17 more]                                                   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

Click `[→]` navigates to Inspect view at that line.

### View: Imports

External repository management. Answers: "What external code is indexed?"

```
┌─────────────────────────────────────────────────────────────────┐
│ IMPORTS                                              [+ Add]    │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│ ✓ github://anthropics/claude-code                               │
│   1,247 files │ Last indexed: 2 hours ago                       │
│   [Reindex] [Remove] [Browse]                                   │
│                                                                  │
│ ⟳ github://microsoft/typescript                                 │
│   Indexing... 5,538 / 8,932 files                               │
│   ████████████░░░░░░░░ 62%                          [Cancel]    │
│                                                                  │
│ ⚠ github://example/broken-repo                                  │
│   Error: Repository not found                                   │
│   [Retry] [Remove]                                              │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### View: Git

Git integration. Answers: "Who changed this? What changes most?"

Three sub-views accessible from file context or navigation:

1. **Blame** — Per-line attribution (from Inspect view)
2. **Hotspots** — Files ranked by change frequency
3. **Related** — Commits related to a search term

---

## Contracts

### IStatusStore

Central state container for host status. Components subscribe to changes.

```csharp
public interface IStatusStore
{
    HostStatus Current { get; }
    PipelineStatus? Pipeline { get; }
    IReadOnlyList<HealthEvent> RecentHealth { get; }
    StatsSnapshot? Stats { get; }

    event Action OnChange;

    void Update(StatusEvent evt);
}

public record HostStatus(
    ConnectionState State,      // Online, Offline, Reconnecting
    string Message,
    DateTime UpdatedAt
);

public enum ConnectionState { Online, Offline, Reconnecting }
```

### INavigationState

Manages view navigation with history for back traversal.

```csharp
public interface INavigationState
{
    NavigationEntry Current { get; }
    bool CanGoBack { get; }

    void NavigateTo(string view, NavigationParams? @params = null);
    void GoBack();

    event Action OnChange;
}

public record NavigationEntry(
    string View,
    NavigationParams? Params,
    int? ScrollPosition
);

public record NavigationParams(
    string? Uri = null,
    int? Line = null,
    string? Query = null
);
```

### IQueryService

Executes SQL queries against RepoQL.

```csharp
public interface IQueryService
{
    Task<QueryResult> ExecuteAsync(
        string sql,
        int? rowLimit = null,
        CancellationToken ct = default);
}

public record QueryResult(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int TotalRows,
    bool Truncated,
    TimeSpan Duration,
    string? Error
);
```

### ISearchService

Tests the explore tool with score retrieval.

```csharp
public interface ISearchService
{
    Task<SearchResult> SearchAsync(SearchParams @params, CancellationToken ct = default);
    Task<ReadinessResult> CheckReadinessAsync(string? scope, CancellationToken ct = default);
}

public record SearchParams(
    string Keywords,
    ExploreIntent Intent,
    int TokenBudget,
    string? Scope = null,
    string? Boost = null,
    string? Penalize = null,
    int? Limit = null
);

public record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    ReadinessResult Readiness,
    TimeSpan Duration,
    string? Error
);

public record SearchHit(
    string Uri,
    string Headline,
    float Score,
    float SemanticScore,
    float Bm25Score,
    float FuzzyScore,
    bool Boosted,
    bool Penalized,
    string? BoostReason
);
```

### IReadService

Tests the read tool.

```csharp
public interface IReadService
{
    Task<ReadResult> ReadAsync(ReadParams @params, CancellationToken ct = default);
}

public record ReadParams(
    string Uri,
    int TokenBudget,
    string? Modifier = null,
    string? Question = null
);

public record ReadResult(
    string Content,
    int TokensUsed,
    string DetailLevel,     // "headline", "structure", "full"
    int FullTokenCount,     // What full content would cost
    TimeSpan Duration,
    string? Error
);
```

### IInspectService

Retrieves everything about a file.

```csharp
public interface IInspectService
{
    Task<InspectResult> InspectAsync(string uri, CancellationToken ct = default);
}

public record InspectResult(
    FileMetadata Metadata,
    IReadOnlyList<NodeInfo> Nodes,
    IReadOnlyList<EdgeInfo> OutgoingEdges,
    IReadOnlyList<EdgeInfo> IncomingEdges,
    IReadOnlyList<AnnotationInfo> Annotations,
    EmbeddingStatus Embeddings,
    string? Error
);

public record EdgeInfo(
    string Type,
    string TargetUri,
    string? TargetHeadline,
    int? SourceLine,
    bool IsResolved
);
```

---

## Data Flow

### Status Updates

```
Host                    Blazor Server                Browser
  │                           │                          │
  │ ──WatchStatus stream──►   │                          │
  │    StatusEvent            │                          │
  │                           │                          │
  │                     StatusStreamService              │
  │                           │                          │
  │                           ├──► StatusStore.Update()  │
  │                           │         │                │
  │                           │         ▼                │
  │                           │    OnChange event        │
  │                           │         │                │
  │                           │ ◄───────┘                │
  │                           │                          │
  │                           │ ──SignalR push──►        │
  │                           │                   Component re-render
```

### Query Execution

```
User                    Component               Service                 Host
  │                          │                      │                      │
  │ ──Click Run──►           │                      │                      │
  │                          │                      │                      │
  │                    Show loading                 │                      │
  │                          │                      │                      │
  │                          │ ──ExecuteAsync──►    │                      │
  │                          │                      │ ──gRPC──►            │
  │                          │                      │    ExecuteRawQuery   │
  │                          │                      │                      │
  │                          │                      │ ◄──Response──        │
  │                          │ ◄──QueryResult──     │                      │
  │                          │                      │                      │
  │                    Render results               │                      │
  │ ◄──See results──         │                      │                      │
```

### Edge Traversal

```
User                    InspectView          NavigationState          InspectService
  │                          │                      │                      │
  │ ──Click edge──►          │                      │                      │
  │                          │                      │                      │
  │                    Extract target URI          │                      │
  │                          │                      │                      │
  │                          │ ──NavigateTo──►     │                      │
  │                          │    (Inspect, uri)   │                      │
  │                          │                      │                      │
  │                          │                 Push to history            │
  │                          │                 Fire OnChange              │
  │                          │                      │                      │
  │                    Re-render with new URI       │                      │
  │                          │                      │                      │
  │                          │ ──────────────InspectAsync──────────────►  │
  │                          │                      │                      │
  │                          │ ◄─────────────InspectResult────────────    │
  │                          │                      │                      │
  │ ◄──See new file──        │                      │                      │
```

---

## Cross-Cutting Concerns

### Error Handling

| Error Type | Handling |
|------------|----------|
| Connection lost | StatusStore → Offline state → Banner appears → Auto-reconnect |
| Query error | Inline in results area, red background, full message |
| Service timeout | "Request timed out" with retry button |
| gRPC failure | Logged, user sees "Service unavailable" |

**Principle**: Errors appear where the user is looking, not in a toast or separate panel.

### Loading States

Every async operation shows loading state:
- Button text changes ("Run" → "Running...")
- Skeleton UI for results area
- Progress bar for long operations (imports)

**Principle**: User always knows something is happening.

### Cancellation

Every async operation is cancellable:
- Cancel button appears during execution
- Component disposal cancels in-flight requests
- Navigating away cancels previous view's requests

### State Persistence

Session-only persistence:
- Query text preserved when switching views
- Search parameters preserved
- Navigation history (10 entries)
- No localStorage, no cookies, no server-side persistence

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+Enter` | Execute (Query, Search, Read) |
| `Escape` | Cancel operation / Close modal |
| `Alt+←` | Back (navigation) |
| `Ctrl+K` | Focus search |

---

## Technology Choices

| Choice | Rationale |
|--------|-----------|
| **Blazor Server** | Direct gRPC over Unix socket; SignalR for push updates; C# throughout |
| **No component library** | Keep simple; vanilla HTML + CSS; avoid dependency churn |
| **gRPC client** | `Grpc.Net.Client` with Unix socket channel |
| **State via events** | Simple `Action OnChange` pattern; no external state library |

### Why Blazor Server (not WebAssembly, not React)

| Alternative | Why Not |
|-------------|---------|
| Blazor WebAssembly | Can't access Unix socket from browser; would need REST API layer |
| React/Vue SPA | Same problem; also requires separate build/deploy |
| Server-rendered HTML | No real-time updates without polling |

Blazor Server is uniquely suited: server-side C# can use gRPC directly, SignalR provides push updates to browser.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Vanilla CSS | Tailwind/Bootstrap | Fewer dependencies; full control; small app |
| Event-based state | Redux/Flux pattern | Simpler for this scale; no actions/reducers overhead |
| Inline errors | Toast notifications | User sees error where they're looking |
| Session-only state | Persistent storage | Local dev tool; simplicity over features |
| Parallel queries in Inspect | Sequential | Faster load; no dependencies between them |
| gRPC direct | REST wrapper | One less layer; already have gRPC contracts |

## Alternatives Considered

**REST API layer**: Would enable WebAssembly or SPA, but adds complexity without benefit for local tool.

**Polling for status**: Simpler than streaming, but latency unacceptable for "is it working?" feedback.

**Monaco editor for Query**: Better editing experience, but adds ~2MB bundle; textarea sufficient for now.

**Graph visualization library (D3, Cytoscape)**: For edge traversal visualization. Deferred — clickable edges in Inspect view covers the use case with less complexity.

## Risks

| Risk | Mitigation |
|------|------------|
| SignalR circuit disconnects | Auto-reconnect with exponential backoff; clear offline indicator |
| Large query results exhaust memory | Row limit default (200); server-side pagination |
| Score retrieval adds latency | Run explore + search() in parallel; show explore results first |
| Many files in Inspect edges | Limit to 50 per direction; "show more" link |

## Extension Points

| Extension | How |
|-----------|-----|
| New view | Add View component, register in Nav, add route |
| New service | Implement interface, register in DI |
| Query presets | Config file or embedded resource |
| Custom themes | CSS variables |

---

## What This Design Does NOT Decide

- Visual styling (colors, spacing, fonts)
- Exact layout dimensions
- Animation/transition details
- Responsive breakpoints
- Accessibility specifics (ARIA, focus management)

These belong in implementation or a separate visual design document.

---

*The simplest thing that makes the flows real.*
