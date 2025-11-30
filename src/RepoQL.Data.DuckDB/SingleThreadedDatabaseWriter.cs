using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;
using DuckDB.NET.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Metrics;
using System.Data;
using System.Diagnostics.Metrics;

namespace RepoQL.Data.DuckDB;

public sealed class SingleThreadedDatabaseWriter(
    IDuckDBConnectionFactory connectionFactory,
    IDuckDbGraphStoreFactory graphStoreFactory,
    IndexingMetrics? metrics = null,
    ILogger<SingleThreadedDatabaseWriter>? logger = null,
    IEnumerable<IFormatSchemaProvider>? schemaProviders = null)
    : IDatabaseWriter, IHostedService
{
    private readonly Channel<QueueItem> _channel = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(MaxQueueDepth)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly IDuckDBConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    private readonly IDuckDbGraphStoreFactory _graphStoreFactory = graphStoreFactory ?? throw new ArgumentNullException(nameof(graphStoreFactory));

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Metrics lifetime is managed by the DI container.")]
    private readonly IndexingMetrics _metrics = metrics ?? new IndexingMetrics();

    private readonly ILogger<SingleThreadedDatabaseWriter> _logger =
        logger ?? NullLogger<SingleThreadedDatabaseWriter>.Instance;
    private readonly IEnumerable<IFormatSchemaProvider> _schemaProviders = schemaProviders ?? [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _writerTask;

    // Stats
    private long _processed;

    // Writer owns a single connection for all writes
    private DuckDBConnection? _writeConnection;
    private DuckDbGraphStore? _store;
    private bool _isDisposed;

    private const int MaxQueueDepth = 1000;
    private const int MaxBatchSize = 32;
    private const int MaxWriteAttempts = 3;
    private static readonly ActivitySource Activity = new("RepoQL.Indexing");

    private sealed class QueueItem
    {
        public required WriteOperation Operation { get; init; }
        public TaskCompletionSource<CommitResult>? Completion { get; init; }
        public long EnqueuedTimestamp { get; init; }
    }

    public int QueueCapacity => MaxQueueDepth;

    public ValueTask EnqueueAsync(WriteOperation operation, CancellationToken ct = default)
    {
        var item = new QueueItem { Operation = operation, EnqueuedTimestamp = Stopwatch.GetTimestamp() };
        return _channel.Writer.WriteAsync(item, ct);
    }

    public async ValueTask<CommitResult> EnqueueAndWaitAsync(WriteOperation operation, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new QueueItem { Operation = operation, Completion = tcs, EnqueuedTimestamp = Stopwatch.GetTimestamp() };
        await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<FlushResult> FlushAsync(CancellationToken ct = default)
    {
        // Drain current queue by inserting a barrier
        var before = _processed;
        // Use a barrier op that increments processed without applying any changes
        var barrierOp = new WriteOperation
        {
            Id = Guid.NewGuid(),
            Type = WriteOperationType.Barrier,
            Uri = RepoUri.Parse("mem://writer-barrier"),
            ParsedData = Records.Empty
        };
        await EnqueueAndWaitAsync(barrierOp, ct).ConfigureAwait(false);
        var after = _processed;
        return new FlushResult { OperationsFlushed = (int)(after - before) };
    }

    public WriterStatus GetStatus() => new()
    {
        PendingCount = _channel.Reader.Count,
        TotalProcessed = Interlocked.Read(ref _processed)
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Initialize connection and store once
        _writeConnection = _connectionFactory.CreateConnection();
        var formatScripts = _schemaProviders
            .SelectMany(p => p.GetSchemaScripts())
            .Where(s => !string.IsNullOrWhiteSpace(s.Sql))
            .ToArray();
        _store = _graphStoreFactory.Create(_writeConnection, formatScripts);
        _store.EnsureSchema();

        _metrics.SetQueueDepthCallback(() => _channel.Reader.Count);
        _metrics.SetQueueCapacityCallback(() => MaxQueueDepth);
        _metrics.SetDbConnectionsActiveCallback(() =>
        {
            var conn = _writeConnection;
            return conn is { State: ConnectionState.Open } ? 1 : 0;
        });

        // Register database total callbacks
        _metrics.RegisterDatabaseCallbacks(
            documentsTotal: () => _store?.DocumentsTotal ?? 0,
            nodesTotal: () => _store?.NodesTotal ?? 0,
            edgesTotal: () => _store?.EdgesTotal ?? 0,
            annotationsTotal: () => _store?.AnnotationsTotal ?? 0,
            embeddingsTotal: () => _store?.EmbeddingsTotal ?? 0
        );

        // Trigger initial count refresh
        _ = Task.Run(() => _store?.RefreshCountsAsync());

        _writerTask = Task.Run(WriterLoop, cancellationToken);
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed)
            return;
        await _stopping.CancelAsync();
        _channel.Writer.TryComplete();
        if (_writerTask is not null)
        {
            try { await _writerTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _store?.Dispose();
        await (_writeConnection?.DisposeAsync() ?? ValueTask.CompletedTask);
    }

    private async Task WriterLoop()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                var batch = new List<QueueItem>(MaxBatchSize);
                while (_channel.Reader.TryRead(out var item) && batch.Count < MaxBatchSize)
                {
                    batch.Add(item);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                if (await TryProcessBatchAsync(batch).ConfigureAwait(false))
                {
                    continue;
                }

                foreach (var qi in batch)
                {
                    await ProcessOneAsync(qi).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Writer loop crashed");
        }
    }

    private async Task ProcessOneAsync(QueueItem item)
    {
        var op = item.Operation;
        RecordQueueWait(item, "single", 1);
        // Create a span for the database write, parented to upstream step to continue the distributed trace
        using var writeActivity = Activity.StartActivity(
            "repoql.db.write",
            ActivityKind.Client,
            op.ParentContext ?? default,
            tags:
            [
                new KeyValuePair<string, object?>("db.system", "duckdb"),
                new KeyValuePair<string, object?>("db.operation", op.Type.ToString()),
                new KeyValuePair<string, object?>("db.operation.name", op.Type.ToString()),
                new KeyValuePair<string, object?>("url.full", op.Uri.AbsoluteUri),
                new KeyValuePair<string, object?>("repoql.uri", op.Uri.AbsoluteUri)
            ],
            links: null);
        CommitResult result;
        Exception? failure = null;
        var attempt = 1;
        while (true)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await ExecuteOperationAsync(op).ConfigureAwait(false);
                sw.Stop();
                Interlocked.Increment(ref _processed);
                _metrics.RecordDbWriteDuration(sw.Elapsed.TotalMilliseconds, op.Type.ToString());
                _metrics.TransactionsCommitted.Add(1, new TagList
                {
                    { "operation_type", op.Type.ToString() },
                    { "mode", "single" }
                });
                _store?.NotifyCommit();
                failure = null;
                break;
            }
            catch (DuckDBException dex)
            {
                if (IsRecoverableDuckDbError(dex) && attempt < MaxWriteAttempts)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(dex,
                            "DuckDB write attempt {Attempt}/{Max} failed for {Uri}; retrying",
                            attempt,
                            MaxWriteAttempts,
                            op.Uri);
                    }
                    attempt++;
                    continue;
                }

                failure = dex;
                break;
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }
        }

        if (failure is null)
        {
            result = new CommitResult { Success = true };
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(failure, "Failed processing write operation {OpId} for {Uri}", op.Id, op.Uri);
            }
            try
            {
                if (writeActivity is not null)
                {
                    var tags = new ActivityTagsCollection
                    {
                        {"exception.type", failure.GetType().FullName},
                        {"exception.message", failure.Message},
                        {"exception.stacktrace", failure.ToString()}
                    };
                    writeActivity.AddEvent(new ActivityEvent("exception", default, tags));
                    writeActivity.SetTag("otel.status_code", "ERROR");
                    writeActivity.SetTag("otel.status_description", failure.Message);
                }
            }
            catch { }

            result = new CommitResult { Success = false, Error = failure };
            _metrics.TransactionsFailed.Add(1, new TagList
            {
                { "operation_type", op.Type.ToString() },
                { "error_type", failure.GetType().Name }
            });
        }

        item.Completion?.TrySetResult(result);
        FireOnCommitted(op, result);
    }

    private void RecordQueueWait(QueueItem item, string mode, int batchSize)
    {
        var waitMs = Stopwatch.GetElapsedTime(item.EnqueuedTimestamp).TotalMilliseconds;
        _metrics.QueueWaitTime.Record(waitMs, new TagList
        {
            { "operation", item.Operation.Type.ToString() },
            { "mode", mode },
            { "batch_size", batchSize }
        });
    }

    private void FireOnCommitted(WriteOperation op, CommitResult result)
    {
        if (op.OnCommitted is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await op.OnCommitted(op, result).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(ex, "OnCommitted callback failed for {OpId}", op.Id);
                }
            }
        });
    }

    private async Task<bool> TryProcessBatchAsync(List<QueueItem> batch)
    {
        if (batch.Count == 0)
            return true;

        if (batch.Count == 1)
        {
            await ProcessOneAsync(batch[0]).ConfigureAwait(false);
            return true;
        }

        try
        {
            foreach (var qi in batch)
            {
                RecordQueueWait(qi, "batch", batch.Count);
            }
            _writeConnection ??= _connectionFactory.CreateConnection();
            if (_writeConnection.State != System.Data.ConnectionState.Open)
            {
                await _writeConnection.OpenAsync(_stopping.Token).ConfigureAwait(false);
            }

            var batchSw = Stopwatch.StartNew();
            await using var tx = await _writeConnection.BeginTransactionAsync(_stopping.Token).ConfigureAwait(false);
            foreach (var item in batch)
            {
                await ExecuteOperationAsync(item.Operation).ConfigureAwait(false);
                Interlocked.Increment(ref _processed);
            }
            await tx.CommitAsync(_stopping.Token).ConfigureAwait(false);
            batchSw.Stop();

            _metrics.BatchDuration.Record(batchSw.Elapsed.TotalMilliseconds, new TagList
            {
                { "batch_size", batch.Count.ToString() }
            });

            // Determine batch size bucket
            var batchSizeBucket = batch.Count switch
            {
                <= 10 => "1-10",
                <= 32 => "11-32",
                _ => "33-64"
            };
            _metrics.BatchesCommitted.Add(1, new TagList { { "batch_size_bucket", batchSizeBucket } });

            _metrics.TransactionsCommitted.Add(1, new TagList
            {
                { "operation_type", "Batch" },
                { "mode", "batch" }
            });

            var perItemMs = batch.Count == 0 ? 0 : batchSw.Elapsed.TotalMilliseconds / batch.Count;
            foreach (var item in batch)
            {
                _metrics.RecordDbWriteDuration(perItemMs, item.Operation.Type.ToString());
            }

            // Notify store for count refresh
            _store?.NotifyCommit();

            foreach (var item in batch)
            {
                item.Completion?.TrySetResult(new CommitResult { Success = true });
                FireOnCommitted(item.Operation, new CommitResult { Success = true });
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private Task ApplyReplaceDocumentAsync(WriteOperation op)
    {
        if (_store is null) throw new InvalidOperationException("Writer not started");
        var records = op.ParsedData;

        // 1) Persist artifacts; map original -> stored IDs
        var artifactIdMap = new Dictionary<Guid, Guid>();
        foreach (var a in records.Artifacts)
        {
            var saved = _store.UpsertArtifact(a);
            artifactIdMap[a.Id] = saved.Id;
        }

        // 2) Upsert document by URI; refresh subtree
        var docRec = records.Nodes.FirstOrDefault(n => string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Parser did not produce a document node.");

        // Check if document exists to determine create vs update
        var existingDoc = _store.GetDocumentByUri(op.Uri);
        var isUpdate = existingDoc is not null;

        var docArtifactId = docRec.ArtifactId is { } da && artifactIdMap.TryGetValue(da, out var newDa) ? newDa : docRec.ArtifactId;
        var docNode = new Node
        {
            Id = docRec.Id,
            Kind = "document",
            Uri = op.Uri,
            ArtifactId = docArtifactId,
            SpanId = null,
            Props = EnrichDocPropsWithFrontmatter(docRec.Props) ?? new System.Text.Json.Nodes.JsonObject(),
            Headline = docRec.Headline,
            Structure = docRec.Structure,
            CreatedAt = docRec.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var savedDoc = _store.UpsertDocumentByUri(op.Uri, docNode);
        var savedDocId = savedDoc.Id;

        // 3) Prepare children with remapped artifact IDs
        var childNodes = new List<Node>();
        foreach (var n in records.Nodes.Where(n => !string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase)))
        {
            var node = n;
            if (n.ArtifactId is { } aid && artifactIdMap.TryGetValue(aid, out var newAid))
            {
                node = new Node
                {
                    Id = n.Id,
                    Kind = n.Kind,
                    Uri = n.Uri,
                    ArtifactId = newAid,
                    SpanId = n.SpanId,
                    Props = n.Props,
                    Headline = n.Headline,
                    Structure = n.Structure,
                    CreatedAt = n.CreatedAt,
                    UpdatedAt = n.UpdatedAt
                };
            }
            childNodes.Add(node);
        }

        // 4) Spans mapped to saved doc id
        var spans = records.Spans.Select(s => new Span
        {
            Id = s.Id,
            DocumentId = savedDocId,
            StartByte = s.StartByte,
            EndByte = s.EndByte,
            StartLine = s.StartLine,
            StartColumn = s.StartColumn,
            EndLine = s.EndLine,
            EndColumn = s.EndColumn
        }).ToArray();

        // 5) Edges with doc id mapping and scope
        var edges = records.Edges.Select(e => new Edge
        {
            Id = e.Id,
            SrcId = e.SrcId == docRec.Id ? savedDocId : e.SrcId,
            DstId = e.DstId == docRec.Id ? savedDocId : e.DstId,
            Type = e.Type,
            IsComposition = e.IsComposition,
            Ordinal = e.Ordinal,
            ScopeDocumentId = savedDocId,
            EdgeKey = e.EdgeKey,
            SrcSpanId = e.SrcSpanId,
            DstSpanId = e.DstSpanId,
            Props = e.Props,
            CreatedAt = e.CreatedAt
        }).ToArray();

        _store.ReplaceDocumentContent(savedDocId, childNodes, spans, edges);

        // 6) Document outline annotation removed: x-ray summaries live on artifact fields now.

        // 7) Record document lifecycle and graph extraction metrics
        var mimeType = records.Artifacts.FirstOrDefault()?.MediaType.ToString() ?? "unknown";
        if (isUpdate)
        {
            _metrics.DocumentsUpdated.Add(1, new TagList { { "mime_type", mimeType } });
        }
        else
        {
            _metrics.DocumentsCreated.Add(1, new TagList { { "mime_type", mimeType } });
        }

        // Record graph extraction metrics
        var nodeCount = 1 + childNodes.Count; // doc node + children
        _metrics.NodesExtracted.Add(nodeCount, new TagList { { "mime_type", mimeType } });
        _metrics.EdgesExtracted.Add(edges.Length, new TagList { { "mime_type", mimeType } });
        _metrics.SpansExtracted.Add(spans.Length, new TagList { { "mime_type", mimeType } });

        return Task.CompletedTask;
    }

    private void ApplyUpsertAnnotations(WriteOperation op)
    {
        if (_store is null) throw new InvalidOperationException("Writer not started");
        var records = op.ParsedData;
        var annotations = records.Annotations ?? Array.Empty<Annotation>();
        var globalSources = records.AnnotationSources ?? Array.Empty<string>();

        var docIds = new HashSet<Guid>(annotations.Select(a => a.ScopeDocumentId));
        if (docIds.Count == 0)
        {
            var doc = _store.GetDocumentByUri(op.Uri);
            if (doc is not null)
            {
                docIds.Add(doc.Id);
            }
        }

        foreach (var docId in docIds)
        {
            var newForDocument = annotations.Where(a => a.ScopeDocumentId == docId).ToList();
            var sourcesToClear = new HashSet<string>(globalSources, StringComparer.Ordinal);
            foreach (var annotation in newForDocument)
            {
                if (!string.IsNullOrWhiteSpace(annotation.Source))
                    sourcesToClear.Add(annotation.Source);
            }

            if (sourcesToClear.Count > 0)
            {
                var newKeys = new HashSet<string>(newForDocument.Where(a => !string.IsNullOrEmpty(a.SemanticKey)).Select(a => a.SemanticKey!), StringComparer.Ordinal);
                var includeNullKey = newForDocument.Any(a => string.IsNullOrEmpty(a.SemanticKey));

                var existing = _store.GetAnnotationsForDocument(docId).ToList();
                foreach (var stale in existing)
                {
                    if (string.IsNullOrEmpty(stale.Source))
                        continue;
                    if (!sourcesToClear.Contains(stale.Source))
                        continue;

                    var key = stale.SemanticKey;
                    if (!string.IsNullOrEmpty(key) && newKeys.Contains(key))
                        continue;

                    if (string.IsNullOrEmpty(key) && includeNullKey)
                        continue;

                    _store.DeleteAnnotation(stale.Id);
                }
            }

            foreach (var annotation in newForDocument)
            {
                _store.UpsertAnnotation(annotation);
            }

            // Record annotation metrics
            if (newForDocument.Count > 0)
            {
                _metrics.AnnotationsUpserted.Add(newForDocument.Count, new TagList { { "operation", "upsert" } });
            }
        }
    }

    private void ApplyDeleteDocument(WriteOperation op)
    {
        if (_store is null) throw new InvalidOperationException("Writer not started");
        _store.DeleteDocumentByUri(op.Uri);
        _metrics.DocumentsDeleted.Add(1);
    }

    private Task ExecuteOperationAsync(WriteOperation op)
    {
        return op.Type switch
        {
            WriteOperationType.ReplaceDocument => ApplyReplaceDocumentAsync(op),
            WriteOperationType.UpsertAnnotations => ExecuteSynchronously(() => ApplyUpsertAnnotations(op)),
            WriteOperationType.DeleteDocument => ExecuteSynchronously(() => ApplyDeleteDocument(op)),
            WriteOperationType.Barrier => Task.CompletedTask,
            _ => throw new NotSupportedException($"Unsupported op: {op.Type}")
        };
    }

    private static Task ExecuteSynchronously(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static bool IsRecoverableDuckDbError(DuckDBException ex)
    {
        var message = ex.Message;
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("Conflict on tuple deletion", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Current transaction is aborted", StringComparison.OrdinalIgnoreCase);
    }

    private static System.Text.Json.Nodes.JsonObject? EnrichDocPropsWithFrontmatter(System.Text.Json.Nodes.JsonObject? original)
    {
        // Deep-clone to avoid assigning nodes that already have a parent
        var props = original is null
            ? new System.Text.Json.Nodes.JsonObject()
            : (System.Text.Json.Nodes.JsonObject)original.DeepClone();
        try
        {
            if (original is not null && original.TryGetPropertyValue("frontmatter", out var fmNode) && fmNode is System.Text.Json.Nodes.JsonObject fm)
            {
                // Copy selected keys if present
                if (fm.TryGetPropertyValue("description", out var d) && d is not null)
                    props["description"] = d.DeepClone();
                if (fm.TryGetPropertyValue("documentationCategory", out var c) && c is not null)
                    props["documentationCategory"] = c.DeepClone();
                if (fm.TryGetPropertyValue("tags", out var t) && t is not null)
                {
                    // Ensure tags is an array
                    if (t is System.Text.Json.Nodes.JsonArray or System.Text.Json.Nodes.JsonObject)
                        props["tags"] = t.DeepClone();
                    else // scalar -> wrap
                        props["tags"] = new System.Text.Json.Nodes.JsonArray(t.DeepClone());
                }
            }
        }
        catch { }
        return props;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _isDisposed = true;
        _stopping.Dispose();
    }
}
