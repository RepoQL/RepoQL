using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.FileSystems.Imports;
using RepoQL.Indexing.Hosting;
using ProtoPipelineStage = RepoQL.Contracts.PipelineStage;
using ProtoPipelineStatus = RepoQL.Contracts.PipelineStatus;
using ProtoStageStatus = RepoQL.Contracts.StageStatus;

namespace RepoQL.ConsoleApp.Host;

public sealed class RepoQlServiceImpl : Contracts.RepoQL.RepoQLBase
{
    private readonly IGraphStore store;
    private readonly RepositoryConfiguration repoConfig;
    private readonly IInitialIndexingBarrier barrier;
    private readonly IIndexingCoordinator coordinator;
    private readonly IFileSystemImportService importService;
    private readonly DocumentPreviewService _previewService;
    private readonly IDatabaseWriter writer;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly ILogger<RepoQlServiceImpl> _logger;
    private static readonly JsonSerializerOptions PreviewJsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static int GetEnvInt(string name, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    public RepoQlServiceImpl(
        IGraphStore store,
        RepositoryConfiguration repoConfig,
        IInitialIndexingBarrier barrier,
        IIndexingCoordinator coordinator,
        IFileSystemImportService importService,
        DocumentPreviewService previewService,
        IDatabaseWriter writer,
        IHostApplicationLifetime hostLifetime,
        IEmbeddingProvider? embeddingProvider = null,
        ILogger<RepoQlServiceImpl>? logger = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));
        this.barrier = barrier ?? throw new ArgumentNullException(nameof(barrier));
        this._embeddingProvider = embeddingProvider;
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _hostLifetime = hostLifetime ?? throw new ArgumentNullException(nameof(hostLifetime));
        _logger = logger ?? NullLogger<RepoQlServiceImpl>.Instance;
        // Note: Schema version checking is now handled by DuckDbGraphStore.EnsureSchema()
    }

    public override Task<RawQueryResponse> ExecuteRawQuery(RawQueryRequest request, ServerCallContext context)
    {
        // No barrier - queries execute immediately with whatever data is available.
        // XrayTool handles "call again to wait" pattern for semantic readiness.
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
        return Task.FromResult(resp);
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

    public override async Task<ShutdownHostResponse> ShutdownHost(ShutdownHostRequest request, ServerCallContext context)
    {
        var pid = Environment.ProcessId;
        _logger.LogInformation("Shutdown requested by {Peer}; process id {Pid}", context.Peer, pid);

        var response = new ShutdownHostResponse { ProcessId = pid };

        // Ensure response is sent before initiating shutdown
        // This prevents the gRPC connection from closing before the response is transmitted
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for response to be transmitted (gRPC flushes on method return)
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch
            {
            }

            _hostLifetime.StopApplication();
        });

        return response;
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

    public override async Task<PreviewDocumentResponse> PreviewDocument(PreviewDocumentRequest request, ServerCallContext context)
    {
        await barrier.InitialScanCompleted.WaitAsync(context.CancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(request.Uri))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "uri is required."));

        var canonicalUri = CanonicalizeRepositoryUri(request.Uri, repoConfig.Path);
        if (!RepoUri.TryParse(canonicalUri, out var repoUri))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "uri must be an absolute repository uri."));

        var contentBytes = request.Content is { Length: > 0 } ? request.Content.ToByteArray() : null;
        var previewRequest = new DocumentPreviewRequest(
            repoUri!,
            contentBytes,
            string.IsNullOrWhiteSpace(request.FileName) ? null : request.FileName,
            string.IsNullOrWhiteSpace(request.MediaTypeHint) ? null : request.MediaTypeHint);

        var result = await _previewService.PreviewAsync(previewRequest, context.CancellationToken).ConfigureAwait(false);
        var response = new PreviewDocumentResponse
        {
            Success = result.Success,
            Error = result.Error ?? string.Empty,
            MediaType = result.MediaType ?? string.Empty,
            DigestHex = result.DigestHex ?? string.Empty
        };

        if (result.Records is not null)
        {
            response.Records = MapPreviewRecords(result.Records);
        }

        foreach (var stage in result.Stages)
        {
            response.Stages.Add(new PreviewStageTiming
            {
                Stage = stage.Stage,
                DurationMs = (long)Math.Round(stage.Duration.TotalMilliseconds),
                Status = stage.Status.ToString(),
                Error = stage.Error ?? string.Empty
            });
        }

        return response;
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

    private static PreviewRecords MapPreviewRecords(Records records)
    {
        var proto = new PreviewRecords();

        if (records.Artifacts is { Length: > 0 })
        {
            foreach (var artifact in records.Artifacts)
            {
                proto.Artifacts.Add(new PreviewArtifact
                {
                    Id = artifact.Id.ToString(),
                    Digest = artifact.Digest ?? string.Empty,
                    SizeBytes = artifact.Size,
                    MediaType = artifact.MediaType?.ToString() ?? string.Empty,
                    Headline = artifact.Headline ?? string.Empty,
                    Summary = artifact.Summary ?? string.Empty,
                    Structure = artifact.Structure ?? string.Empty,
                    StoreUri = artifact.StoreUri?.ToString() ?? string.Empty
                });
            }
        }

        if (records.Nodes is { Length: > 0 })
        {
            foreach (var node in records.Nodes)
            {
                proto.Nodes.Add(new PreviewNode
                {
                    Id = node.Id.ToString(),
                    Kind = node.Kind ?? string.Empty,
                    Uri = node.Uri?.ToString() ?? string.Empty,
                    ArtifactId = node.ArtifactId?.ToString() ?? string.Empty,
                    SpanId = node.SpanId?.ToString() ?? string.Empty,
                    Headline = node.Headline ?? string.Empty,
                    Structure = node.Structure ?? string.Empty,
                    PropsJson = JsonSerializer.Serialize(node.Props ?? new System.Text.Json.Nodes.JsonObject(), PreviewJsonOptions)
                });
            }
        }

        if (records.Spans is { Length: > 0 })
        {
            foreach (var span in records.Spans)
            {
                proto.Spans.Add(new PreviewSpan
                {
                    Id = span.Id.ToString(),
                    DocumentId = span.DocumentId.ToString(),
                    StartByte = span.StartByte ?? 0,
                    EndByte = span.EndByte ?? 0,
                    StartLine = span.StartLine ?? 0,
                    EndLine = span.EndLine ?? 0,
                    StartColumn = span.StartColumn ?? 0,
                    EndColumn = span.EndColumn ?? 0
                });
            }
        }

        if (records.Edges is { Length: > 0 })
        {
            foreach (var edge in records.Edges)
            {
                proto.Edges.Add(new PreviewEdge
                {
                    Id = edge.Id.ToString(),
                    Type = edge.Type ?? string.Empty,
                    IsComposition = edge.IsComposition,
                    Ordinal = edge.Ordinal ?? 0,
                    ScopeDocumentId = edge.ScopeDocumentId?.ToString() ?? string.Empty,
                    EdgeKey = edge.EdgeKey ?? string.Empty,
                    SrcId = edge.SrcId.ToString(),
                    DstId = edge.DstId.ToString(),
                    SrcSpanId = edge.SrcSpanId?.ToString() ?? string.Empty,
                    DstSpanId = edge.DstSpanId?.ToString() ?? string.Empty,
                    PropsJson = JsonSerializer.Serialize(edge.Props ?? new System.Text.Json.Nodes.JsonObject(), PreviewJsonOptions)
                });
            }
        }

        if (records.Annotations is { Length: > 0 })
        {
            foreach (var annotation in records.Annotations)
            {
                proto.Annotations.Add(new PreviewAnnotation
                {
                    Kind = annotation.Kind ?? string.Empty,
                    Severity = annotation.Severity ?? string.Empty,
                    Source = annotation.Source ?? string.Empty,
                    RuleId = annotation.RuleId ?? string.Empty,
                    Message = annotation.Message ?? string.Empty
                });
            }
        }

        return proto;
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
        return JsonSerializer.Serialize(obj, PreviewJsonOptions);
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

        await foreach (var progress in coordinator.ReindexAsync(
                         new ReindexRequestOptions(request.Clear),
                         context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(ToProtoProgress(progress)).ConfigureAwait(false);
        }

        await writer.FlushAsync(context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<WaitForPipelineResponse> WaitForPipeline(WaitForPipelineRequest request, ServerCallContext context)
    {
        IReadOnlyCollection<CoordinatorPipelineStage> stages = request.Stages.Count > 0
            ? request.Stages.Select(MapStage).ToArray()
            : Array.Empty<CoordinatorPipelineStage>();

        await coordinator.WaitForPipelineAsync(stages, request.WaitAll, context.CancellationToken).ConfigureAwait(false);
        var snapshot = coordinator.GetPipelineStatus();
        return new WaitForPipelineResponse { Status = ToProtoStatus(snapshot) };
    }

    public override async Task<ImportResponse> ImportRepository(ImportRequest request, ServerCallContext context)
    {
        await barrier.InitialScanCompleted.WaitAsync(context.CancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(request.Uri))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "uri is required."));

        if (!RepoUri.TryParse(request.Uri, out var repoUri))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid Repo URI '{request.Uri}'."));

        try
        {
            await importService.ImportAsync(repoUri!, context.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }

        var waitStages = ResolveImportStages(request);
        if (waitStages.Count > 0)
        {
            await coordinator.WaitForPipelineAsync(waitStages, waitAll: true, context.CancellationToken).ConfigureAwait(false);
        }

        // Flush writer to ensure all indexed documents are committed before refreshing FTS
        await writer.FlushAsync(context.CancellationToken).ConfigureAwait(false);

        // Refresh search projection and embeddings to include newly imported documents
        var waitForEmbeddings = waitStages.Contains(CoordinatorPipelineStage.Writer); // Writer = SemanticIndexing
        if (store is DuckDbGraphStore duck)
        {
            duck.RefreshSearchProjection(incrementalRefresh: true);

            if (_embeddingProvider is not null && _embeddingProvider.Enabled)
            {
                if (waitForEmbeddings)
                {
                    // SemanticIndexing requested - wait for embeddings to complete
                    try
                    {
                        await duck.RefreshDocumentEmbeddingsAsync(_embeddingProvider, context.CancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Embedding refresh failed during import");
                        throw new RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Internal, $"Embedding refresh failed: {ex.Message}"));
                    }
                }
                else
                {
                    // No semantic wait - refresh embeddings in background
                    var provider = _embeddingProvider;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await duck.RefreshDocumentEmbeddingsAsync(provider, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Background embedding refresh failed");
                        }
                    });
                }
            }
        }

        var snapshot = coordinator.GetPipelineStatus();
        return new ImportResponse { Status = ToProtoStatus(snapshot) };
    }

    public override Task<GetPipelineStatusResponse> GetPipelineStatus(GetPipelineStatusRequest request, ServerCallContext context)
    {
        var snapshot = coordinator.GetPipelineStatus();
        return Task.FromResult(new GetPipelineStatusResponse { Status = ToProtoStatus(snapshot) });
    }

    private static ReindexProgress ToProtoProgress(ReindexProgressSnapshot snapshot)
        => new()
        {
            Phase = snapshot.Phase switch
            {
                CoordinatorReindexPhase.Preparing => ReindexPhase.Preparing,
                CoordinatorReindexPhase.Enumerating => ReindexPhase.Enumerating,
                CoordinatorReindexPhase.Queueing => ReindexPhase.Queueing,
                CoordinatorReindexPhase.HotPath => ReindexPhase.HotPath,
                CoordinatorReindexPhase.Pruning => ReindexPhase.Pruning,
                CoordinatorReindexPhase.VectorRefresh => ReindexPhase.VectorRefresh,
                CoordinatorReindexPhase.MultiFileAnalysis => ReindexPhase.MultifileAnalysis,
                CoordinatorReindexPhase.IndexRebuild => ReindexPhase.IndexRebuild,
                CoordinatorReindexPhase.Completed => ReindexPhase.Completed,
                _ => ReindexPhase.Unspecified
            },
            TotalItems = (ulong)Math.Max(0, snapshot.TotalItems),
            ProcessedItems = (ulong)Math.Max(0, snapshot.ProcessedItems),
            PhaseElapsedMs = (ulong)Math.Max(0, snapshot.PhaseElapsed.TotalMilliseconds)
        };

    private static ProtoPipelineStatus ToProtoStatus(PipelineStatusSnapshot snapshot)
    {
        var proto = new ProtoPipelineStatus
        {
            CapturedAt = Timestamp.FromDateTimeOffset(snapshot.CapturedAt),
            Reindexing = snapshot.IsReindexing,
            WriterPending = snapshot.WriterPending
        };

        foreach (var stage in snapshot.Stages)
        {
            proto.Stages.Add(ToProtoStageStatus(stage));
        }

        return proto;
    }

    private static ProtoStageStatus ToProtoStageStatus(PipelineStageStatusSnapshot stage)
        => new()
        {
            Stage = stage.Stage switch
            {
                CoordinatorPipelineStage.Discovery => ProtoPipelineStage.Discovery,
                CoordinatorPipelineStage.Parsing => ProtoPipelineStage.Indexing,
                CoordinatorPipelineStage.Analysis => ProtoPipelineStage.Analysis,
                CoordinatorPipelineStage.Writer => ProtoPipelineStage.SemanticIndexing,
                _ => ProtoPipelineStage.Unspecified
            },
            Busy = stage.Busy,
            Queued = (uint)Math.Max(0, stage.Queued),
            InProgress = (uint)Math.Max(0, stage.InProgress)
        };

    private static IReadOnlyCollection<CoordinatorPipelineStage> ResolveImportStages(ImportRequest request)
    {
        if (!request.HasWaitStage)
            return new[] { CoordinatorPipelineStage.Writer }; // Default to SemanticIndexing - waits for embeddings

        if (request.WaitStage == ProtoPipelineStage.Unspecified)
            return Array.Empty<CoordinatorPipelineStage>();

        return new[] { MapStage(request.WaitStage) };
    }

    private static CoordinatorPipelineStage MapStage(ProtoPipelineStage stage)
        => stage switch
        {
            ProtoPipelineStage.Discovery => CoordinatorPipelineStage.Discovery,
            ProtoPipelineStage.Indexing => CoordinatorPipelineStage.Parsing,
            ProtoPipelineStage.Analysis => CoordinatorPipelineStage.Analysis,
            ProtoPipelineStage.SemanticIndexing => CoordinatorPipelineStage.Writer,
            _ => CoordinatorPipelineStage.Discovery
        };

}
