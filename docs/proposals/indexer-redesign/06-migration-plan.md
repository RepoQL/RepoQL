# Migration Plan

This document outlines the phased approach to implementing the indexer redesign.

## Overview

The implementation is broken into 4 phases, each deliverable independently:

1. **Core Infrastructure** - IndexItem, IdleDetector, batch_state table
2. **Node Embeddings** - Incremental embedding refresh with both levels
3. **Semantic Analysis** - Workspace management and cross-file analyzers
4. **Polish** - Optimization, metrics, additional analyzers

## Phase 1: Core Infrastructure

### Goals

- Introduce `IndexItem` flow object
- Add idle detection infrastructure
- Create `batch_state` table
- Refactor stages to use `IndexItem` (can be incremental)

### Tasks

#### 1.1 Add IndexItem Class

```csharp
// src/RepoQL.Core/IndexItem.cs
public sealed class IndexItem { ... }
```

**Testing:**
```csharp
[Test]
public void IndexItem_CreatesWithRequiredFields() {
    var item = new IndexItem { Uri = "file:///test.cs", Path = "test.cs" };
    Assert.Equal("file:///test.cs", item.Uri);
}
```

**Effort:** 1 day

#### 1.2 Add IdleDetector Component

```csharp
// src/RepoQL.Core/IdleDetector.cs
public sealed class IdleDetector { ... }
```

**Testing:**
```csharp
[Test]
public async Task IdleDetector_DetectsWhenQueuesEmpty() {
    var detector = new IdleDetector(indexer);
    var idle = await detector.WaitForIdleAsync(ct).WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(idle);
}
```

**Effort:** 2 days

#### 1.3 Create batch_state Table

```sql
-- src/RepoQL.Data.DuckDB/Schema/Tables/batch_state.sql
CREATE TABLE IF NOT EXISTS batch_state (...);
```

**Migration:**
```csharp
// Run on startup
await graphStore.ExecuteAsync(@"
    CREATE TABLE IF NOT EXISTS batch_state (...);
    INSERT INTO batch_state (name, last_run_at)
    VALUES ('embeddings', '1970-01-01'), ('semantic_analysis', '1970-01-01')
    ON CONFLICT DO NOTHING;
");
```

**Testing:**
```csharp
[Test]
public void BatchState_InitializesWithDefaults() {
    var lastRun = db.QuerySingle<DateTime>(
        "SELECT last_run_at FROM batch_state WHERE name='embeddings'");
    Assert.Equal(new DateTime(1970, 1, 1), lastRun);
}
```

**Effort:** 1 day

#### 1.4 Refactor Discovery Stage

```csharp
// Existing: different object per stage
var classifyItem = new ClassifyItem { Uri = uri, Path = path };

// New: single flow object
var item = new IndexItem { Uri = uri, Path = path };
item.Digest = ComputeDigest();
item.ProvisionalType = Classify();
```

**Strategy:** Incremental refactoring (can coexist with old approach)

**Testing:** Existing tests should pass with minimal changes

**Effort:** 3 days

#### 1.5 Refactor Parsing Stage

Similar to discovery - update to mutate `IndexItem` instead of creating new objects.

**Effort:** 3 days

#### 1.6 Refactor Writer Stage

```csharp
// New: set CommittedAt on IndexItem
item.CommittedAt = DateTimeOffset.UtcNow;
OnCommitted?.Invoke(item.Uri, item.CommittedAt.Value);
```

**Effort:** 2 days

#### 1.7 Refactor First-Pass Analysis

```csharp
// New: populate IndexItem.FirstPassAnnotations
await foreach (var result in analyzer.AnalyzeAsync(uri, ct)) {
    item.FirstPassAnnotations.Add(result);
}
```

**Effort:** 2 days

### Deliverables

- ✅ IndexItem class with tests
- ✅ IdleDetector with integration tests
- ✅ batch_state table with migration
- ✅ All stages refactored to use IndexItem
- ✅ Existing functionality unchanged (backward compatible)

### Success Criteria

- All existing tests pass
- New unit tests for IndexItem and IdleDetector
- Performance unchanged (hot path unaffected)

**Total Effort:** 14 days (3 weeks)

---

## Phase 2: Node Embeddings

### Goals

- Add `node_embedding` table
- Implement `EmbeddingBatch` component
- Wire up idle coordinator to trigger embedding batches
- Support both document and node-level embeddings

### Tasks

#### 2.1 Create node_embedding Table

```sql
-- src/RepoQL.Data.DuckDB/Schema/Tables/node_embedding.sql
CREATE TABLE IF NOT EXISTS node_embedding (...);
```

**Migration:** Run on startup, additive (no breaking changes)

**Effort:** 1 day

#### 2.2 Implement EmbeddingBatch

```csharp
// src/RepoQL.Core/EmbeddingBatch.cs
public sealed class EmbeddingBatch { ... }
```

**Testing:**
```csharp
[Test]
public async Task EmbeddingBatch_ProcessesChangedDocuments() { ... }

[Test]
public async Task EmbeddingBatch_ProcessesChangedNodes() { ... }

[Test]
public async Task EmbeddingBatch_UpdatesLastRunOnSuccess() { ... }
```

**Effort:** 4 days

#### 2.3 Add IdleWorkCoordinator

```csharp
// src/RepoQL.ConsoleApp/Host/IdleWorkCoordinator.cs
public sealed class IdleWorkCoordinator : BackgroundService { ... }
```

**Wiring:**
```csharp
services.AddSingleton<IdleDetector>();
services.AddSingleton<EmbeddingBatch>();
services.AddHostedService<IdleWorkCoordinator>();
```

**Effort:** 2 days

#### 2.4 Add Node Embedding Search Macros

```sql
-- src/RepoQL.Data.DuckDB/Schema/Tables/node_search.sql
CREATE OR REPLACE MACRO node_search(
    question VARCHAR,
    kinds VARCHAR[] := NULL,
    k INTEGER := 50
) AS TABLE (
    WITH query_vec AS (
        SELECT embed_text_json('Represent this sentence for searching relevant passages: ' || question) AS qvec
    )
    SELECT
        ne.node_id,
        ne.uri,
        ne.kind,
        ne.start_line,
        ne.end_line,
        cosine_similarity_json(q.qvec, ne.embedding) AS similarity
    FROM query_vec q
    CROSS JOIN node_embedding ne
    WHERE (kinds IS NULL OR ne.kind = ANY(kinds))
    ORDER BY similarity DESC
    LIMIT k
);
```

**Effort:** 1 day

#### 2.5 Integration Testing

```csharp
[Test]
public async Task EndToEnd_EmbedsNodesAfterIndexing() {
    // Index a C# file with methods
    await indexer.IndexFileAsync("Foo.cs", @"
        public class Foo {
            public void MethodOne() { }
            public void MethodTwo() { }
        }
    ");

    // Wait for idle
    await idleDetector.WaitForIdleAsync(ct);

    // Verify node embeddings created
    var embeddings = db.Query<NodeEmbedding>(
        "SELECT * FROM node_embedding WHERE kind='cs_method'");
    Assert.Equal(2, embeddings.Count());
}
```

**Effort:** 3 days

### Deliverables

- ✅ node_embedding table
- ✅ EmbeddingBatch component with tests
- ✅ IdleWorkCoordinator background service
- ✅ Node search macros
- ✅ End-to-end integration tests

### Success Criteria

- Embeddings refresh automatically on file changes
- Both document and node embeddings work
- `node_search()` macro returns relevant results
- Performance: < 1s for 100 nodes

**Total Effort:** 11 days (2.5 weeks)

---

## Phase 3: Semantic Analysis

### Goals

- Implement `IWorkspaceManager` and `SimpleWorkspaceManager`
- Add `CSharpWorkspaceSnapshot` with Roslyn integration
- Implement example analyzer (unused public symbols)
- Wire up `SemanticAnalysisBatch` to idle coordinator

### Tasks

#### 3.1 Define Abstractions

```csharp
// src/RepoQL.Contracts/Analysis/IWorkspaceManager.cs
public interface IWorkspaceManager { ... }

// src/RepoQL.Contracts/Analysis/WorkspaceSnapshot.cs
public abstract class WorkspaceSnapshot : IDisposable { ... }
```

**Effort:** 1 day

#### 3.2 Implement CSharpWorkspaceSnapshot

```csharp
// src/RepoQL.Formats.DotNet/Analysis/CSharpWorkspaceSnapshot.cs
public sealed class CSharpWorkspaceSnapshot : WorkspaceSnapshot { ... }
```

**Dependencies:** Add Microsoft.CodeAnalysis.* NuGet packages

**Testing:**
```csharp
[Test]
public void CSharpSnapshot_GetsDocumentByUri() { ... }

[Test]
public async Task CSharpSnapshot_FindsReferences() { ... }
```

**Effort:** 5 days

#### 3.3 Implement SimpleWorkspaceManager

```csharp
// src/RepoQL.Formats.DotNet/Analysis/SimpleWorkspaceManager.cs
public sealed class SimpleWorkspaceManager : IWorkspaceManager { ... }
```

**Features:**
- Load from .sln if present
- Fallback to .csproj discovery
- Final fallback to adhoc workspace

**Testing:**
```csharp
[Test]
public void WorkspaceManager_BuildsFromSolution() { ... }

[Test]
public void WorkspaceManager_FallsBackToAdhoc() { ... }
```

**Effort:** 4 days

#### 3.4 Implement UnusedPublicSymbolAnalyzer

```csharp
// src/RepoQL.Formats.DotNet/Analysis/UnusedPublicSymbolAnalyzer.cs
public class UnusedPublicSymbolAnalyzer { ... }
```

**Testing:**
```csharp
[Test]
public async Task Analyzer_FindsUnusedPublicMethod() { ... }

[Test]
public async Task Analyzer_IgnoresUsedPublicMethod() { ... }
```

**Effort:** 3 days

#### 3.5 Implement SemanticAnalysisBatch

```csharp
// src/RepoQL.Core/SemanticAnalysisBatch.cs
public sealed class SemanticAnalysisBatch { ... }
```

**Testing:**
```csharp
[Test]
public async Task SemanticBatch_ProcessesChangedFiles() { ... }

[Test]
public async Task SemanticBatch_BuildsWorkspace() { ... }
```

**Effort:** 3 days

#### 3.6 Wire Up to IdleWorkCoordinator

```csharp
// Update IdleWorkCoordinator to spawn semantic batch
var semanticTask = Task.Run(() => _semanticBatch.RunAsync(ct), ct);
var embeddingTask = Task.Run(() => _embeddingBatch.RunAsync(ct), ct);
await Task.WhenAll(semanticTask, embeddingTask);
```

**Effort:** 1 day

#### 3.7 Integration Testing

```csharp
[Test]
public async Task EndToEnd_SemanticAnalysisFindsUnusedSymbol() {
    // Index two files with cross-file reference
    await indexer.IndexFileAsync("IService.cs", "...");
    await indexer.IndexFileAsync("ServiceImpl.cs", "...");

    // Wait for idle
    await idleDetector.WaitForIdleAsync(ct);

    // Verify semantic analysis ran
    var annotations = db.Query<Annotation>(
        "SELECT * FROM annotation WHERE kind='unused_symbol'");
    Assert.NotEmpty(annotations);
}
```

**Effort:** 3 days

### Deliverables

- ✅ Workspace management abstractions
- ✅ CSharpWorkspaceSnapshot with Roslyn
- ✅ SimpleWorkspaceManager
- ✅ UnusedPublicSymbolAnalyzer
- ✅ SemanticAnalysisBatch component
- ✅ Full integration tests

### Success Criteria

- Workspace builds from .sln or .csproj
- Unused symbol analyzer works cross-file
- Semantic batch runs automatically on idle
- Performance: < 5s for 1000 files (with warm workspace)

**Total Effort:** 20 days (4 weeks)

---

## Phase 4: Polish

### Goals

- Add metrics and observability
- Optimize workspace caching
- Add more analyzers
- Tune batch sizes and configuration
- Documentation and examples

### Tasks

#### 4.1 Add Metrics

```csharp
// Add OpenTelemetry metrics
_metrics.IdleDetections.Add(1);
_metrics.EmbeddingBatchDuration.Record(duration.TotalMilliseconds);
_metrics.SemanticBatchDuration.Record(duration.TotalMilliseconds);
```

**Effort:** 2 days

#### 4.2 Implement CachingWorkspaceManager

```csharp
// src/RepoQL.Formats.DotNet/Analysis/CachingWorkspaceManager.cs
public sealed class CachingWorkspaceManager : IWorkspaceManager { ... }
```

**Features:**
- Cache workspace snapshots
- Incremental updates
- Memory limit enforcement
- Cache expiry

**Effort:** 4 days

#### 4.3 Add More Analyzers

**Examples:**
- Call graph edges
- Type hierarchy edges
- Breaking change detection
- Documentation coverage

**Effort:** 5 days (1 day per analyzer)

#### 4.4 Configuration Tuning

```bash
# Add environment variable support
REPOQL_IDLE_QUIET_WINDOW_MS
REPOQL_EMBED_BATCH_SIZE
REPOQL_SEMANTIC_BATCH_SIZE
REPOQL_WORKSPACE_CACHE_ENABLED
```

**Effort:** 1 day

#### 4.5 Documentation

- User guide for semantic search
- Developer guide for writing analyzers
- Configuration reference
- Performance tuning guide

**Effort:** 3 days

### Deliverables

- ✅ Full metrics and observability
- ✅ Workspace caching (optional, configurable)
- ✅ 5+ cross-file analyzers
- ✅ Comprehensive documentation
- ✅ Configuration options

### Success Criteria

- Metrics exported to OpenTelemetry
- Workspace caching reduces batch time by 50%
- Multiple analyzers enabled by default
- Complete user documentation

**Total Effort:** 15 days (3 weeks)

---

## Overall Timeline

| Phase | Effort | Cumulative |
|-------|--------|------------|
| 1. Core Infrastructure | 3 weeks | 3 weeks |
| 2. Node Embeddings | 2.5 weeks | 5.5 weeks |
| 3. Semantic Analysis | 4 weeks | 9.5 weeks |
| 4. Polish | 3 weeks | 12.5 weeks |

**Total:** ~3 months (12.5 weeks)

## Risk Mitigation

### Risk: Workspace Build Too Slow

**Mitigation:**
- Phase 3 includes performance testing
- Caching in Phase 4 addresses this
- Can disable semantic analysis for large repos

### Risk: Memory Usage Too High

**Mitigation:**
- Dispose snapshots immediately after analysis
- Add memory limits and monitoring
- Option to disable features per repo

### Risk: Breaking Changes to Hot Path

**Mitigation:**
- Phase 1 is backward compatible
- Comprehensive test coverage
- Feature flags for idle-time work

### Risk: Roslyn API Changes

**Mitigation:**
- Pin to stable Roslyn versions
- Abstract workspace management (can swap implementations)
- Unit tests for Roslyn integration

## Rollback Plan

Each phase is independently deployable and can be rolled back:

**Phase 1:** Remove IndexItem, revert to old object passing (no data loss)
**Phase 2:** Drop node_embedding table, disable IdleWorkCoordinator (embeddings stop updating)
**Phase 3:** Disable SemanticAnalysisBatch service (no semantic analysis)
**Phase 4:** Revert to SimpleWorkspaceManager (no caching)

## Testing Strategy

### Unit Tests
- Every new class has >80% code coverage
- Mock database and file system
- Fast execution (< 1s per test)

### Integration Tests
- End-to-end scenarios with real database
- Test idle detection and batch triggers
- Slower (< 10s per test)

### Performance Tests
- Measure hot path latency (no regression)
- Measure idle batch duration (targets met)
- Memory usage monitoring

### Regression Tests
- All existing tests must pass
- No breaking changes to public APIs
- Backward compatibility verified

## Feature Flags

Enable gradual rollout:

```bash
# Phase 2
REPOQL_NODE_EMBEDDINGS_ENABLED=1

# Phase 3
REPOQL_SEMANTIC_ANALYSIS_ENABLED=1

# Phase 4
REPOQL_WORKSPACE_CACHING_ENABLED=0  # Start disabled
```

## Success Metrics

### After Phase 1
- ✅ IndexItem flow object works
- ✅ Idle detection reliable
- ✅ No performance regression

### After Phase 2
- ✅ Node embeddings refresh automatically
- ✅ `node_search()` returns relevant results
- ✅ < 1s latency for 100 nodes

### After Phase 3
- ✅ Unused symbol detection works
- ✅ Workspace builds reliably
- ✅ < 5s for 1000 files

### After Phase 4
- ✅ All metrics exported
- ✅ Workspace caching improves perf 2x
- ✅ Complete documentation

## Post-Launch

### Monitoring

- Dashboard for idle batch metrics
- Alerts for batch failures
- Performance regression detection

### Future Enhancements

- Python workspace management
- Go workspace management
- Additional analyzers (security, complexity, duplication)
- Machine learning for code suggestions
- HNSW indexes (if needed for performance)
