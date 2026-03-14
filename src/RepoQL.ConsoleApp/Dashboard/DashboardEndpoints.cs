using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Diagnostics;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Hosting;

namespace RepoQL.ConsoleApp.Dashboard;

/// <summary>
/// Maps dashboard HTTP endpoints: GET /api/snapshot (JSON) and GET /api/events (SSE).
/// Purpose: Bridge host-internal observability to the embedded React dashboard.
/// Complexity: Composes real file data from UriRegistry with pipeline status and operations.
/// SSE streams StatusEventAggregator events plus file deltas and periodic lease/operation snapshots.
/// </summary>
internal static class DashboardEndpoints
{
    private const double SlowItemThresholdMs = 15_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/snapshot", HandleSnapshot);
        app.MapGet("/api/events", HandleEvents);
    }

    // --- Snapshot endpoint ---

    private static IResult HandleSnapshot(
        IIndexingCoordinator coordinator,
        IOperationManager operations,
        UriRegistry uriRegistry,
        HostState hostState,
        DuckDbDataStore dataStore,
        DashboardQueryActivityTracker queryActivity,
        IIndexingDiagnosticsProvider diagnostics)
    {
        var pipelineStatus = coordinator.GetPipelineStatus();
        var leases = LeaseRegistry.Snapshot();
        var activeOps = operations.ActiveOperations;

        // Load token counts from artifact table — keyed by node URI.
        var tokenCounts = LoadTokenCounts(dataStore);

        var snapshot = new
        {
            host = new
            {
                repositoryPath = hostState.RepositoryPath,
                startedAt = hostState.StartedAtUtc,
                dashboardUrl = hostState.DashboardUrl,
                initialIndexingCompleted = hostState.InitialIndexingCompleted,
            },
            pipeline = new
            {
                stages = pipelineStatus.Stages.Select(s => new
                {
                    name = s.Stage.ToString(),
                    busy = s.Busy,
                    queued = s.Queued,
                    inProgress = s.InProgress,
                }).ToArray(),
                reindexing = pipelineStatus.IsReindexing,
                writerPending = pipelineStatus.WriterPending,
            },
            leases = leases.Select(l => new
            {
                clientId = l.ClientId,
                lastBeatUtc = l.LastBeatUtc,
            }).ToArray(),
            operations = activeOps.Select(op => new
            {
                id = op.Id,
                description = op.Description,
                state = op.State.ToString(),
                createdAt = op.CreatedAt,
                progress = new
                {
                    totalFiles = op.Progress.TotalFiles,
                    indexedCount = op.Progress.IndexedCount,
                    embeddedCount = op.Progress.EmbeddedCount,
                    failedCount = op.Progress.FailedCount,
                    readyPercent = op.Progress.ReadyPercent,
                },
            }).ToArray(),
            queries = SnapshotQueries(queryActivity),
            indexing = SnapshotIndexing(diagnostics),
            files = SnapshotFiles(uriRegistry, tokenCounts),
        };

        return Results.Json(snapshot, JsonOptions);
    }

    // --- Token counts from DuckDB ---

    private static readonly Dictionary<string, int> EmptyTokenCounts = new();

    private static Dictionary<string, int> LoadTokenCounts(DuckDbDataStore dataStore)
    {
        try
        {
            // Use a cancellation token with timeout to avoid blocking during heavy writes.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var task = Task.Run(() => dataStore.Read(
                "SELECT n.uri, a.token_count FROM node n JOIN artifact a ON n.artifact_id = a.id WHERE a.token_count > 0 AND n.kind = 'document'",
                r => (uri: r.GetString(0), tokens: r.GetInt32(1))), cts.Token);

            if (!task.Wait(500))
                return EmptyTokenCounts;

            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (uri, tokens) in task.Result)
                dict[uri] = tokens;
            return dict;
        }
        catch
        {
            return EmptyTokenCounts;
        }
    }


    private static object[] SnapshotQueries(DashboardQueryActivityTracker queryActivity)
    {
        return queryActivity.CaptureSnapshot(DateTime.UtcNow)
            .Select(entry => new
            {
                id = entry.Id,
                tool = entry.Tool,
                @params = entry.Parameters,
                tokenBudget = entry.TokenBudget,
                tokensUsed = entry.TokensUsed,
                elapsedMs = entry.ElapsedMs,
                resultSummary = entry.ResultSummary,
                timestampUtc = entry.TimestampUtc,
                state = entry.State.ToString().ToLowerInvariant(),
            })
            .ToArray();
    }
    // --- File snapshot from UriRegistry ---

    private static object[] SnapshotFiles(UriRegistry registry, Dictionary<string, int> tokenCounts)
    {
        return registry
            .Where(kvp => kvp.Key.Scheme is "file" or "github")
            .OrderBy(kvp => kvp.Key.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(kvp => BuildFileDto(kvp.Key, kvp.Value, tokenCounts))
            .ToArray();
    }

    /// <summary>
    /// Builds a dictionary-based DTO for a file entry.
    /// Uses Dictionary instead of anonymous types for trim-safe JSON serialization.
    /// </summary>
    private static object BuildFileDto(RepoUri uri, FileEntry entry, Dictionary<string, int> tokenCounts)
    {
        var path = uri.Scheme == "file"
            ? uri.AbsolutePath.TrimStart('/')
            : uri.ToString();
        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        var (state, processing) = MapFileState(entry);
        tokenCounts.TryGetValue(uri.ToString(), out var tokens);

        var dto = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["ext"] = ext,
            ["state"] = state,
            ["processing"] = processing,
        };

        if (entry.LineCount > 0) dto["lines"] = entry.LineCount;
        if (tokens > 0) dto["tokens"] = tokens;
        if (entry.Symbols.Count > 0) dto["symbols"] = entry.Symbols.Count;
        if (entry.EmbeddedChunkCount > 0) dto["chunks"] = entry.EmbeddedChunkCount;
        if (entry.IndexedAt.HasValue) dto["indexedAt"] = entry.IndexedAt.Value.ToString("O");
        if (entry.EmbeddedAt.HasValue) dto["embeddedAt"] = entry.EmbeddedAt.Value.ToString("O");
        if ((entry.Status == UriStatus.Failed || entry.Status == UriStatus.Skipped) && entry.Error is not null) dto["error"] = entry.Error;
        if (entry.Headline is not null) dto["headline"] = entry.Headline;
        if (entry.Structure is not null) dto["structure"] = entry.Structure;
        if (entry.Symbols.Count > 0) dto["tree"] = BuildSymbolTree(entry.Symbols);

        return dto;
    }

    /// <summary>
    /// Builds a compact symbol tree for tooltip display.
    /// Returns top-level containers (types) with member counts,
    /// plus any standalone functions/declarations.
    /// </summary>
    private static object[] BuildSymbolTree(IReadOnlyDictionary<RepoUri, SymbolEntry> symbols)
    {
        // Extract symbol info: name from URI fragment, kind, span.
        var all = symbols
            .Select(kvp =>
            {
                var name = ExtractSymbolName(kvp.Key);
                return new { name, kvp.Value.Kind, kvp.Value.StartLine, kvp.Value.EndLine };
            })
            .Where(s => !string.IsNullOrEmpty(s.name))
            .OrderBy(s => s.StartLine)
            .ToList();

        if (all.Count == 0) return [];

        // Identify containers (types) vs members.
        var isType = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
        {
            if (s.Kind.Contains("type", StringComparison.OrdinalIgnoreCase))
                isType.Add(s.name);
        }

        var result = new List<object>();

        foreach (var s in all)
        {
            if (isType.Contains(s.name))
            {
                // Container: count direct members.
                var prefix = s.name + ".";
                var memberCount = all.Count(m =>
                    m.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && m.name.IndexOf('.', prefix.Length) < 0);

                result.Add(new
                {
                    n = ShortName(s.name),
                    k = SimplifyKind(s.Kind),
                    l = s.StartLine > 0 ? s.StartLine : (int?)null,
                    e = s.EndLine > 0 ? s.EndLine : (int?)null,
                    m = memberCount > 0 ? memberCount : (int?)null,
                });
            }
            else
            {
                // Skip members of known types — they're counted above.
                var isMember = isType.Any(t =>
                    s.name.StartsWith(t + ".", StringComparison.OrdinalIgnoreCase));
                if (isMember) continue;

                // Standalone symbol (top-level function, variable, etc.).
                result.Add(new
                {
                    n = ShortName(s.name),
                    k = SimplifyKind(s.Kind),
                    l = s.StartLine > 0 ? s.StartLine : (int?)null,
                    e = s.EndLine > 0 ? s.EndLine : (int?)null,
                    m = (int?)null,
                });
            }

            if (result.Count >= 10) break;
        }

        return result.ToArray();
    }

    private static string ExtractSymbolName(RepoUri uri)
    {
        var fragment = uri.Fragment;
        if (string.IsNullOrEmpty(fragment)) return string.Empty;

        // Fragment: #line=42,100&symbol=Name or #symbol=Name
        const string marker = "symbol=";
        var idx = fragment.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;

        return Uri.UnescapeDataString(fragment.Substring(idx + marker.Length));
    }

    private static string ShortName(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return dot >= 0 ? qualified.Substring(dot + 1) : qualified;
    }

    private static string SimplifyKind(string kind)
    {
        // "csharp.type" → "type", "typescript.member" → "member"
        var dot = kind.LastIndexOf('.');
        return dot >= 0 ? kind.Substring(dot + 1) : kind;
    }

    private static (string state, bool processing) MapFileState(FileEntry entry)
    {
        if (entry.Status == UriStatus.Failed || entry.Status == UriStatus.Skipped)
            return ("failed", false);

        return entry.Status switch
        {
            UriStatus.Discovered => ("discovered", false),
            UriStatus.Stale => ("discovered", false),
            UriStatus.Indexing => ("classified", true),
            UriStatus.Indexed => entry.EmbeddingStatus switch
            {
                EmbeddingStatus.Pending => ("parsed", false),
                EmbeddingStatus.Embedding => ("struct_embedded", true),
                EmbeddingStatus.Embedded => ("full_embedded", false),
                EmbeddingStatus.NotApplicable => ("full_embedded", false),
                EmbeddingStatus.Failed => ("failed", false),
                _ => ("parsed", false)
            },
            _ => ("discovered", false)
        };
    }

    // --- SSE endpoint ---

    private static async Task HandleEvents(
        HttpContext context,
        StatusEventAggregator aggregator,
        UriRegistry uriRegistry,
        IOperationManager operations,
        DuckDbDataStore dataStore,
        DashboardQueryActivityTracker queryActivity,
        IIndexingDiagnosticsProvider diagnostics,
        CancellationToken cancellationToken)
    {
        // Disable response buffering for real-time streaming.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var lastPeriodicSend = DateTime.UtcNow;
        var periodicSnapshotInterval = TimeSpan.FromSeconds(2);
        var lastSentEntries = new Dictionary<string, RepoQL.Contracts.FileEntry>(StringComparer.OrdinalIgnoreCase);
        var lastDeltaHadChanges = false;
        var lastPeriodicFilesSend = DateTime.UtcNow;
        var tokenCounts = LoadTokenCounts(dataStore);
        var lastTokenRefresh = DateTime.UtcNow;
        var events = Channel.CreateUnbounded<StatusEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        await WritePeriodicSnapshots(context.Response, operations, queryActivity, diagnostics, cancellationToken).ConfigureAwait(false);

        using var heartbeat = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var eventPump = PumpStatusEventsAsync(aggregator, events.Writer, cancellationToken);
        var readTask = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var heartbeatTask = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();

        while (!cancellationToken.IsCancellationRequested)
        {
            var completedTask = await Task.WhenAny(readTask, heartbeatTask).ConfigureAwait(false);

            if (completedTask == readTask)
            {
                if (!await readTask.ConfigureAwait(false))
                {
                    break;
                }

                while (events.Reader.TryRead(out var evt))
                {
                    await WriteStatusEvent(context.Response, evt, cancellationToken).ConfigureAwait(false);
                }

                readTask = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
            }
            else if (!await heartbeatTask.ConfigureAwait(false))
            {
                break;
            }
            else
            {
                heartbeatTask = heartbeat.WaitForNextTickAsync(cancellationToken).AsTask();
            }

            // Adaptive file deltas.
            var now = DateTime.UtcNow;

            // Refresh token counts every 10s (they change as artifacts are committed).
            if (now - lastTokenRefresh >= TimeSpan.FromSeconds(10))
            {
                tokenCounts = LoadTokenCounts(dataStore);
                lastTokenRefresh = now;
            }

            var fileSnapshotInterval = lastDeltaHadChanges
                ? TimeSpan.FromMilliseconds(150)
                : TimeSpan.FromSeconds(1);
            if (now - lastPeriodicFilesSend >= fileSnapshotInterval)
            {
                lastDeltaHadChanges = await WriteDeltaFiles(
                        context.Response,
                        uriRegistry,
                        lastSentEntries,
                        tokenCounts,
                        cancellationToken)
                    .ConfigureAwait(false);
                lastPeriodicFilesSend = now;
            }

            // Periodic snapshots: leases and operations.
            if (now - lastPeriodicSend >= periodicSnapshotInterval)
            {
                await WritePeriodicSnapshots(context.Response, operations, queryActivity, diagnostics, cancellationToken).ConfigureAwait(false);
                lastPeriodicSend = now;
            }
        }

        await eventPump.ConfigureAwait(false);
    }

    private static async Task PumpStatusEventsAsync(
        StatusEventAggregator aggregator,
        ChannelWriter<StatusEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in aggregator.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task WriteStatusEvent(HttpResponse response, StatusEvent evt, CancellationToken cancellationToken)
    {
        // Map StatusEvent to SSE event type + JSON data.
        string? eventType = null;
        string? data = null;

        switch (evt.EventCase)
        {
            case StatusEvent.EventOneofCase.Pipeline:
                eventType = "pipeline";
                data = JsonSerializer.Serialize(new
                {
                    reindexing = evt.Pipeline.Reindexing,
                    writerPending = evt.Pipeline.WriterPending,
                    ready = evt.Pipeline.Ready,
                    stages = evt.Pipeline.Stages.Select(s => new
                    {
                        name = s.Stage.ToString(),
                        busy = s.Busy,
                        queued = s.Queued,
                        inProgress = s.InProgress,
                        avgDurationMs = s.AvgDurationMs,
                        peakDurationMs = s.PeakDurationMs,
                        processedTotal = s.ProcessedTotal,
                        throughputPerSec = s.ThroughputPerSec,
                    }).ToArray(),
                }, JsonOptions);
                break;

            case StatusEvent.EventOneofCase.Activity:
                eventType = "activity";
                data = JsonSerializer.Serialize(new
                {
                    type = evt.Activity.Type.ToString(),
                    uri = evt.Activity.Uri,
                    message = evt.Activity.Message,
                    queuedCount = evt.Activity.QueuedCount,
                    processedCount = evt.Activity.ProcessedCount,
                }, JsonOptions);
                break;

            case StatusEvent.EventOneofCase.Health:
                eventType = "health";
                data = JsonSerializer.Serialize(new
                {
                    type = evt.Health.Type.ToString(),
                    message = evt.Health.Message,
                    severity = evt.Health.Severity.ToString(),
                }, JsonOptions);
                break;

            case StatusEvent.EventOneofCase.Stats:
                eventType = "stats";
                data = JsonSerializer.Serialize(new
                {
                    totalFiles = evt.Stats.TotalFiles,
                    totalNodes = evt.Stats.TotalNodes,
                    exploreCoveragePercent = evt.Stats.ExploreCoveragePercent,
                    embeddingsReady = evt.Stats.EmbeddingsReady,
                }, JsonOptions);
                break;
        }

        if (eventType is not null && data is not null)
        {
            await WriteSseEvent(response, eventType, data, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteSseEvent(
        HttpResponse response, string eventType, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventType}\ndata: {data}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> WriteDeltaFiles(
        HttpResponse response,
        UriRegistry uriRegistry,
        Dictionary<string, RepoQL.Contracts.FileEntry> lastSentEntries,
        Dictionary<string, int> tokenCounts,
        CancellationToken ct)
    {
        var updates = new List<object>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in uriRegistry
                     .Where(kvp => kvp.Key.Scheme is "file" or "github")
                     .OrderBy(kvp => kvp.Key.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var path = kvp.Key.Scheme == "file"
                ? kvp.Key.AbsolutePath.TrimStart('/')
                : kvp.Key.ToString();
            seenPaths.Add(path);

            if (lastSentEntries.TryGetValue(path, out var previousEntry) && ReferenceEquals(previousEntry, kvp.Value))
                continue;

            // Cap per-tick updates to prevent browser flooding during reindex.
            if (updates.Count >= 50)
                continue; // Will be picked up next tick (entry NOT updated in lastSentEntries)

            updates.Add(BuildFileDto(kvp.Key, kvp.Value, tokenCounts));

            lastSentEntries[path] = kvp.Value;
        }

        var removedPaths = lastSentEntries.Keys
            .Where(path => !seenPaths.Contains(path))
            .ToArray();

        foreach (var removedPath in removedPaths)
        {
            lastSentEntries.Remove(removedPath);
        }

        var wroteUpdates = updates.Count > 0;
        if (wroteUpdates)
        {
            await WriteSseEvent(
                response,
                "file_updates",
                JsonSerializer.Serialize(updates, JsonOptions),
                ct).ConfigureAwait(false);
        }

        var wroteRemovals = removedPaths.Length > 0;
        if (wroteRemovals)
        {
            await WriteSseEvent(
                response,
                "file_removes",
                JsonSerializer.Serialize(removedPaths, JsonOptions),
                ct).ConfigureAwait(false);
        }

        return wroteUpdates || wroteRemovals;
    }

    private static async Task WritePeriodicSnapshots(
        HttpResponse response,
        IOperationManager operations,
        DashboardQueryActivityTracker queryActivity,
        IIndexingDiagnosticsProvider diagnostics,
        CancellationToken ct)
    {
        await WriteLeaseSnapshot(response, ct).ConfigureAwait(false);
        await WriteOperationsSnapshot(response, operations, ct).ConfigureAwait(false);
        await WriteQuerySnapshot(response, queryActivity, ct).ConfigureAwait(false);
        await WriteIndexingSnapshot(response, diagnostics, ct).ConfigureAwait(false);
    }

    private static object SnapshotIndexing(IIndexingDiagnosticsProvider diagnostics)
    {
        var snapshot = diagnostics.GetSnapshot();
        var stuckItems = diagnostics.GetQueuedItems()
            .Where(item =>
                item.DeferredRetry
                || item.TimeoutAttempts > 0
                || (item.ElapsedMs ?? 0) >= SlowItemThresholdMs)
            .OrderByDescending(item => item.ElapsedMs ?? (DateTimeOffset.UtcNow - item.EnqueuedAt).TotalMilliseconds)
            .Take(8)
            .Select(item => new
            {
                uri = item.Uri,
                name = item.Name,
                stage = item.Stage,
                status = item.Status,
                enqueuedAt = item.EnqueuedAt,
                startedAt = item.StartedAt,
                elapsedMs = item.ElapsedMs,
                workerId = item.WorkerId,
                timeoutAttempts = item.TimeoutAttempts,
                deferredRetry = item.DeferredRetry,
                size = item.Size,
                mimeType = item.MimeType,
            })
            .ToArray();

        return new
        {
            hotPathTimeouts = snapshot.HotPathTimeouts,
            analysisTimeouts = snapshot.AnalysisTimeouts,
            deferredRetryTimeouts = snapshot.DeferredRetryTimeouts,
            deferredRetryPending = snapshot.DeferredRetryPending,
            deferredRetryActive = snapshot.DeferredRetryActive,
            deferredToIdleCount = snapshot.DeferredToIdleCount,
            activeWorkers = snapshot.ActiveWorkers.Select(worker => new
            {
                queue = worker.Queue,
                workerId = worker.WorkerId,
                uri = worker.Uri,
                name = worker.Name,
                stage = worker.Stage,
                startedAt = worker.StartedAt,
                elapsedMs = worker.ElapsedMs,
                timeoutAttempts = worker.TimeoutAttempts,
                deferredRetry = worker.DeferredRetry,
            }).ToArray(),
            stuckItems,
        };
    }

    private static async Task WriteLeaseSnapshot(HttpResponse response, CancellationToken ct)
    {
        // Leases.
        var leases = LeaseRegistry.Snapshot().Select(l => new
        {
            clientId = l.ClientId,
            lastBeatUtc = l.LastBeatUtc,
        });

        await WriteSseEvent(
            response,
            "leases",
            JsonSerializer.Serialize(leases, JsonOptions),
            ct).ConfigureAwait(false);
    }


    private static async Task WriteQuerySnapshot(
        HttpResponse response,
        DashboardQueryActivityTracker queryActivity,
        CancellationToken ct)
    {
        await WriteSseEvent(
            response,
            "queries",
            JsonSerializer.Serialize(SnapshotQueries(queryActivity), JsonOptions),
            ct).ConfigureAwait(false);
    }

    private static async Task WriteIndexingSnapshot(
        HttpResponse response,
        IIndexingDiagnosticsProvider diagnostics,
        CancellationToken ct)
    {
        await WriteSseEvent(
            response,
            "indexing",
            JsonSerializer.Serialize(SnapshotIndexing(diagnostics), JsonOptions),
            ct).ConfigureAwait(false);
    }
    private static async Task WriteOperationsSnapshot(
        HttpResponse response, IOperationManager operations, CancellationToken ct)
    {
        // Operations.
        var ops = operations.ActiveOperations.Select(op => new
        {
            id = op.Id,
            description = op.Description,
            state = op.State.ToString(),
            createdAt = op.CreatedAt,
            progress = new
            {
                totalFiles = op.Progress.TotalFiles,
                indexedCount = op.Progress.IndexedCount,
                embeddedCount = op.Progress.EmbeddedCount,
                failedCount = op.Progress.FailedCount,
                readyPercent = op.Progress.ReadyPercent,
            },
        });

        await WriteSseEvent(
            response,
            "operations",
            JsonSerializer.Serialize(ops, JsonOptions),
            ct).ConfigureAwait(false);
    }
}

