---
description: Research findings on RepoQL web UI for observability and testing
tags: [ui, observability, blazor, dashboard, research]
audience: { human: 70, agent: 30 }
purpose: { research: 85, design: 15 }
---

# RepoQL Web UI Research

Research for designing/enhancing a web UI to observe RepoQL's functioning and test its features.

*Research date: 2026-01-31*

## Context

RepoQL needs a web interface for:
1. **Observability** - Monitor indexing pipeline, health status, metrics
2. **Testing** - Interactively explore query surface, search, and document retrieval

**Key discovery**: A Blazor Server dashboard (`RepoQL.Web`) already exists with substantial functionality. This research examines what's built, what's missing, and what opportunities exist.

## Current State: Existing Blazor Dashboard

> Source: `src/RepoQL.Web/` (34 files, ~50k tokens)

### Implemented Pages

| Page | Lines | Purpose | Status |
|------|-------|---------|--------|
| Overview.razor | 566 | Live status bar, key metrics, pipeline view, health panel | Implemented |
| Status.razor | 556 | Operations dashboard, pipeline stages, reindex/import controls | Implemented |
| Stats.razor | 287 | Detailed statistics by media type, node kind, annotations | Implemented |
| QueryStudio.razor | 172 | SQL editor with result grid | Implemented |
| Explorer.razor | 527 | Document browser with tree view | Implemented |
| Explore.razor | 503 | X-ray search interface | Implemented |
| FormatPreview.razor | 273 | Format processor preview/testing | Implemented |

> Source: `src/RepoQL.Web/Design.md`

### Implemented Services

| Service | Purpose |
|---------|---------|
| RepoQlConnectionManager | Singleton gRPC client with connection state events |
| HostStatusService | Background service subscribing to real-time status stream |
| HostStatusStore | State container for snapshots, pipeline status, health events |
| StatsService | Queries for overview, media types, node kinds, annotations |
| OperationsService | Reindex, import, cancel operations |
| SqlExecutionService | Raw SQL execution |
| DocumentExplorerService | Document browsing and snippet retrieval |
| ExploreService | X-ray exploration |
| FormatPreviewService | Format processor preview |

### Implemented Shared Components

| Component | Purpose |
|-----------|---------|
| PipelineView.razor | Visualizes hot path and idle processing status |
| PipelineStatusBar.razor | Compact pipeline indicator |
| Sparkline.razor | Mini charts for metrics |
| SqlResultGrid.razor | Renders SQL query results |
| SnippetViewer.razor | Code snippet display |

## Observability Infrastructure

### OpenTelemetry Integration

> Source: `src/RepoQL.ConsoleApp/Program.cs:47-53`

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("RepoQL.*").AddAspNetCoreInstrumentation())
    .UseOtlpExporter();
```

ActivitySource instrumentation exists in:
- `OnnxEmbeddingProvider.cs` - Embedding generation
- `VssIndexManager.cs` - Vector search operations
- `IndexingEngine.cs` - Indexing pipeline
- `DuckDbDataStore.cs` - Database operations

### IndexingMetrics Class

> Source: `src/RepoQL.Contracts/Metrics/IndexingMetrics.cs`

| Metric Type | Count | Examples |
|-------------|-------|----------|
| Counters | 22 | FilesEnqueued, FilesErrored, DocumentsCreated, NodesExtracted, EmbedRequests |
| Histograms | 9 | StageDuration, HotPathDuration, DbWriteDuration, EpochDuration, EmbedDuration |
| Observable Gauges | ~15 | QueueDepth, QueueCapacity, WorkersActive, CatalogEntries, EpochCurrent |

### Diagnostics Provider

> Source: `src/RepoQL.Contracts/Diagnostics/IIndexingDiagnosticsProvider.cs`

```csharp
IndexingDiagnosticsSnapshot GetSnapshot();  // Status, Epoch, HotPathDepth, LastError
IReadOnlyList<QueuedItemInfo> GetQueuedItems();  // Uri, Stage, Status, MimeType
```

SQL-accessible via `indexing_diagnostics()` and `indexing_queue()` UDFs.

### Real-Time Status Streaming

> Source: `src/RepoQL.Protocol/Protos/repoql.proto`

`WatchStatus` gRPC method streams:
- `PipelineStatusEvent` - Stage busy/idle, queued counts
- `IndexingActivityEvent` - File change activity
- `HealthEvent` - Health state changes
- `StatsSnapshotEvent` - Stats updates

## Query Surface

### Three MCP Tools

> Source: `src/RepoQL.ConsoleApp/Tools/`

| Tool | Purpose | Key Parameters |
|------|---------|----------------|
| `explore` | Token-budgeted discovery | intent (Inventory/Locate/Inspect/Explain), keywords, scope, boost/penalize |
| `query` | Raw DuckDB SQL | sql, tokenBudget |
| `read` | Fetch by URI with progressive disclosure | uri (with fragments, globs, questions), tokenBudget |

### Hybrid Search Architecture

> Source: `src/RepoQL.Data.DuckDB/Schema/Macros/search.sql`

Three signals combined:
- BM25 lexical matching (15% default weight)
- Fuzzy subsequence matching (15%)
- Semantic embedding similarity (70%)

Results boosted/penalized via RE2 regex patterns.

### Primary Views

| View | Purpose |
|------|---------|
| Files | Document inventory with X-ray summaries |
| Types | Type declarations across languages |
| Functions | Callable entities with signatures |
| Annotations | Diagnostics and lint results |

## Gaps and Opportunities

### Telemetry Gaps

> Source: `docs/flows/indexing-failure-modes.md`

| Gap | Impact |
|-----|--------|
| Per-parser duration breakdown | Can't identify slow parsers |
| File I/O vs processing time | Can't distinguish disk from CPU |
| Embedding provider latency details | Can't identify API issues |
| Queue backpressure (wait time) | Can't see producer blocking |
| Worker health (total vs faulted) | Can't detect worker attrition |

### Missing UI Capabilities

| Capability | Current State | Opportunity |
|------------|---------------|-------------|
| Graph visualization | Not implemented | Rich edge/node model exists but no rendering |
| Multi-repo comparison | Possible via SQL only | No dedicated comparison workflow |
| Progressive readiness indicator | Binary (loading/ready) | Staged indicator showing which capabilities available |
| Confidence indicators | Not surfaced | "Did I find everything?" is key UX need |
| Historical metrics | Real-time only | No persistence or trend analysis |
| Monaco editor | Textarea only | Stretch goal per Design.md |
| Diff viewer | Not implemented | Stretch goal per Design.md |

### Synergies Vision (Unimplemented)

> Source: `docs/ideas/synergies/README.md:418-483`

8-phase plan for intelligent context selection:
1. Focused Snippets (use best_chunk_start/end)
2. Query Expansion (abbreviations dictionary)
3. SimHash Deduplication
4. Clustered Output
5. Budget Allocation
6. PPR Expansion
7. MMR Diversity
8. Spectral Modules

None currently implemented. UI should anticipate cluster-based presentation.

### Operations Tracking

> Source: `docs/designs/operations.md`

Partially implemented. Open questions include:
- "Should operations be exposed via MCP tools?"
- "Should completed operations be queryable via SQL?"

## Architecture Considerations

### Agent-First Design Tension

> Source: `docs/DesignEthos.md:9`

"Things that would be challenging for humans but easy for AI (e.g. AST, Grammars, complex SQL) are to be embraced"

**Implication**: Human-facing UI must bridge gap between SQL-native design and point-and-click expectations.

### Token Budget as Central Concept

All three MCP tools use token budgets to control output verbosity. UI should expose this as:
- Slider control
- Named presets (Light: 500, Medium: 2000, Deep: 5000)

### Scope Syntax Inconsistency

- `search()` uses SQL LIKE patterns (`file:///src/%`)
- `search_symbol()` and explore use glob patterns (`file:///src/**/*.cs`)

UI should clarify or auto-convert between syntaxes.

## Aspire Integration

> Source: `src/RepoQL.Orchestrator/`

.NET Aspire orchestrator for local development:
- Hosts RepoQL.ConsoleApp and RepoQL.Web
- Custom health check connecting via Unix socket
- "Reset .repoql and restart" command

## Comparison: Existing vs Potential

| Dimension | Existing Dashboard | Potential Enhancements |
|-----------|-------------------|------------------------|
| Pipeline visibility | Good (PipelineView, sparklines) | Add per-parser breakdown, embedding latency |
| Query testing | Good (QueryStudio, Explore) | Add query builder, saved query categories |
| Document browsing | Good (Explorer) | Add graph visualization, relationship view |
| Health monitoring | Good (health panel, events) | Add alerting, historical trends |
| Operations control | Good (reindex, import) | Add scheduled tasks, operation history |
| Search testing | Good (Explore page) | Add A/B comparison, confidence visualization |

## Gaps in This Research

| Topic | What couldn't be determined |
|-------|---------------------------|
| User feedback | No issue tracker analysis or user research found |
| Usage patterns | No telemetry data or analytics visible |
| Performance benchmarks | Mentioned but not found |
| Competitive analysis | No comparison to similar tools |
| Mobile/responsive | Current Blazor app desktop-focused |

## Source Inventory

| Source Type | Examples |
|-------------|----------|
| Code (📄) | RepoQL.Web/*, IndexingEngine.cs, search.sql |
| Docs (📚) | Design.md, Schema.md, indexing-failure-modes.md |
| Synthesis (🧠) | Cross-referenced findings from multiple sources |

---

*Research conducted via parallel exploration of: core architecture, query surface, indexing pipeline, existing observability patterns, and unexplored directions.*
