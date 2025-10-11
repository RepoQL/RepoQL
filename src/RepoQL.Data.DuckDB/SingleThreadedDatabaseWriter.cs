using System.Diagnostics;
using System.Threading.Channels;
using DuckDB.NET.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB;

public sealed class SingleThreadedDatabaseWriter(
    IDuckDBConnectionFactory connectionFactory,
    ILogger<SingleThreadedDatabaseWriter>? logger = null)
    : IDatabaseWriter, IHostedService
{
    private readonly Channel<QueueItem> _channel = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(MaxQueueDepth)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly IDuckDBConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    private readonly ILogger<SingleThreadedDatabaseWriter> _logger =
        logger ?? NullLogger<SingleThreadedDatabaseWriter>.Instance;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _writerTask;

    // Stats
    private long _processed;

    // Writer owns a single connection for all writes
    private DuckDBConnection? _writeConnection;
    private DuckDbGraphStore? _store;
    private bool _isDisposed;

    private const int MaxQueueDepth = 1000;
    private static readonly ActivitySource Activity = new("RepoQL.Indexing");

    private sealed class QueueItem
    {
        public required WriteOperation Operation { get; init; }
        public TaskCompletionSource<CommitResult>? Completion { get; init; }
    }

    public int QueueCapacity => MaxQueueDepth;

    public ValueTask EnqueueAsync(WriteOperation operation, CancellationToken ct = default)
    {
        var item = new QueueItem { Operation = operation };
        return _channel.Writer.WriteAsync(item, ct);
    }

    public async ValueTask<CommitResult> EnqueueAndWaitAsync(WriteOperation operation, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new QueueItem { Operation = operation, Completion = tcs };
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
            ParsedData = new Records { Artifacts = [], Nodes = [], Spans = [], Edges = [] }
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
        // Writer does not need UDFs; avoid duplicate registration conflicts
        _store = new DuckDbGraphStore(_writeConnection, enableExtensions: true, registerUdfs: false);
        _store.EnsureSchema();

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
                while (_channel.Reader.TryRead(out var item))
                {
                    await ProcessOneAsync(item).ConfigureAwait(false);
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
        try
        {
            switch (op.Type)
            {
                case WriteOperationType.ReplaceDocument:
                    await ApplyReplaceDocumentAsync(op).ConfigureAwait(false);
                    break;
                case WriteOperationType.Barrier:
                    // no-op
                    break;
                default:
                    throw new NotSupportedException($"Unsupported op: {op.Type}");
            }
            Interlocked.Increment(ref _processed);
            result = new CommitResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed processing write operation {OpId} for {Uri}", op.Id, op.Uri);
            try
            {
                if (writeActivity is not null)
                {
                    var tags = new ActivityTagsCollection
                    {
                        {"exception.type", ex.GetType().FullName},
                        {"exception.message", ex.Message},
                        {"exception.stacktrace", ex.ToString()}
                    };
                    writeActivity.AddEvent(new ActivityEvent("exception", default, tags));
                    writeActivity.SetTag("otel.status_code", "ERROR");
                    writeActivity.SetTag("otel.status_description", ex.Message);
                }
            }
            catch { }
            result = new CommitResult { Success = false, Error = ex };
        }

        item.Completion?.TrySetResult(result);
        FireOnCommitted(op, result);
    void FireOnCommitted(WriteOperation op, CommitResult result)
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
                _logger.LogError(ex, "OnCommitted callback failed for {OpId}", op.Id);
            }
        });
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
        var docArtifactId = docRec.ArtifactId is { } da && artifactIdMap.TryGetValue(da, out var newDa) ? newDa : docRec.ArtifactId;
        var docNode = new Node
        {
            Id = docRec.Id,
            Kind = "document",
            Uri = op.Uri,
            ArtifactId = docArtifactId,
            SpanId = null,
            Props = EnrichDocPropsWithFrontmatter(docRec.Props) ?? new System.Text.Json.Nodes.JsonObject(),
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

        return Task.CompletedTask;
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
