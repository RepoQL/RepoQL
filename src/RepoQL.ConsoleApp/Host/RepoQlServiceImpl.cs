using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Core;
using CorePipelineSnapshot = RepoQL.Core.PipelineSnapshot;
using CorePipelineStage = RepoQL.Core.PipelineStage;
using CorePipelineStageSnapshot = RepoQL.Core.PipelineStageSnapshot;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using ProtoPipelineSnapshot = RepoQL.Contracts.PipelineSnapshot;
using ProtoPipelineStage = RepoQL.Contracts.PipelineStage;
using ProtoPipelineStageStatus = RepoQL.Contracts.PipelineStageStatus;

namespace RepoQL.ConsoleApp.Host;

public sealed class RepoQlServiceImpl(
    IGraphStore store,
    RepositoryConfiguration repoConfig,
    IInitialIndexingBarrier barrier,
    IRepositoryIndexer indexer,
    IMultiFileSystem fileSystem,
    IUriFilter uriFilter,
    IDatabaseWriter writer) : Contracts.RepoQL.RepoQLBase
{
    private static int GetEnvInt(string name, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    public override async Task<RawQueryResponse> ExecuteRawQuery(RawQueryRequest request, ServerCallContext context)
    {
        await barrier.InitialScanCompleted.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        var resp = new RawQueryResponse();
        try
        {
            var parameters = request.Parameters.Select(FromProtoValue).ToArray();
            var rows = store.RawQuery(request.Sql, parameters);

            var limited = request.Limit > 0;
            var take = limited ? rows.Take(request.Limit) : rows;

            IReadOnlyDictionary<string, object?>? first = null;
            foreach (var r in take)
            {
                if (first is null)
                {
                    first = r;
                    foreach (var col in first.Keys)
                    {
                        var sample = first[col];
                        resp.Columns.Add(new ColumnSchema { Name = col, DbType = InferDbType(sample) });
                    }
                }
                var rd = new RowData();
                foreach (var col in first.Keys)
                {
                    r.TryGetValue(col, out var value);
                    rd.Values.Add(ToProtoValue(value));
                }
                resp.Rows.Add(rd);
            }
            resp.RowCount = resp.Rows.Count;
            resp.Truncated = limited && (first is not null) && (store.RawQuery(request.Sql, parameters).Skip(resp.Rows.Count).Any());
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        return resp;
    }

    public override async Task<ClientLeaseSummary> HoldClientLease(IAsyncStreamReader<ClientLeaseBeat> requestStream, ServerCallContext context)
    {
        string? clientId = null;
        try
        {
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var beat = requestStream.Current;
                clientId ??= string.IsNullOrWhiteSpace(beat.ClientId) ? Guid.NewGuid().ToString() : beat.ClientId;
                var beatAt = ParseRfc3339OrUtcNow(beat.BeatAt);
                LeaseRegistry.Upsert(clientId, beatAt);
            }
        }
        catch (OperationCanceledException) { }
        finally { if (!string.IsNullOrWhiteSpace(clientId)) LeaseRegistry.Remove(clientId!); }

        var state = context.GetHttpContext().RequestServices.GetRequiredService<HostState>();
        return new ClientLeaseSummary
        {
            ServerStartedAt = state.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ImplicitStart = state.ImplicitStart,
            ActiveClients = LeaseRegistry.Count,
            ShutdownAfterIdleSeconds = GetEnvInt("REPOQL_IDLE_GRACE_SECONDS", 45)
        };
    }

    public override async Task<GetDocumentSummariesResponse> GetDocumentSummaries(GetDocumentSummariesRequest request, ServerCallContext context)
    {
        await barrier.InitialScanCompleted.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        var resp = new GetDocumentSummariesResponse();
        var kinds = request.Kinds.Count > 0 ? request.Kinds.Select(k => k.Trim()).ToArray() : ["outline"];
        var minSeverity = string.IsNullOrWhiteSpace(request.MinSeverity) ? null : request.MinSeverity.Trim();
        var includeData = request.IncludeData;
        var includeMessage = request.IncludeMessage;
        var includeResolvedTargetUri = request.IncludeResolvedTargetUri;

        foreach (var u in request.Uris)
        {
            var result = new DocumentSummaryResult { Uri = u };
            try
            {
                var canonicalUri = CanonicalizeRepositoryUri(u, repoConfig.Path);
                var sql = @"SELECT kind, severity, source, rule_id, message,
                                   CASE WHEN ? THEN data ELSE NULL END AS data,
                                   CASE WHEN ? THEN resolved_target_uri ELSE NULL END AS resolved_target_uri,
                                   target_node_id, target_edge_id, target_span_id, created_at, expires_at
                            FROM annotations_for(?, ?, ?)";
                var rows = store.RawQuery(sql, includeData, includeResolvedTargetUri, canonicalUri,
                    string.Join(',', kinds), minSeverity ?? (object)DBNull.Value);

                var any = false;
                foreach (var row in rows)
                {
                    any = true;
                    var ann = new SummaryAnnotation
                    {
                        Kind = row["kind"]?.ToString() ?? string.Empty,
                        Severity = row["severity"]?.ToString() ?? string.Empty,
                        Source = row["source"]?.ToString() ?? string.Empty,
                        RuleId = row["rule_id"]?.ToString() ?? string.Empty,
                    };
                    if (includeMessage)
                        ann.Message = row["message"]?.ToString() ?? string.Empty;
                    if (includeData && row.TryGetValue("data", out var d) && d is not null)
                    {
                        if (d is string sjson)
                        {
                            ann.Data = ParseStruct(sjson);
                        }
                        else
                        {
                            ann.Data = ParseStruct(SerializeToJson(d));
                        }
                    }
                    if (includeResolvedTargetUri)
                        ann.ResolvedTargetUri = row["resolved_target_uri"]?.ToString() ?? string.Empty;

                    if (row.TryGetValue("target_node_id", out var tn) && tn != null) ann.TargetNodeId = tn.ToString();
                    if (row.TryGetValue("target_edge_id", out var te) && te != null) ann.TargetEdgeId = te.ToString();
                    if (row.TryGetValue("target_span_id", out var ts) && ts != null) ann.TargetSpanId = ts.ToString();

                    if (row.TryGetValue("created_at", out var ca) && ca is DateTime cadt) ann.CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(cadt, DateTimeKind.Utc));
                    if (row.TryGetValue("expires_at", out var ea) && ea is DateTime eadt) ann.ExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(eadt, DateTimeKind.Utc));

                    result.Annotations.Add(ann);
                }
                if (!any)
                {
                    const string existsSql = "SELECT 1 FROM node WHERE lower(uri)=lower(repository_uri_container(?)) LIMIT 1";
                    var exists = store.RawQuery(existsSql, canonicalUri).Any();
                    result.Status = exists ? SummaryStatus.Ok : SummaryStatus.NotFound;
                }
                else
                {
                    result.Status = SummaryStatus.Ok;
                }
            }
            catch (Exception ex)
            {
                result.Status = SummaryStatus.Error;
                result.Error = ex.Message;
            }
            resp.Results.Add(result);
        }

        return resp;
    }

    private static string CanonicalizeRepositoryUri(string input, string repoRoot)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var u) && u.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = u.LocalPath;
            if (string.IsNullOrEmpty(u.Host))
            {
                var relOnly = localPath.TrimStart('/', '\\');
                if (!string.IsNullOrEmpty(relOnly)) return $"file:///{relOnly.Replace('\\', '/')}";
            }
            try
            {
                var root = Path.GetFullPath(repoRoot);
                var full = Path.GetFullPath(localPath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                    return $"file:///{rel}";
                }
            }
            catch { }
            return u.AbsoluteUri;
        }
        return input;
    }

    private static DateTime ParseRfc3339OrUtcNow(string s)
    {
        if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToUniversalTime();
        return DateTime.UtcNow;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "JSON serialization for dynamic data structures; fallback serialization")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JSON serialization for dynamic data structures; fallback serialization")]
    private static string SerializeToJson(object obj)
    {
        return JsonSerializer.Serialize(obj);
    }

    private static Struct ParseStruct(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ToStruct(doc.RootElement);
    }
    private static Struct ToStruct(JsonElement el)
    {
        var s = new Struct();
        if (el.ValueKind != JsonValueKind.Object) return s;
        foreach (var prop in el.EnumerateObject()) s.Fields[prop.Name] = ToValue(prop.Value);
        return s;
    }
    private static IEnumerable<Value> ToList(JsonElement el)
    {
        foreach (var item in el.EnumerateArray())
            yield return ToValue(item);
    }

    private static Value ToValue(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return Value.ForNull();
            case JsonValueKind.True:
                return Value.ForBool(true);
            case JsonValueKind.False:
                return Value.ForBool(false);
            case JsonValueKind.Number:
                return Value.ForNumber(el.TryGetInt64(out var i) ? i : el.GetDouble());
            case JsonValueKind.String:
                return Value.ForString(el.GetString() ?? string.Empty);
            case JsonValueKind.Object:
                return Value.ForStruct(ToStruct(el));
            case JsonValueKind.Array:
            {
                var lv = new ListValue();
                lv.Values.AddRange(ToList(el));
                return new Value { ListValue = lv };
            }
            default:
                return Value.ForNull();
        }
    }

    private static object? FromProtoValue(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.NullValue => DBNull.Value,
        Value.KindOneofCase.BoolValue => v.BoolValue,
        Value.KindOneofCase.NumberValue => v.NumberValue,
        Value.KindOneofCase.StringValue => v.StringValue,
        Value.KindOneofCase.StructValue => JsonFormatter.Default.Format(v),
        Value.KindOneofCase.ListValue => JsonFormatter.Default.Format(v),
        _ => DBNull.Value
    };

    private static Value ToProtoValue(object? value) => value switch
    {
        null => Value.ForNull(),
        DBNull => Value.ForNull(),
        bool b => Value.ForBool(b),
        byte b => Value.ForNumber(b),
        sbyte sb => Value.ForNumber(sb),
        short s => Value.ForNumber(s),
        ushort us => Value.ForNumber(us),
        int i => Value.ForNumber(i),
        uint ui => Value.ForNumber(ui),
        long l => Value.ForNumber(l),
        ulong ul => Value.ForNumber((double)ul),
        float f => Value.ForNumber(f),
        double d => Value.ForNumber(d),
        decimal dec => Value.ForNumber((double)dec),
        string s => Value.ForString(s),
        DateTime dt => Value.ForString(dt.ToString("O")),
        Guid g => Value.ForString(g.ToString()),
        byte[] bytes => Value.ForString(Convert.ToBase64String(bytes)),
        _ => Value.ForString(value.ToString() ?? string.Empty)
    };

    private static string InferDbType(object? sample)
    {
        if (sample is null || sample is DBNull) return "UNKNOWN";
        return sample switch
        {
            bool => "BOOLEAN",
            byte or sbyte or short or ushort or int => "INTEGER",
            uint or long => "BIGINT",
            ulong => "UBIGINT",
            float or double or decimal => "DOUBLE",
            DateTime => "TIMESTAMP",
            Guid => "UUID",
            byte[] => "BLOB",
            _ => "VARCHAR"
        };
    }


    public override async Task ReindexAll(ReindexRequest request, IServerStreamWriter<ReindexProgress> responseStream, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        await responseStream.WriteAsync(new ReindexProgress { Phase = "preparing", Total = 0, Completed = 0 }).ConfigureAwait(false);

        var uris = new List<RepoUri>(capacity: 4096);
        await foreach (var entry in fileSystem.EnumerateAsync(context.CancellationToken).ConfigureAwait(false))
        {
            if (!uriFilter.IncludeFile(entry.Uri))
                continue;
            uris.Add(entry.Uri);
        }

        var total = uris.Count;
        await responseStream.WriteAsync(new ReindexProgress { Phase = "enumerated", Total = total, Completed = 0 }).ConfigureAwait(false);

        using var reindexScope = indexer.EnterReindexScope();
        var baseline = indexer.GetPipelineSnapshot();
        await PublishStageProgressAsync(responseStream, baseline, baseline, total, context.CancellationToken).ConfigureAwait(false);

        await indexer.QueueForIndexingAsync(uris, skipUnchanged: false).ConfigureAwait(false);
        var waitTask = indexer.WaitForIdle(context.CancellationToken);

        var throttle = TimeSpan.FromMilliseconds(250);
        var lastSent = baseline;
        var lastSentAt = Stopwatch.StartNew();

        while (!waitTask.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), context.CancellationToken).ConfigureAwait(false);

            if (lastSentAt.Elapsed < throttle)
                continue;

            var snapshot = indexer.GetPipelineSnapshot();
            if (HasProgressChanged(snapshot, lastSent))
            {
                await PublishStageProgressAsync(responseStream, snapshot, baseline, total, context.CancellationToken).ConfigureAwait(false);
                lastSent = snapshot;
            }

            lastSentAt.Restart();
        }

        await waitTask.ConfigureAwait(false);

        var finalSnapshot = indexer.GetPipelineSnapshot();
        if (HasProgressChanged(finalSnapshot, lastSent))
        {
            await PublishStageProgressAsync(responseStream, finalSnapshot, baseline, total, context.CancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(context.CancellationToken).ConfigureAwait(false);
        await responseStream.WriteAsync(new ReindexProgress { Phase = "completed", Total = total, Completed = total }).ConfigureAwait(false);
    }

    public override async Task<WaitForPipelineResponse> WaitForPipeline(WaitForPipelineRequest request, ServerCallContext context)
    {
        var stages = ToCoreStageMask(request.Stages);
        if (stages == CorePipelineStage.None)
            stages = CorePipelineStage.All;

        if (request.WaitAll)
            await indexer.WaitForStagesIdleAsync(stages, context.CancellationToken).ConfigureAwait(false);
        else
            await indexer.WaitForAnyStageIdleAsync(stages, context.CancellationToken).ConfigureAwait(false);

        var snapshot = indexer.GetPipelineSnapshot();
        return new WaitForPipelineResponse { Snapshot = ToProtoSnapshot(snapshot) };
    }

    private sealed class Observer(System.Action completed, System.Action<System.Exception> error, System.Action<IndexerEvent> next) : IObserver<IndexerEvent>
    {
        public void OnCompleted() => completed();
        public void OnError(System.Exception err) => error(err);
        public void OnNext(IndexerEvent value) => next(value);
    }

    private static bool HasProgressChanged(CorePipelineSnapshot current, CorePipelineSnapshot previous)
        => current.Discovery.Completed != previous.Discovery.Completed
           || current.Parsing.Completed != previous.Parsing.Completed
           || current.Analysis.Completed != previous.Analysis.Completed;

    private static async Task PublishStageProgressAsync(
        IServerStreamWriter<ReindexProgress> responseStream,
        CorePipelineSnapshot snapshot,
        CorePipelineSnapshot baseline,
        int total,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await responseStream.WriteAsync(new ReindexProgress
        {
            Phase = "Classified",
            Total = total,
            Completed = ClampCompleted(snapshot.Discovery, baseline.Discovery, total)
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await responseStream.WriteAsync(new ReindexProgress
        {
            Phase = "Parsed",
            Total = total,
            Completed = ClampCompleted(snapshot.Parsing, baseline.Parsing, total)
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await responseStream.WriteAsync(new ReindexProgress
        {
            Phase = "Analyzed",
            Total = total,
            Completed = ClampCompleted(snapshot.Analysis, baseline.Analysis, total)
        }).ConfigureAwait(false);
    }

    private static long ClampCompleted(CorePipelineStageSnapshot current, CorePipelineStageSnapshot baseline, int total)
    {
        var delta = current.Completed - baseline.Completed;
        if (delta < 0) delta = 0;
        if (total >= 0)
        {
            var max = (long)total;
            if (delta > max)
                delta = max;
        }
        return delta;
    }

    private static CorePipelineStage ToCoreStageMask(IEnumerable<ProtoPipelineStage> stages)
    {
        var mask = CorePipelineStage.None;
        foreach (var stage in stages)
        {
            mask |= stage switch
            {
                ProtoPipelineStage.Discovery => CorePipelineStage.Discovery,
                ProtoPipelineStage.Parsing => CorePipelineStage.Parsing,
                ProtoPipelineStage.Analysis => CorePipelineStage.Analysis,
                ProtoPipelineStage.Writer => CorePipelineStage.Writer,
                _ => CorePipelineStage.None
            };
        }
        return mask;
    }

    private static ProtoPipelineSnapshot ToProtoSnapshot(CorePipelineSnapshot snapshot)
    {
        var proto = new ProtoPipelineSnapshot
        {
            CapturedAt = Timestamp.FromDateTimeOffset(snapshot.CapturedAt),
            Reindexing = snapshot.IsReindexing,
            WriterPending = snapshot.WriterPending
        };
        proto.Stages.Add(ToProtoStage(snapshot.Discovery));
        proto.Stages.Add(ToProtoStage(snapshot.Parsing));
        proto.Stages.Add(ToProtoStage(snapshot.Analysis));
        if (snapshot.Writer is not null)
            proto.Stages.Add(ToProtoStage(snapshot.Writer));
        return proto;
    }

    private static ProtoPipelineStageStatus ToProtoStage(CorePipelineStageSnapshot stage)
        => new()
        {
            Stage = stage.Stage switch
            {
                CorePipelineStage.Discovery => ProtoPipelineStage.Discovery,
                CorePipelineStage.Parsing => ProtoPipelineStage.Parsing,
                CorePipelineStage.Analysis => ProtoPipelineStage.Analysis,
                CorePipelineStage.Writer => ProtoPipelineStage.Writer,
                _ => ProtoPipelineStage.Unspecified
            },
            Depth = stage.Depth,
            Capacity = stage.Capacity,
            Scheduled = stage.Scheduled,
            Completed = stage.Completed
        };
}
