# RepoQL.Indexing

**Status**: 🚧 Active Development - Redesign of indexing pipeline for testability and extensibility

---

## What This Is

RepoQL.Indexing is a **redesign** of the indexing pipeline currently in production (`RepoQL.Core/RepositoryIndexer`). The redesign focuses on:

1. **Testability** - Single flow object makes testing trivial
2. **Extensibility** - Support hundreds of format processors without complexity
3. **Safety** - Clear contracts and isolated stages prevent cascading failures
4. **Observability** - Full OpenTelemetry instrumentation built-in

The production system (`RepoQL.Core`) remains stable and active. This project will eventually replace it once fully tested.

---

## Core Philosophy

### The Single Flow Object Pattern

The entire redesign centers on one concept: **IndexItem flows through the pipeline, accumulating state**.

```csharp
// Discovery creates it
var item = new IndexItem(rawArtifact, options);

// Classification adds media type
item.MediaType = "text/markdown;kind=markdown.doc";

// Parsing adds records
item.Records = materializer.Materialize(document);

// Analysis adds annotations
item.Annotations.AddRange(lintResults);
```

**Why this matters:**
- ✅ **Testing**: Mock stages in isolation, inspect state at any point
- ✅ **Debugging**: Single object to inspect, complete history visible
- ✅ **Tracing**: One Activity spans entire pipeline, no context loss
- ✅ **Simplicity**: No hidden state, no complex coordination

### Contrast with Current System (RepoQL.Core)

| Aspect | Current (Core) | Redesign (Indexing) |
|--------|----------------|---------------------|
| **State management** | Scattered across queues/dictionaries | Single `IndexItem` object |
| **Testing** | Requires full pipeline setup | Stages test independently |
| **Processor isolation** | Shared state via closures | Clean interfaces with `next()` |
| **Error boundaries** | Queue-level try-catch | Per-processor error handling |
| **Trace continuity** | Manual ActivityContext tracking | Built into flow object |

---

## Architecture

### Pipeline Stages

**Important**: Files start with a **provisional media type** computed from their extension (e.g., `.md` → `text/markdown`). Classification refines this, parsing uses it, analysis validates it.

```
┌─────────────────────────────────────────────────────────────┐
│                     IndexItem Creation                       │
│         (from file system with provisional type)             │
│         e.g., "README.md" → text/markdown                    │
└────────────────────────┬────────────────────────────────────┘
                         ↓
┌─────────────────────────────────────────────────────────────┐
│  Stage 1: Classification                                     │
│  • Input:  IDiscoveredArtifact (file + provisional type)    │
│  • Output: SemanticMediaType?                                │
│  • Mutates: item.MediaType                                   │
│  • Purpose: Refine type, add 'kind' parameters               │
│             e.g., text/markdown → text/markdown;kind=doc     │
└────────────────────────┬────────────────────────────────────┘
                         ↓
┌─────────────────────────────────────────────────────────────┐
│  Stage 2: Parsing                                            │
│  • Input:  IClassifiedArtifact (file + type)                │
│  • Output: Records? (graph structure)                        │
│  • Mutates: item.Records                                     │
│  • Purpose: Materialize file into graph                      │
└────────────────────────┬────────────────────────────────────┘
                         ↓
┌─────────────────────────────────────────────────────────────┐
│  Stage 3: Single-File Analysis                               │
│  • Input:  IParsedArtifact (file + graph)                   │
│  • Output: Annotation[] (lint, metrics)                      │
│  • Mutates: item.Annotations                                 │
│  • Purpose: Single-file validation/analysis                  │
└────────────────────────┬────────────────────────────────────┘
                         ↓
┌─────────────────────────────────────────────────────────────┐
│  Stage 4: Multi-File Analysis (deferred)                     │
│  • Input:  IAnnotatedArtifact (complete item)               │
│  • Output: Annotation[]                                      │
│  • Mutates: item.Annotations                                 │
│  • Purpose: Cross-file semantic analysis                     │
└────────────────────────┬────────────────────────────────────┘
                         ↓
┌─────────────────────────────────────────────────────────────┐
│  Stage 5: Index Rebuild (deferred)                           │
│  • Input:  IAnnotatedArtifact                               │
│  • Output: string (status)                                   │
│  • Purpose: Trigger rebuilds, embeddings                     │
└─────────────────────────────────────────────────────────────┘
```

### Document Catalog (current work)

- **Purpose**: keep an in-memory map of committed documents (URI, digest, semantic media type, physical location) so `OnlyIfStale` decisions are O(1).
- **Startup**: hydrates once via an injected `IDocumentCatalogDataSource` before any items enter the indexing queue.
- **Hot path**: Stage 1 hashes each artifact, checks the catalog, and skips unchanged files when requested by `IndexItemOptions`.
- **Updates**: the catalog tracks in-flight digests today; when the writer wiring lands, `WriteOperation.OnCommitted` will call `DocumentCatalog.ApplyUpsert` / `ApplyDelete` to keep it authoritative.
- **Later**: snapshotting the catalog to disk is deferred until the new engine owns persistence end-to-end.

### Committer

- **Goal**: isolate “persist records + annotations + catalog update” into a small service (`IIndexingCommitter`) so the engine can stay focused on orchestration.
- **Responsibilities**:
  - Translate a fully populated `IndexItem` into the right `WriteOperation` set and hand them to `IDatabaseWriter`.
  - Update `DocumentCatalog` inside the writer’s `OnCommitted` callback so catalog state stays consistent with the DB.
  - Forward analyzer output to `IAnalysisResultWriter` once document IDs are stable.
- **Why separate**: keeps the engine lean, makes persistence logic testable on its own, and lets us swap commit strategies (batching, dry-run, alternate writers) through DI later.

### The Flow Interfaces

Each stage sees progressively more complete data:

```csharp
IDiscoveredArtifact      // Just file info (size, path, URI)
  ↓
IClassifiedArtifact      // + MediaType
  ↓
IParsedArtifact          // + Records (graph structure)
  ↓
IAnnotatedArtifact       // + Annotations
```

**Why interfaces matter**: Stages can only access data from previous stages. This prevents temporal coupling bugs.

---

## Core Contracts

### IAsyncPipeline&lt;TInput, TResult&gt;

The processor interface that all pipeline processors implement.

```csharp
public interface IAsyncPipeline<TInput, TResult>
    where TInput : IDiscoveredArtifact
{
    Task<(TResult? Result, PipelineResult PipelineStatus)> ProcessAsync(
        TInput item,
        CallNextPipeline<TInput, TResult> next,
        CancellationToken token
    );
}
```

**Pattern**: Middleware / Chain of Responsibility

#### Example: Classification Processor

```csharp
public class MarkdownClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        // Check provisional media type (already computed from .md extension)
        var provisionalType = item.RawArtifact.ProvisionalMediaType.Value;
        if (provisionalType?.Type != "text/markdown")
            return next(item); // Pass to next processor

        // Refine by adding 'kind' parameter to distinguish document vs fragment
        var mediaType = SemanticMediaType.Parse("text/markdown;kind=markdown.doc");

        // Short-circuit: don't call next, we're done
        return Task.FromResult<(SemanticMediaType?, PipelineResult)>((mediaType, PipelineResult.Success));
    }
}
```

**Key Points:**
- ✅ Call `next(item)` to continue the chain
- ✅ Return without calling `next` to short-circuit
- ✅ Return `PipelineResult.Filtered` to skip item entirely
- ✅ Return `PipelineResult.Error` to log error but continue pipeline

---

## Invariants

These rules ensure system correctness. **Most are enforced by the type system** - you'll get compilation errors if you violate them. The remaining rules are architectural constraints you must respect.

### 1. IndexItem is Append-Only During Pipeline (Enforced by Type System)

✅ **Type system prevents overwriting fields from previous stages**

```csharp
// ✅ Processors receive read-only interfaces
public async Task ProcessAsync(IClassifiedArtifact item, ...)
{
    // item.MediaType is read-only - compilation error if you try to set it
    var descriptor = registry.ResolveByMedia(item.MediaType);
}

// ❌ Cannot compile - MediaType is { get; } only on interface
// item.MediaType = "something-different";
```

**Why enforced**: Interfaces expose `{ get; }` only properties. Processors physically cannot mutate previous stage results.

**Exception**: The dictionary (`item[key]`) is intentionally mutable for the "Bag" pattern - stage-specific scratchpad data.

### 2. Stages are Pure Functions of Input

❌ **Never access database or external state in processors**

```csharp
// ❌ BAD: Processor queries database
public async Task ProcessAsync(...)
{
    var existingDoc = await _db.QueryAsync("SELECT ...");
    // ...
}

// ✅ GOOD: Processor only reads from item
public async Task ProcessAsync(IClassifiedArtifact item, ...)
{
    using var stream = item.CreateReadStream();
    var content = await ReadAsync(stream);
    // ...
}
```

**Why**: Processors become untestable. Cannot run in isolation. Performance degrades.

**Exception**: Analysis stage may query database via `IAnalysisContext` (explicitly designed for this).

### 3. PipelinePhase Orchestrates, Processors Transform

❌ **Never put orchestration logic in processors**

```csharp
// ❌ BAD: Processor manages queue
public async Task ProcessAsync(...)
{
    var result = ComputeResult(item);
    await _nextQueue.EnqueueAsync(result); // NO
    return (result, PipelineResult.Success);
}

// ✅ GOOD: Processor returns result, phase handles queueing
public async Task ProcessAsync(...)
{
    var result = ComputeResult(item);
    return (result, PipelineResult.Success);
}
```

**Why**: Violates separation of concerns. PipelinePhase owns flow control.

### 4. Single-Threaded Writer (Database) (Enforced by Architecture)

✅ **Architecture enforces single-threaded writes**

```csharp
// ✅ Only one WorkQueue for writer, with 1 worker
Writer = new WorkQueue<IndexItem>("Writer", capacity: 1000, workers: 1, ...);

// Database writes happen serially - architecture enforces this
// Processors never have direct database access
```

**Why enforced**: Single WorkQueue with 1 worker = serial execution. Processors don't have database access.

**Note**: This is an architectural invariant, not a rule to remember.

### 5. Errors Isolate to Single Item

❌ **Never let processor exception stop the pipeline**

```csharp
// ❌ BAD: Uncaught exception kills worker
public async Task ProcessAsync(...)
{
    var result = DangerousOperation(); // May throw
    return (result, PipelineResult.Success);
}

// ✅ GOOD: Catch exceptions, return error status
public async Task ProcessAsync(...)
{
    try
    {
        var result = DangerousOperation();
        return (result, PipelineResult.Success);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to process {Uri}", item.Uri);
        return (null, PipelineResult.Error);
    }
}
```

**Why**: One malformed file should not block thousands of others.

---

## Testing Guide

### Testing Philosophy

**Tests are documentation**. When a test fails in CI, the failure message should explain:
1. What broke
2. Why the expectation exists
3. What to check if you're changing behavior

### Test Structure for Processors

Every processor should have this test structure:

```csharp
public class MarkdownClassifierTests
{
    [Test]
    [DisplayName("Classifies .md files as markdown documents")]
    public async Task Given_MarkdownExtension_When_Classify_Then_Returns_MarkdownMediaType()
    {
        // Arrange
        var classifier = new MarkdownClassifier();
        var item = CreateTestItem("README.md");

        // Act
        var (result, status) = await classifier.ProcessAsync(
            item,
            _ => Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success)),
            CancellationToken.None
        );

        // Assert
        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result.Value.Type.Should().Be("text/markdown");
        result.Value.GetParameter("kind").Should().Be("markdown.doc");
    }

    [Test]
    [DisplayName("Passes non-markdown files to next processor")]
    public async Task Given_NonMarkdownExtension_When_Classify_Then_CallsNext()
    {
        // Arrange
        var classifier = new MarkdownClassifier();
        var item = CreateTestItem("Program.cs");
        var nextCalled = false;

        // Act
        await classifier.ProcessAsync(
            item,
            _ => {
                nextCalled = true;
                return Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None
        );

        // Assert
        nextCalled.Should().BeTrue("classifier should delegate to next processor for non-markdown files");
    }

    [Test]
    [DisplayName("Handles read errors gracefully")]
    public async Task Given_UnreadableFile_When_Classify_Then_Returns_Error()
    {
        // Arrange
        var classifier = new MarkdownClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.CreateReadStream()).Throws<IOException>();
        A.CallTo(() => item.Name).Returns("test.md");

        // Act
        var (result, status) = await classifier.ProcessAsync(
            item,
            _ => Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success)),
            CancellationToken.None
        );

        // Assert
        status.Should().Be(PipelineResult.Error);
        result.Should().BeNull();
    }
}
```

### Key Testing Patterns

#### 1. Test Processors in Isolation

Don't create full pipeline. Test each processor independently.

```csharp
// ✅ GOOD: Isolated processor test
var processor = new MyProcessor();
var result = await processor.ProcessAsync(item, next, ct);

// ❌ BAD: Requires full pipeline
var engine = new IndexingEngine(...); // 10+ dependencies
await engine.EnqueueItemAsync(...);
```

#### 2. Use Fakes for Interfaces

```csharp
using FakeItEasy;

var item = A.Fake<IClassifiedArtifact>();
A.CallTo(() => item.MediaType).Returns(SemanticMediaType.Parse("text/markdown"));
A.CallTo(() => item.CreateReadStream()).Returns(new MemoryStream(content));
```

#### 3. Assert on Both Result and Status

```csharp
var (result, status) = await processor.ProcessAsync(...);

// Always check both
status.Should().Be(PipelineResult.Success);
result.Should().NotBeNull();
```

#### 4. Test the `next()` Callback

```csharp
var nextCalled = false;
CallNextPipeline<TInput, TResult> next = async item =>
{
    nextCalled = true;
    return (expectedResult, PipelineResult.Success);
};

await processor.ProcessAsync(item, next, ct);

nextCalled.Should().BeTrue("processor should call next for unhandled files");
```

### Test Coverage Requirements

- **Minimum**: 80% line coverage for publication
- **Ideal**: 90%+ for core pipeline components
- **Required scenarios**:
  - ✅ Happy path (successful processing)
  - ✅ Unhandled file (calls `next`)
  - ✅ Error handling (returns `PipelineResult.Error`)
  - ✅ Cancellation (respects `CancellationToken`)

---

## Agent Safety Guidelines

### ✅ Safe to Modify (Green Zone)

Agents can freely create/modify these without risk:

- **Processors**: Create new `IAsyncPipeline<TInput, TResult>` implementations
- **Tests**: Add/modify tests for processors
- **Documentation**: Update README, add examples
- **Analyzers**: Implement new `IAnalyzer` implementations (future)

### ⚠️ Modify with Caution (Yellow Zone)

Requires understanding invariants:

- **IndexItem properties**: Only add fields, never remove/rename
- **Pipeline registration**: Add processors to collections, don't reorder without reason
- **Error handling**: Modify logging but preserve error isolation

### 🚫 Do Not Modify (Red Zone)

Core infrastructure - breaking changes require human approval:

- **PipelinePhase<TInput, TResult>**: Orchestration logic
- **WorkQueue<T>**: Concurrency primitive
- **IndexingEngine**: Core orchestrator
- **Interface contracts**: `IAsyncPipeline`, `IDiscoveredArtifact`, etc.
- **Database writer**: Single-threaded constraint

**Why these are protected**: Changes here affect ALL processors. Bugs cascade. Hard to test.

---

## Common Patterns

### Creating a New Processor

1. **Determine which stage** (Classification, Parsing, Analysis)
2. **Implement the interface**
3. **Register with DI** (future: auto-registration)
4. **Write tests** (3+ tests minimum)

**Example**: Adding a YAML classifier

```csharp
// 1. Implement interface
public class YamlClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public async Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        if (!item.Name.EndsWith(".yaml") && !item.Name.EndsWith(".yml"))
            return await next(item);

        return (SemanticMediaType.Parse("application/yaml"), PipelineResult.Success);
    }
}

// 2. Register (in IndexingEngine constructor)
var classificationProcessors = new List<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>>
{
    new MarkdownClassifier(),
    new YamlClassifier(),  // Add here
    // ...
};
```

### Using the IndexItem Bag

`IDiscoveredArtifact` inherits `IDictionary<string, object>` for stage-specific scratchpad data.

**When to use**:
- ✅ Temporary data within a stage (e.g., parsed frontmatter for formatters)
- ✅ Optional metadata that doesn't warrant a property
- ✅ Extension points for custom processors

**When NOT to use**:
- ❌ Core pipeline data (use properties: MediaType, Records, etc.)
- ❌ Cross-stage communication (processors in different stages can't rely on bag contents)

**Pattern**:
```csharp
// Store in one processor (classification stage)
item["markdown.frontmatter"] = frontmatter;

// Retrieve in another processor (same stage)
if (item.TryGet<Dictionary<string, object>>("markdown.frontmatter", out var fm))
{
    // Use frontmatter
}
```

**Namespacing Rule**: Bag keys should be namespaced (e.g., `"markdown.frontmatter"`, not `"frontmatter"`) to avoid collisions.

**Why mutable**: Dictionary is intentionally mutable despite other properties being read-only. This is the ONE exception to immutability.

---

## Migration Status

| Component | Status | Notes |
|-----------|--------|-------|
| IndexItem | ✅ Implemented | Single flow object |
| PipelinePhase | ✅ Implemented | Base orchestrator |
| WorkQueue | ✅ Implemented | Concurrent queue primitive |
| IndexingEngine | 🚧 In Progress | TODO: Database writer integration |
| Classification | 🚧 In Progress | Needs processors |
| Parsing | 🚧 In Progress | Needs processors |
| Analysis | 📝 Planned | Waiting on pipeline completion |
| Tests | 📝 Planned | Will match existing coverage in Core |

**Current Focus**: Implementing processors for Classification and Parsing stages.

---

## FAQ

### Why not just fix RepoQL.Core?

The current system has fundamental testability issues:
- State scattered across closures and dictionaries
- Processors share mutable state
- Testing requires spinning up full pipeline
- Difficult to trace execution path

The redesign fixes these architecturally rather than incrementally.

### Will this break existing queries?

No. Database schema and query APIs remain identical. Only indexing pipeline changes.

### When does this replace RepoQL.Core?

When:
1. ✅ All format processors migrated
2. ✅ Test coverage ≥80%
3. ✅ Performance benchmarks meet/exceed Core
4. ✅ 1 week of production testing with Core as fallback

---

## Contributing

**For Agents**:
1. Read this entire README (yes, really)
2. Start with processors (Green Zone)
3. Write tests first (TDD)
4. Check invariants before committing

**For Humans**:
1. Discuss breaking changes in issues first
2. Update docs with code changes
3. Run full test suite (`dotnet test`)
4. Ensure coverage doesn't drop

---

## References

- [Indexing Process (Current System)](../../docs/IndexingProcess.md)
- [Redesign Proposal](../../docs/proposals/indexer-redesign/README.md)
- [Testing Guidelines](../../docs/knowledge/testing-guidelines.md)
- [Pipeline Architecture](../../docs/proposals/indexer-redesign/02-pipeline-architecture.md)
