# How README.md Flows Through The System

Complete trace of one file through the entire indexing pipeline. Concrete code paths and state changes.

---

## Initial State

File exists on disk: `C:\Source\MyRepo\README.md`

Content:
```markdown
# MyProject

Introduction to the project.

## Installation

See [docs/setup.md](docs/setup.md) for details.
```

File watcher detects change. RepoqlHost creates RawArtifact.

---

## Phase 1: Enqueue

**Code path**: RepoqlHost → IndexingCoordinator → IndexingEngine

```csharp
// RepoqlHost.cs
var file = fileSystem.GetFile("README.md");
var rawArtifact = new RawArtifact(file, fileSystem);
await coordinator.EnqueueAsync(rawArtifact, IndexItemOptions.OnlyIfStale);

// IndexingEngine.EnqueueItemAsync
var indexItem = new IndexItem(rawArtifact, options);
var epoch = _epochTracker.CurrentEpoch;  // e.g., 42
indexItem.SetEpoch(epoch);
_epochTracker.Increment(epoch);  // Track pending item in epoch 42
await IndexerQueue.EnqueueAsync(indexItem, cancellationToken);
```

**State after enqueue**:
```csharp
IndexItem {
    RawArtifact: {
        Uri: file:///README.md,
        Name: "README.md",
        Digest: AsyncLazy<byte[]> (not yet computed),
        ProvisionalMediaType: Lazy<"text/markdown.doc"> (from .md extension)
    },
    Epoch: 42,
    Status: null,  // Not terminal
    MediaType: null,  // Not yet classified
    Records: null,  // Not yet parsed
    AnnotationsList: []
}
```

**EpochTracker state**:
```
Epoch 42: 1 pending item
```

---

## Phase 2: Index Item Processing

**Code path**: WorkQueue pulls item → IndexingEngine.IndexItemAsync

### 2.1 Filter Check

```csharp
// IndexingEngine.IndexItemAsync:208
if (item.Options.HasFlag(IndexItemOptions.OnlyIfNotExcluded) &&
    !Filter.IncludeFile(item.Uri)) {
    RecordResult(PipelineResult.Filtered);
    return;
}
```

README.md not in .gitignore → passes filter.

### 2.2 Digest Computation & Catalog Check

```csharp
// IndexingEngine.IndexItemAsync:214
await DocumentCatalog.EnsureInitializedAsync(cancellationToken);
var digestBytes = await item.RawArtifact.Digest.WithCancellation(cancellationToken);
// Computes xxHash64: [0x3A, 0x9F, 0x... ]

var digestHex = Convert.ToHexString(digestBytes);
// "3A9F7B2C..."

item.DigestHex = digestHex;

var evaluation = DocumentCatalog.Evaluate(item.Uri, digestHex);
// Returns: Unknown (file never indexed)
// Or: Reindex (digest differs)
// Or: SkipUpToDate (digest matches)
```

**Assume**: File is new → evaluation = `Unknown`.

```csharp
// IndexingEngine.IndexItemAsync:222
if (item.Options.HasFlag(IndexItemOptions.OnlyIfStale) &&
    evaluation.Decision == DocumentCatalogDecision.SkipUpToDate) {
    RecordResult(PipelineResult.Filtered);
    return;  // Early exit for unchanged files
}
```

File is new → continue.

### 2.3 Register Processing

```csharp
// IndexingEngine.IndexItemAsync:233
DocumentCatalog.BeginProcessing(item.Uri, digestHex);
// Adds to _pendingDigests: {"file:///README.md" => "3A9F7B2C..."}
// Prevents duplicate work if same file queued again
```

---

## Phase 3: Classification

**Code path**: IndexingEngine.ApplyIndexerPipeline → StageContext.RunAsync → ClassificationPipeline.ProcessItemAsync

```csharp
// IndexingEngine.ApplyIndexerPipeline:318
var result = await _classificationStage.RunAsync(item, cancellationToken, UpdateState);

// Inside StageContext.RunAsync:32
UpdateState(IndexingState.ClassificationBusy, IndexingState.ClassificationIdle, entering: true);
// Sets: ClassificationBusy = true, ClassificationIdle = false, Started = true

try {
    // ClassificationPipeline.ProcessItemAsync
    foreach (var processor in _processors) {
        var result = await processor.ProcessAsync(item, ct);
        if (result != null) {
            item.MediaType = result;
            return PipelineResult.Success;
        }
    }
    return PipelineResult.Filtered;
} finally {
    UpdateState(IndexingState.ClassificationBusy, IndexingState.ClassificationIdle, entering: false);
    // Sets: ClassificationBusy = false, ClassificationIdle = true
}
```

**MarkdownClassificationProcessor runs**:
```csharp
public Task<SemanticMediaType?> ProcessAsync(IDiscoveredArtifact artifact, CancellationToken ct) {
    if (artifact.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        return Task.FromResult(SemanticMediaType.Parse("text/markdown.doc"));
    return Task.FromResult<SemanticMediaType?>(null);
}
```

**State after classification**:
```csharp
IndexItem {
    MediaType: "text/markdown.doc",  // ✓ Set!
    Records: null,  // Still null
    ...
}
```

**IndexingState**: `ClassificationIdle` set, `ClassificationBusy` cleared.

---

## Phase 4: Parsing

**Code path**: StageContext.RunAsync → ParsingPipeline.ProcessItemAsync

```csharp
// UpdateState sets: ParsingBusy = true, ParsingIdle = false

// ParsingPipeline.ProcessItemAsync
foreach (var processor in _processors) {
    var result = await processor.ProcessAsync(item, ct);
    if (result != null) {
        item.Records = result;
        return PipelineResult.Success;
    }
}
```

**MarkdownParsingProcessor runs**:
```csharp
public async Task<Records?> ProcessAsync(IClassifiedArtifact artifact, CancellationToken ct) {
    if (artifact.MediaType?.BaseType != "text/markdown.doc")
        return null;

    // Load document
    var document = await _loader.LoadAsync(artifact.RawArtifact, ct);

    // Materialize to graph
    var records = _materializer.Materialize(document);
    return records;
}
```

**Markdown parser output**:
```csharp
Records {
    Artifacts: [
        {
            Id: <guid-1>,
            Digest: "3A9F7B2C...",
            MediaType: "text/markdown.doc",
            Content: <file bytes>,
            Headline: "README.md | markdown.doc | 245 bytes | 10 lines",
            Summary: "# MyProject\n## Installation",
            Structure: "- MyProject (h1)\n  - Installation (h2)"
        }
    ],
    Nodes: [
        {
            Id: <guid-2>,
            Kind: "document",
            Uri: "file:///README.md",
            ArtifactId: <guid-1>,
            SpanId: null,
            Properties: {}
        },
        {
            Id: <guid-3>,
            Kind: "md_heading",
            Uri: null,
            ArtifactId: <guid-1>,
            SpanId: <span-1>,
            Properties: { "text": "MyProject", "level": 1 }
        },
        {
            Id: <guid-4>,
            Kind: "md_heading",
            Uri: null,
            ArtifactId: <guid-1>,
            SpanId: <span-2>,
            Properties: { "text": "Installation", "level": 2 }
        },
        {
            Id: <guid-5>,
            Kind: "md_link",
            Uri: null,
            ArtifactId: <guid-1>,
            SpanId: <span-3>,
            Properties: { "text": "docs/setup.md", "url": "docs/setup.md" }
        }
    ],
    Spans: [
        { Id: <span-1>, DocumentId: <guid-2>, StartLine: 1, EndLine: 1, StartByte: 0, EndByte: 12 },
        { Id: <span-2>, DocumentId: <guid-2>, StartLine: 5, EndLine: 5, StartByte: 40, EndByte: 56 },
        { Id: <span-3>, DocumentId: <guid-2>, StartLine: 7, EndLine: 7, StartByte: 62, EndByte: 94 }
    ],
    Edges: [
        {
            Id: <edge-1>,
            SourceNodeId: <guid-2>,  // document
            DestinationNodeId: <guid-3>,  // h1 heading
            Type: "HAS_PART",
            IsComposition: true,
            Ordinal: 0
        },
        {
            Id: <edge-2>,
            SourceNodeId: <guid-2>,  // document
            DestinationNodeId: <guid-4>,  // h2 heading
            Type: "HAS_PART",
            IsComposition: true,
            Ordinal: 1
        }
    ],
    Annotations: []
}
```

**State after parsing**:
```csharp
IndexItem {
    MediaType: "text/markdown.doc",
    Records: { ... },  // ✓ Populated!
    ...
}
```

**IndexingState**: `ParsingIdle` set, `ParsingBusy` cleared.

---

## Phase 5: Single-File Analysis

**Code path**: StageContext.RunAsync → SingleFileAnalysisPipeline.ProcessItemAsync

```csharp
// UpdateState sets: SingleFileAnalysisBusy = true, SingleFileAnalysisIdle = false

// SingleFileAnalysisPipeline.ProcessItemAsync
foreach (var processor in _processors) {
    await processor.ProcessAsync(item, ct);
}
return PipelineResult.Success;
```

**MarkdownLinkAnalyzer runs**:
```csharp
public async Task<PipelineResult> ProcessAsync(IAnnotatedArtifact artifact, CancellationToken ct) {
    foreach (var link in document.Links) {
        var targetUri = ResolveRelative(artifact.Uri, link.Url);
        var targetFile = _fileSystem.GetFile(targetUri);

        if (!targetFile.Exists) {
            artifact.AnnotationsList.Add(new Annotation {
                Kind: "lint",
                Severity: "warning",
                Source: "MarkdownLinkAnalyzer",
                Message: $"Broken link: {link.Url}",
                TargetNodeId: link.NodeId,
                TargetSpanId: link.SpanId
            });
        }
    }
    return PipelineResult.Success;
}
```

**Result**: Link to `docs/setup.md` checked. File doesn't exist → annotation added.

**State after analysis**:
```csharp
IndexItem {
    MediaType: "text/markdown.doc",
    Records: { ... },
    AnnotationsList: [
        {
            Kind: "lint",
            Severity: "warning",
            Source: "MarkdownLinkAnalyzer",
            Message: "Broken link: docs/setup.md",
            TargetNodeId: <guid-5>,
            TargetSpanId: <span-3>
        }
    ]
}
```

**IndexingState**: `SingleFileAnalysisIdle` set, `SingleFileAnalysisBusy` cleared.

---

## Phase 6: Commit

**Code path**: IndexingEngine.IndexItemAsync:240 → IndexingCommitter.CommitAsync

```csharp
// IndexingCommitter.CommitAsync:22
// Validation
if (item.Records is null) {
    _logger.LogWarning("Skipping commit for {Uri} because no records were produced.", item.Uri);
    return;
}

var mediaType = item.MediaType ?? item.RawArtifact.ProvisionalMediaType.Value;
var documentNode = item.Records.Nodes.FirstOrDefault(n =>
    string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));

// Combine annotations
var commitRecords = new Records {
    Artifacts = item.Records.Artifacts,
    Nodes = item.Records.Nodes,
    Spans = item.Records.Spans,
    Edges = item.Records.Edges,
    Annotations = [
        ...item.Records.Annotations,  // From parser (none in this case)
        ...item.AnnotationsList        // From analyzers (broken link warning)
    ],
    AnnotationSources = item.Records.AnnotationSources
};

// Create write operation
var operation = new WriteOperation {
    Id: Guid.NewGuid(),
    Type: WriteOperationType.ReplaceDocument,
    Uri: item.Uri,
    ParsedData: commitRecords,
    ParentContext: Activity.Current?.Context,
    OnCommitted: (_, result) => {
        if (!result.Success)
            return Task.CompletedTask;

        var entry = new DocumentCatalogEntry(
            item.Uri,
            digestHex: "3A9F7B2C...",
            mediaType,
            item.RawArtifact.PhysicalPath,
            item.LastModified
        );
        _catalog.ApplyUpsert(entry);
        return Task.CompletedTask;
    }
};

// Enqueue to writer
var commitResult = await _writer.EnqueueAndWaitAsync(operation, cancellationToken);

if (!commitResult.Success)
    throw new InvalidOperationException($"Database commit failed for {item.Uri}.", commitResult.Error);
```

**DatabaseWriter executes** (single-threaded):

```sql
-- 1. Upsert artifact
INSERT INTO artifact (id, digest, media_type, text_content, headline, summary, structure)
VALUES (
    '<guid-1>',
    '3A9F7B2C...',
    'text/markdown.doc',
    '# MyProject\n\nIntroduction...',
    'README.md | markdown.doc | 245 bytes | 10 lines',
    '# MyProject\n## Installation',
    '- MyProject (h1)\n  - Installation (h2)'
)
ON CONFLICT (digest) DO UPDATE SET ...;

-- 2. Upsert document node (by URI)
INSERT INTO node (id, kind, uri, artifact_id, span_id, properties)
VALUES ('<guid-2>', 'document', 'file:///README.md', '<guid-1>', NULL, '{}')
ON CONFLICT (uri) WHERE kind = 'document' DO UPDATE SET ...;

-- 3. Delete old child nodes
DELETE FROM node WHERE document_id = '<guid-2>' AND kind != 'document';

-- 4. Insert new child nodes
INSERT INTO node (id, kind, uri, artifact_id, span_id, properties, document_id)
VALUES
  ('<guid-3>', 'md_heading', NULL, '<guid-1>', '<span-1>', '{"text":"MyProject","level":1}', '<guid-2>'),
  ('<guid-4>', 'md_heading', NULL, '<guid-1>', '<span-2>', '{"text":"Installation","level":2}', '<guid-2>'),
  ('<guid-5>', 'md_link', NULL, '<guid-1>', '<span-3>', '{"text":"docs/setup.md","url":"docs/setup.md"}', '<guid-2>');

-- 5. Insert spans
INSERT INTO span (id, document_id, start_line, end_line, start_byte, end_byte)
VALUES
  ('<span-1>', '<guid-2>', 1, 1, 0, 12),
  ('<span-2>', '<guid-2>', 5, 5, 40, 56),
  ('<span-3>', '<guid-2>', 7, 7, 62, 94);

-- 6. Insert edges
INSERT INTO edge (id, source_node_id, destination_node_id, type, is_composition, ordinal)
VALUES
  ('<edge-1>', '<guid-2>', '<guid-3>', 'HAS_PART', true, 0),
  ('<edge-2>', '<guid-2>', '<guid-4>', 'HAS_PART', true, 1);

-- 7. Insert annotations
INSERT INTO annotation (id, kind, severity, source, message, target_node_id, span_id, scope_document_id)
VALUES ('<ann-1>', 'lint', 'warning', 'MarkdownLinkAnalyzer', 'Broken link: docs/setup.md', '<guid-5>', '<span-3>', '<guid-2>');
```

**OnCommitted fires**:
```csharp
catalog.ApplyUpsert(new DocumentCatalogEntry(
    Uri: "file:///README.md",
    Digest: "3A9F7B2C...",
    MediaType: "text/markdown.doc",
    PhysicalPath: "C:\\Source\\MyRepo\\README.md",
    LastModified: DateTime(2025, 1, 13, ...)
));
```

**Catalog state after commit**:
```
_entries["file:///README.md"] = DocumentCatalogEntry { Digest: "3A9F7B2C..." }
_pendingDigests.Remove("file:///README.md")
```

---

## Phase 7: Schedule for Idle Processing

```csharp
// IndexingEngine.IndexItemAsync:241
ScheduleAnalysis(item);

// Inside ScheduleAnalysis:275
lock (_analysisLock) {
    if (!_pendingAnalysis.TryGetValue(item.Epoch, out var backlog)) {
        backlog = new Queue<IndexItem>();
        _pendingAnalysis[item.Epoch] = backlog;
    }
    backlog.Enqueue(item);
}
```

**State**:
```
_pendingAnalysis[42] = Queue { README.md item }
```

---

## Phase 8: Epoch Completion

```csharp
// IndexingEngine.IndexItemAsync:263 (finally block)
finally {
    var epochBecameIdle = _epochTracker.Decrement(item.Epoch);
    // Epoch 42: 1 pending → 0 pending = became idle

    if (epochBecameIdle && State == IndexingState.AllIdle) {
        HotPathIdle?.Invoke(this, new HotPathIdleEventArgs(item.Epoch));
        // Fires HotPathIdle(epoch: 42)
    }
}
```

**Conditions met**:
- Epoch 42 has 0 pending items (last item completed)
- IndexingState == AllIdle (no busy flags set)

**Event fires**: `HotPathIdle(epoch: 42)`

**Event handler**:
```csharp
// IndexingEngine constructor:163
HotPathIdle += OnHotPathIdle;

private void OnHotPathIdle(object? sender, HotPathIdleEventArgs args) {
    EnqueueIdleEpoch(args.Epoch);
}

internal void EnqueueIdleEpoch(long epoch) {
    _analysisEpochChannel.Writer.TryWrite(epoch);
    // Writes 42 to channel
}
```

---

## Phase 9: Idle Processing

**Background task** (started in IndexingEngine constructor:165):

```csharp
// ProcessIdleEpochsAsync
await foreach (var epoch in _analysisEpochChannel.Reader.ReadAllAsync()) {
    // Receives: 42

    if (epoch <= _lastReleasedEpoch)
        continue;  // Skip duplicates

    await ReleaseAnalysisAsync(epoch, cancellationToken);
    _lastReleasedEpoch = epoch;
}
```

**Inside ReleaseAnalysisAsync**:

### 9.1 Prune

```csharp
var pending = GetPendingItems(epoch: 42);
// Returns: [ README.md item ]

var pruneResult = await _pruner.PruneAsync(pending, cancellationToken);
// Compares pending URIs against database
// Returns: { StaleUris: [] } (no deletions in this case)
```

### 9.2 Delete Stale (none in this case)

```csharp
if (pruneResult.StaleUris.Count > 0) {
    await DeleteStaleDocumentsAsync(pruneResult.StaleUris, cancellationToken);
}
// Skipped: no stale URIs
```

### 9.3 Vector Refresh

```csharp
await _vectorCoordinator.ProcessPendingAsync(pending, pruneResult, cancellationToken);

// Inside VectorIndexCoordinator:
// 1. Apply deletes (none)
// 2. Compute embeddings for new/changed docs
var embeddings = await _embeddingService.ComputeEmbeddingsAsync([
    new EmbeddingRequest {
        DocumentUri: "file:///README.md",
        Text: "MyProject\n\nIntroduction to the project.\n\nInstallation\n\nSee docs/setup.md for details.",
        ChunkIndex: 0
    }
]);

// 3. Insert embeddings
INSERT INTO document_embedding (document_uri, chunk_index, embedding)
VALUES ('file:///README.md', 0, <768-dim vector>);
```

### 9.4 Multi-File Analysis

```csharp
foreach (var item in pending) {
    await _analysisQueue.EnqueueAsync(item, cancellationToken);
}

// MultiFileAnalysisPipeline processes:
// - CrossReferenceAnalyzer: Check if links resolve across documents
// - DependencyGraphAnalyzer: Build import/reference graph
// etc.
```

### 9.5 Index Rebuild

```csharp
// IndexRebuildPipeline: Rebuild secondary indexes, aggregates
```

---

## Final State

**Database contains**:

```sql
-- Query document
SELECT * FROM node WHERE uri = 'file:///README.md';
-- Returns: 1 document node + 3 child nodes (2 headings, 1 link)

-- Query annotations
SELECT * FROM annotation WHERE scope_document_id = '<guid-2>';
-- Returns: 1 warning (broken link)

-- Query xray
SELECT headline, summary FROM artifact WHERE digest = '3A9F7B2C...';
-- Returns: headline, summary with content preview

-- Query embeddings
SELECT * FROM document_embedding WHERE document_uri = 'file:///README.md';
-- Returns: 768-dim vector for semantic search
```

**Catalog state**:
```
_entries["file:///README.md"] = DocumentCatalogEntry {
    Uri: "file:///README.md",
    Digest: "3A9F7B2C...",
    MediaType: "text/markdown.doc",
    PhysicalPath: "C:\\Source\\MyRepo\\README.md",
    LastModified: 2025-01-13T10:30:00Z
}
```

**IndexingState**: `AllIdle`

---

## What Happens If File Changes?

User edits README.md, adds link to existing file:

```markdown
See [docs/setup.md](docs/setup.md) and [LICENSE](LICENSE) for details.
```

File watcher detects change → enqueue with same flow.

**Digest computation**:
```csharp
var newDigestHex = "B8E3D9A1...";  // Different!
```

**Catalog check**:
```csharp
var evaluation = catalog.Evaluate("file:///README.md", "B8E3D9A1...");
// Returns: Reindex (digest "B8E3D9A1..." differs from stored "3A9F7B2C...")
```

**Full pipeline runs again**:
- Classification: Returns "text/markdown.doc" (same)
- Parsing: New Records (additional link node)
- Analysis: Checks links (LICENSE exists → no warning)
- Commit: ReplaceDocument in database (old nodes deleted, new inserted)
- Catalog: Updated with new digest "B8E3D9A1..."

**Result**: README.md updated in database. Old broken link warning removed. No new warnings (both links valid).

---

## Summary

Ten-step journey from file change to queryable:

1. **Enqueue**: File wrapped in IndexItem, stamped with epoch
2. **Filter & Catalog**: Check if work needed (digest comparison)
3. **Classification**: Determine media type
4. **Parsing**: Build graph structure (nodes, edges, spans)
5. **Analysis**: Check for issues (broken links, lint)
6. **Commit**: Write to database (single-threaded)
7. **Schedule**: Add to pending analysis for epoch
8. **Epoch Complete**: Last item done + idle → event fires
9. **Idle Processing**: Prune, vector refresh, multi-file analysis
10. **Queryable**: File indexed and searchable

Key characteristics:
- Flow object pattern: One IndexItem accumulates state through all stages
- Incremental: Digest check prevents redundant work on unchanged files
- Serial writes: Single-threaded DatabaseWriter ensures consistency
- Batch operations: Post-processing runs once per epoch (not per file)
- Event-driven: Idle detection via events (not polling)
