using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.ConsoleApp.Dashboard;

/// <summary>
/// Maps dashboard HTTP endpoints: GET /api/snapshot (JSON) and GET /api/events (SSE).
/// Purpose: Bridge host-internal observability to the embedded React dashboard.
/// Complexity: Composes real file data from UriRegistry with pipeline status and operations.
/// SSE streams StatusEventAggregator events plus periodic file/lease/operation snapshots.
/// </summary>
internal static class DashboardEndpoints
{
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
        HostState hostState)
    {
        var pipelineStatus = coordinator.GetPipelineStatus();
        var leases = LeaseRegistry.Snapshot();
        var activeOps = operations.ActiveOperations;

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
            files = SnapshotFiles(uriRegistry),
        };

        return Results.Json(snapshot, JsonOptions);
    }

    // --- File snapshot from UriRegistry ---

    private static object[] SnapshotFiles(UriRegistry registry)
    {
        return registry
            .Where(kvp => kvp.Key.Scheme == "file")
            .OrderBy(kvp => kvp.Key.AbsolutePath, StringComparer.OrdinalIgnoreCase)
            .Select(kvp =>
            {
                var entry = kvp.Value;
                var path = kvp.Key.AbsolutePath.TrimStart('/');
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var (state, processing) = MapFileState(entry);
                return (object)new
                {
                    path,
                    ext,
                    state,
                    processing,
                    lines = entry.LineCount > 0 ? entry.LineCount : (int?)null,
                    symbols = entry.Symbols.Count > 0 ? entry.Symbols.Count : (int?)null,
                    chunks = entry.EmbeddedChunkCount > 0 ? entry.EmbeddedChunkCount : (int?)null,
                    indexedAt = entry.IndexedAt?.ToString("O"),
                    embeddedAt = entry.EmbeddedAt?.ToString("O"),
                    error = entry.Status == UriStatus.Failed ? entry.Error : null,
                    tree = entry.Symbols.Count > 0 ? BuildSymbolTree(entry.Symbols) : null,
                };
            })
            .ToArray();
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
        if (entry.Status == UriStatus.Failed)
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
        CancellationToken cancellationToken)
    {
        // Disable response buffering for real-time streaming.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var lastPeriodicSend = DateTime.UtcNow;
        var periodicSnapshotInterval = TimeSpan.FromSeconds(2);

        await foreach (var evt in aggregator.WatchAsync(cancellationToken))
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
                await WriteSseEvent(context.Response, eventType, data, cancellationToken).ConfigureAwait(false);
            }

            // Periodic snapshots: files, leases, operations.
            var now = DateTime.UtcNow;
            if (now - lastPeriodicSend >= periodicSnapshotInterval)
            {
                await WritePeriodicSnapshots(context.Response, uriRegistry, operations, cancellationToken).ConfigureAwait(false);
                lastPeriodicSend = now;
            }
        }
    }

    private static async Task WriteSseEvent(
        HttpResponse response, string eventType, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventType}\ndata: {data}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WritePeriodicSnapshots(
        HttpResponse response, UriRegistry uriRegistry, IOperationManager operations, CancellationToken ct)
    {
        // Files — real state from UriRegistry.
        await WriteSseEvent(
            response,
            "files",
            JsonSerializer.Serialize(SnapshotFiles(uriRegistry), JsonOptions),
            ct).ConfigureAwait(false);

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
