# Idle Processing

This document covers the idle-window batch processing that runs when the hot path is quiet.

## Idle Detection

### IdleDetector Component

```csharp
namespace RepoQL.Core;

public sealed class IdleDetector
{
    private readonly RepositoryIndexer _indexer;
    private readonly TimeSpan _quietWindow;
    private readonly ILogger<IdleDetector> _logger;

    public IdleDetector(
        RepositoryIndexer indexer,
        TimeSpan? quietWindow = null,
        ILogger<IdleDetector>? logger = null)
    {
        _indexer = indexer;
        _quietWindow = quietWindow ?? TimeSpan.FromMilliseconds(500);
        _logger = logger ?? NullLogger<IdleDetector>.Instance;
    }

    public async Task<bool> WaitForIdleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var snapshot = _indexer.GetPipelineSnapshot();

            // Check if all hot-path queues are idle
            if (IsIdle(snapshot))
            {
                _logger.LogDebug("Pipeline idle detected, waiting {Window}ms quiet window",
                    _quietWindow.TotalMilliseconds);

                // Wait for quiet window
                await Task.Delay(_quietWindow, ct);

                // Re-check still idle
                snapshot = _indexer.GetPipelineSnapshot();
                if (IsIdle(snapshot))
                {
                    _logger.LogInformation("Pipeline confirmed idle after quiet window");
                    return true;
                }

                _logger.LogDebug("Pipeline no longer idle after quiet window, restarting detection");
            }

            // Check again soon
            await Task.Delay(100, ct);
        }

        return false;
    }

    private bool IsIdle(PipelineSnapshot snapshot)
    {
        return snapshot.discovery.depth == 0
            && snapshot.parsing.depth == 0
            && snapshot.writer.depth == 0;
    }
}
```

### Background Service

```csharp
namespace RepoQL.ConsoleApp.Host;

public sealed class IdleWorkCoordinator : BackgroundService
{
    private readonly IdleDetector _idleDetector;
    private readonly SemanticAnalysisBatch _semanticBatch;
    private readonly EmbeddingBatch _embeddingBatch;
    private readonly ILogger<IdleWorkCoordinator> _logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Idle work coordinator started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for idle
                await _idleDetector.WaitForIdleAsync(stoppingToken);

                _logger.LogInformation("Idle window detected, spawning batch work");

                // Spawn both batches concurrently
                var semanticTask = Task.Run(
                    () => _semanticBatch.RunAsync(stoppingToken),
                    stoppingToken);

                var embeddingTask = Task.Run(
                    () => _embeddingBatch.RunAsync(stoppingToken),
                    stoppingToken);

                // Wait for both to complete
                await Task.WhenAll(semanticTask, embeddingTask);

                _logger.LogInformation("Idle work completed");

                // Wait before checking for idle again (prevent tight loop)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Idle work coordinator error, will retry");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Idle work coordinator stopped");
    }
}
```

## Embedding Batch

### EmbeddingBatch Component

```csharp
namespace RepoQL.Core;

public sealed class EmbeddingBatch
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IGraphStore _store;
    private readonly int _batchSize;
    private readonly ILogger<EmbeddingBatch> _logger;
    private static readonly ActivitySource Activity = new("RepoQL.Core");

    public EmbeddingBatch(
        IEmbeddingProvider embeddingProvider,
        IGraphStore store,
        int? batchSize = null,
        ILogger<EmbeddingBatch>? logger = null)
    {
        _embeddingProvider = embeddingProvider;
        _store = store;
        _batchSize = batchSize ?? 8;
        _logger = logger ?? NullLogger<EmbeddingBatch>.Instance;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_embeddingProvider.Enabled)
        {
            _logger.LogDebug("Embedding provider not enabled, skipping batch");
            return;
        }

        using var activity = Activity.StartActivity("repoql.embedding.batch", ActivityKind.Internal);

        try
        {
            // Get last run
            var lastRun = _store.QuerySingleOrDefault<DateTime?>(
                "SELECT last_run_at FROM batch_state WHERE name='embeddings'")
                ?? DateTime.MinValue;

            activity?.SetTag("repoql.embedding.last_run_at", lastRun.ToString("o"));

            // Find changed documents
            var changedDocs = await GetChangedDocumentsAsync(lastRun, ct);

            // Find changed nodes
            var changedNodes = await GetChangedNodesAsync(lastRun, ct);

            if (!changedDocs.Any() && !changedNodes.Any())
            {
                _logger.LogDebug("No documents or nodes changed since last embedding run");
                activity?.SetTag("repoql.embedding.changed_docs", 0);
                activity?.SetTag("repoql.embedding.changed_nodes", 0);
                return;
            }

            _logger.LogInformation(
                "Embedding batch: {Docs} documents, {Nodes} nodes",
                changedDocs.Count(), changedNodes.Count());

            activity?.SetTag("repoql.embedding.changed_docs", changedDocs.Count());
            activity?.SetTag("repoql.embedding.changed_nodes", changedNodes.Count());

            var sw = Stopwatch.StartNew();
            var docCount = 0;
            var nodeCount = 0;

            // Embed documents
            docCount = await EmbedDocumentsAsync(changedDocs, ct);

            // Embed nodes
            nodeCount = await EmbedNodesAsync(changedNodes, ct);

            sw.Stop();

            // Update batch state
            _store.Execute(@"
                INSERT INTO batch_state (name, last_run_at)
                VALUES ('embeddings', NOW())
                ON CONFLICT (name) DO UPDATE SET last_run_at = NOW()");

            _logger.LogInformation(
                "Embedding batch completed: {Docs} docs, {Nodes} nodes in {Duration}ms",
                docCount, nodeCount, sw.ElapsedMilliseconds);

            activity?.SetTag("repoql.embedding.docs_embedded", docCount);
            activity?.SetTag("repoql.embedding.nodes_embedded", nodeCount);
            activity?.SetTag("repoql.embedding.duration_ms", sw.ElapsedMilliseconds);
            activity?.SetTag("otel.status_code", "OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding batch failed");
            activity?.SetTag("otel.status_code", "ERROR");
            activity?.SetTag("otel.status_description", ex.Message);
            throw;
        }
    }

    private async Task<IEnumerable<(Guid Id, string Text)>> GetChangedDocumentsAsync(
        DateTime lastRun,
        CancellationToken ct)
    {
        return _store.Query<(Guid, string)>(@"
            SELECT n.id, a.text_content
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.kind = 'document'
              AND a.text_content IS NOT NULL
              AND a.updated_at > @lastRun",
            new { lastRun });
    }

    private async Task<IEnumerable<NodeEmbeddingCandidate>> GetChangedNodesAsync(
        DateTime lastRun,
        CancellationToken ct)
    {
        return _store.Query<NodeEmbeddingCandidate>(@"
            SELECT
                n.id AS NodeId,
                n.uri AS Uri,
                n.kind AS Kind,
                s.start_line AS StartLine,
                s.end_line AS EndLine,
                SUBSTR(a.text_content, s.start_byte, s.end_byte - s.start_byte) AS Text
            FROM node n
            JOIN artifact a ON n.artifact_id = a.id
            JOIN span s ON n.span_id = s.id
            WHERE n.kind IN (
                'cs_class', 'cs_method', 'cs_interface', 'cs_property',
                'md_heading', 'md_code_block',
                'graphql_type', 'graphql_field'
            )
            AND a.updated_at > @lastRun",
            new { lastRun });
    }

    private async Task<int> EmbedDocumentsAsync(
        IEnumerable<(Guid Id, string Text)> documents,
        CancellationToken ct)
    {
        var count = 0;

        foreach (var batch in documents.Chunk(_batchSize))
        {
            using var tx = _store.BeginTransaction();

            foreach (var (id, text) in batch)
            {
                ct.ThrowIfCancellationRequested();

                var vec = await _embeddingProvider.EmbedAsync(text, ct);
                if (vec == null)
                {
                    _logger.LogWarning("Failed to embed document {Id}", id);
                    continue;
                }

                var json = SerializeFloatArray(vec);

                _store.Execute(@"
                    INSERT INTO document_embedding (doc_id, model, dim, embedding, updated_at)
                    VALUES (?, ?, ?, ?, NOW())
                    ON CONFLICT (doc_id)
                    DO UPDATE SET
                        model = excluded.model,
                        dim = excluded.dim,
                        embedding = excluded.embedding,
                        updated_at = excluded.updated_at",
                    id, _embeddingProvider.Model, _embeddingProvider.Dimension, json);

                count++;
            }

            tx.Commit();
        }

        return count;
    }

    private async Task<int> EmbedNodesAsync(
        IEnumerable<NodeEmbeddingCandidate> nodes,
        CancellationToken ct)
    {
        var count = 0;

        foreach (var batch in nodes.Chunk(_batchSize))
        {
            using var tx = _store.BeginTransaction();

            foreach (var node in batch)
            {
                ct.ThrowIfCancellationRequested();

                var vec = await _embeddingProvider.EmbedAsync(node.Text, ct);
                if (vec == null)
                {
                    _logger.LogWarning("Failed to embed node {Id}", node.NodeId);
                    continue;
                }

                var json = SerializeFloatArray(vec);

                _store.Execute(@"
                    INSERT INTO node_embedding (
                        node_id, uri, kind, start_line, end_line,
                        model, dim, embedding, updated_at
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, NOW())
                    ON CONFLICT (node_id)
                    DO UPDATE SET
                        uri = excluded.uri,
                        kind = excluded.kind,
                        start_line = excluded.start_line,
                        end_line = excluded.end_line,
                        model = excluded.model,
                        dim = excluded.dim,
                        embedding = excluded.embedding,
                        updated_at = excluded.updated_at",
                    node.NodeId, node.Uri, node.Kind, node.StartLine, node.EndLine,
                    _embeddingProvider.Model, _embeddingProvider.Dimension, json);

                count++;
            }

            tx.Commit();
        }

        return count;
    }

    private static string SerializeFloatArray(float[] vector)
    {
        return System.Text.Json.JsonSerializer.Serialize(vector);
    }

    private record NodeEmbeddingCandidate(
        Guid NodeId,
        string Uri,
        string Kind,
        int StartLine,
        int EndLine,
        string Text);
}
```

### Configuration

```bash
# Batch size (documents per transaction)
REPOQL_EMBED_BATCH_SIZE=8

# Enable/disable embeddings
REPOQL_EMBED_ENABLED=1

# Model path (optional override)
REPOQL_EMBED_MODEL_PATH=/path/to/model.onnx
```

### Performance Characteristics

**Throughput:**
- 8 documents per batch (configurable)
- ~10-50ms per document (model-dependent)
- Total: ~100-400ms per batch of 8

**Scaling:**
- 100 changed documents = ~13 batches = ~1-5 seconds
- 1000 changed documents = ~125 batches = ~12-50 seconds

**Optimization opportunities:**
- Use GPU for inference (via ONNX Runtime DirectML/CUDA)
- Increase batch size if memory allows
- Run in parallel with semantic batch (currently does)

## Semantic Analysis Batch

### SemanticAnalysisBatch Component

```csharp
namespace RepoQL.Core;

public sealed class SemanticAnalysisBatch
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly IGraphStore _store;
    private readonly WorkQueue _writerQueue;
    private readonly int _maxUris;
    private readonly ILogger<SemanticAnalysisBatch> _logger;
    private static readonly ActivitySource Activity = new("RepoQL.Core");

    public SemanticAnalysisBatch(
        IWorkspaceManager workspaceManager,
        IGraphStore store,
        WorkQueue writerQueue,
        int? maxUris = null,
        ILogger<SemanticAnalysisBatch>? logger = null)
    {
        _workspaceManager = workspaceManager;
        _store = store;
        _writerQueue = writerQueue;
        _maxUris = maxUris ?? 5000;
        _logger = logger ?? NullLogger<SemanticAnalysisBatch>.Instance;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var activity = Activity.StartActivity("repoql.semantic.batch", ActivityKind.Internal);

        try
        {
            // Get last run
            var lastRun = _store.QuerySingleOrDefault<DateTime?>(
                "SELECT last_run_at FROM batch_state WHERE name='semantic_analysis'")
                ?? DateTime.MinValue;

            activity?.SetTag("repoql.semantic.last_run_at", lastRun.ToString("o"));

            // Find changed C# documents
            var changedUris = _store.Query<string>(@"
                SELECT n.uri
                FROM node n
                JOIN artifact a ON a.id = n.artifact_id
                WHERE n.kind = 'document'
                  AND a.media_type LIKE '%csharp%'
                  AND a.updated_at > @lastRun
                LIMIT @maxUris",
                new { lastRun, maxUris = _maxUris });

            if (!changedUris.Any())
            {
                _logger.LogDebug("No C# documents changed since last semantic analysis");
                activity?.SetTag("repoql.semantic.changed_count", 0);
                return;
            }

            var uriList = changedUris.ToList();
            _logger.LogInformation("Semantic analysis batch: {Count} C# files", uriList.Count);
            activity?.SetTag("repoql.semantic.changed_count", uriList.Count);

            var sw = Stopwatch.StartNew();

            // Build workspace
            WorkspaceSnapshot? workspace = null;
            try
            {
                var workspaceSw = Stopwatch.StartNew();
                workspace = _workspaceManager.GetOrBuild("csharp", ct);
                workspaceSw.Stop();

                _logger.LogInformation(
                    "Built C# workspace: {Uris} documents in {Duration}ms",
                    workspace.Uris.Count, workspaceSw.ElapsedMilliseconds);

                activity?.SetTag("repoql.semantic.workspace.uris", workspace.Uris.Count);
                activity?.SetTag("repoql.semantic.workspace.duration_ms", workspaceSw.ElapsedMilliseconds);

                // Run analyzers
                await RunAnalyzersAsync(workspace, uriList, ct);
            }
            finally
            {
                workspace?.Dispose();
            }

            sw.Stop();

            // Update batch state
            _store.Execute(@"
                INSERT INTO batch_state (name, last_run_at)
                VALUES ('semantic_analysis', NOW())
                ON CONFLICT (name) DO UPDATE SET last_run_at = NOW()");

            _logger.LogInformation(
                "Semantic analysis completed: {Count} files in {Duration}ms",
                uriList.Count, sw.ElapsedMilliseconds);

            activity?.SetTag("repoql.semantic.duration_ms", sw.ElapsedMilliseconds);
            activity?.SetTag("otel.status_code", "OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic analysis batch failed");
            activity?.SetTag("otel.status_code", "ERROR");
            activity?.SetTag("otel.status_description", ex.Message);
            throw;
        }
    }

    private async Task RunAnalyzersAsync(
        WorkspaceSnapshot workspace,
        List<string> uris,
        CancellationToken ct)
    {
        // Example analyzer: Find unused public symbols
        var analyzer = new UnusedPublicSymbolAnalyzer(workspace, _store, _logger);

        foreach (var uri in uris)
        {
            ct.ThrowIfCancellationRequested();

            var annotations = await analyzer.AnalyzeAsync(uri, ct);

            if (annotations.Any())
            {
                // Enqueue to writer
                _writerQueue.Enqueue(new WriteOperation
                {
                    Annotations = annotations.ToList()
                });
            }
        }
    }
}
```

### Example Analyzer: Unused Public Symbols

```csharp
namespace RepoQL.Core.Analysis;

public class UnusedPublicSymbolAnalyzer
{
    private readonly CSharpWorkspaceSnapshot _workspace;
    private readonly IGraphStore _store;
    private readonly ILogger _logger;

    public async Task<IEnumerable<Annotation>> AnalyzeAsync(string uri, CancellationToken ct)
    {
        var annotations = new List<Annotation>();

        // Get semantic model for document
        var document = _workspace.GetDocumentByUri(uri);
        if (document == null) return annotations;

        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel == null) return annotations;

        // Find all public symbols declared in this document
        var root = await document.GetSyntaxRootAsync(ct);
        var publicSymbols = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
            .Select(m => semanticModel.GetDeclaredSymbol(m))
            .Where(s => s != null)
            .ToList();

        // Check each symbol for references across the workspace
        foreach (var symbol in publicSymbols)
        {
            ct.ThrowIfCancellationRequested();

            // Find references in other documents
            var references = await SymbolFinder.FindReferencesAsync(
                symbol,
                _workspace.Solution,
                ct);

            // If no external references, it's unused
            var externalRefs = references
                .SelectMany(r => r.Locations)
                .Where(l => l.Document.FilePath != document.FilePath);

            if (!externalRefs.Any())
            {
                annotations.Add(new Annotation
                {
                    Kind = "unused_symbol",
                    Severity = "info",
                    Source = "semantic_analysis",
                    Message = $"Public {symbol.Kind.ToString().ToLower()} '{symbol.Name}' is not used outside this file",
                    ScopeDocumentUri = uri,
                    // Set target to specific symbol location
                });
            }
        }

        return annotations;
    }
}
```

### Configuration

```bash
# Max URIs per semantic batch
REPOQL_SEMANTIC_BATCH_SIZE=5000

# Future: enable/disable specific analyzers
REPOQL_SEMANTIC_ANALYZERS=unused_symbols,call_graph
```

## Error Handling

### Batch Failure Semantics

**Key principle:** Only update `batch_state.last_run_at` on successful completion.

```csharp
try {
    // Run batch
    await embeddingBatch.RunAsync(ct);

    // Only update on success
    db.Execute("UPDATE batch_state SET last_run_at=NOW() WHERE name='embeddings'");
}
catch (Exception ex) {
    _logger.LogWarning(ex, "Embedding batch failed, will retry next idle window");
    // Don't update last_run_at - next idle will retry from same timestamp
}
```

**Guarantees:**
- ✅ Crash-safe: last_run_at is persisted
- ✅ Idempotent: re-processing is safe (UPSERT semantics)
- ✅ No data loss: failed batches retry automatically

### Partial Batch Failures

**Embeddings:** If a single document fails to embed, log and continue:

```csharp
foreach (var (id, text) in batch) {
    try {
        var vec = await provider.EmbedAsync(text, ct);
        // ... upsert ...
    }
    catch (Exception ex) {
        _logger.LogWarning(ex, "Failed to embed document {Id}, skipping", id);
        continue;  // Skip this document, continue batch
    }
}
```

**Semantic analysis:** If analyzer throws, log and continue to next file:

```csharp
foreach (var uri in uris) {
    try {
        var annotations = await analyzer.AnalyzeAsync(uri, ct);
        // ... write ...
    }
    catch (Exception ex) {
        _logger.LogWarning(ex, "Failed to analyze {Uri}, skipping", uri);
        continue;
    }
}
```

## Testing

### Unit Tests

```csharp
[Test]
public async Task EmbeddingBatch_ProcessesOnlyChangedDocuments()
{
    // Arrange: Set last_run_at to T0
    db.Execute("UPDATE batch_state SET last_run_at=@t0 WHERE name='embeddings'",
        new { t0 = DateTime.Parse("2025-01-01 10:00:00") });

    // Edit one file at T1
    await indexer.IndexFileAsync("foo.cs", "2025-01-01 10:05:00");

    // Act: Run embedding batch
    await embeddingBatch.RunAsync(ct);

    // Assert: Only foo.cs was embedded
    var embeddings = db.Query<string>(
        "SELECT uri FROM node n JOIN document_embedding de ON n.id=de.doc_id " +
        "WHERE de.updated_at > @t0",
        new { t0 = DateTime.Parse("2025-01-01 10:00:00") });

    Assert.Single(embeddings);
    Assert.Contains("foo.cs", embeddings);
}

[Test]
public async Task EmbeddingBatch_UpdatesLastRunOnSuccess()
{
    var beforeRun = DateTime.UtcNow;
    await Task.Delay(10);

    await embeddingBatch.RunAsync(ct);

    var lastRun = db.QuerySingle<DateTime>(
        "SELECT last_run_at FROM batch_state WHERE name='embeddings'");

    Assert.True(lastRun > beforeRun);
}

[Test]
public async Task EmbeddingBatch_DoesNotUpdateLastRunOnFailure()
{
    var beforeRun = db.QuerySingle<DateTime>(
        "SELECT last_run_at FROM batch_state WHERE name='embeddings'");

    // Inject failing provider
    var failingProvider = new Mock<IEmbeddingProvider>();
    failingProvider.Setup(p => p.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(new Exception("Embedding service unavailable"));

    var batch = new EmbeddingBatch(failingProvider.Object, store);

    // Act
    await Assert.ThrowsAsync<Exception>(() => batch.RunAsync(ct));

    // Assert: last_run_at unchanged
    var afterRun = db.QuerySingle<DateTime>(
        "SELECT last_run_at FROM batch_state WHERE name='embeddings'");

    Assert.Equal(beforeRun, afterRun);
}
```

### Integration Tests

```csharp
[Test]
public async Task IdleDetection_TriggersAfterQuietWindow()
{
    // Start indexing
    var indexTask = indexer.IndexDirectoryAsync("/repo", ct);

    // Wait for idle
    var idleTask = idleDetector.WaitForIdleAsync(ct);

    // Indexing completes
    await indexTask;

    // Idle detected after 500ms quiet window
    var idle = await idleTask.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.True(idle);
}

[Test]
public async Task SemanticBatch_FindsUnusedPublicMethod()
{
    // Arrange: Two files, one with unused public method
    await indexer.IndexFileAsync("Foo.cs", @"
        public class Foo {
            public void UsedMethod() { }
            public void UnusedMethod() { }
        }
    ");
    await indexer.IndexFileAsync("Bar.cs", @"
        public class Bar {
            void Test() { new Foo().UsedMethod(); }
        }
    ");

    await indexer.WaitForIdleAsync(ct);

    // Act: Run semantic analysis
    await semanticBatch.RunAsync(ct);

    // Assert: Annotation created for UnusedMethod
    var annotations = db.Query<Annotation>(
        "SELECT * FROM annotation WHERE kind='unused_symbol'");

    Assert.Single(annotations);
    Assert.Contains("UnusedMethod", annotations[0].Message);
}
```
