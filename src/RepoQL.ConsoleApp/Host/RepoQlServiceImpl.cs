using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.FileSystems.Imports;
using RepoQL.Indexing.Hosting;
using RepoQL.Xray;
using ProtoPipelineStage = RepoQL.Contracts.PipelineStage;
using ProtoPipelineStatus = RepoQL.Contracts.PipelineStatus;
using ProtoStageStatus = RepoQL.Contracts.StageStatus;

namespace RepoQL.ConsoleApp.Host;

public sealed class RepoQlServiceImpl : Contracts.RepoQL.RepoQLBase
{
    private readonly DuckDbDataStore _db;
    private readonly RepositoryConfiguration repoConfig;
    private readonly IInitialIndexingBarrier barrier;
    private readonly IIndexingCoordinator coordinator;
    private readonly IFileSystemImportService importService;
    private readonly ICompositeFileSystemManager _mountManager;
    private readonly DocumentPreviewService _previewService;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly ILlmProvider? _llmProvider;
    private readonly EmbeddingMode _embeddingMode;
    private readonly XrayOrchestrator _xrayOrchestrator;
    private readonly StatusEventAggregator _statusAggregator;
    private readonly ILogger<RepoQlServiceImpl> _logger;
    private static readonly JsonSerializerOptions PreviewJsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static int GetEnvInt(string name, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    public RepoQlServiceImpl(
        DuckDbDataStore db,
        RepositoryConfiguration repoConfig,
        IInitialIndexingBarrier barrier,
        IIndexingCoordinator coordinator,
        IFileSystemImportService importService,
        ICompositeFileSystemManager mountManager,
        DocumentPreviewService previewService,
        IHostApplicationLifetime hostLifetime,
        XrayOrchestrator xrayOrchestrator,
        StatusEventAggregator statusAggregator,
        EmbeddingModeOptions? embeddingModeOptions = null,
        IEmbeddingProvider? embeddingProvider = null,
        ILlmProvider? llmProvider = null,
        ILogger<RepoQlServiceImpl>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        this.repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));
        this.barrier = barrier ?? throw new ArgumentNullException(nameof(barrier));
        _embeddingProvider = embeddingProvider;
        _llmProvider = llmProvider;
        _embeddingMode = embeddingModeOptions?.Mode ?? EmbeddingMode.Full;
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _mountManager = mountManager ?? throw new ArgumentNullException(nameof(mountManager));
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _hostLifetime = hostLifetime ?? throw new ArgumentNullException(nameof(hostLifetime));
        _xrayOrchestrator = xrayOrchestrator ?? throw new ArgumentNullException(nameof(xrayOrchestrator));
        _statusAggregator = statusAggregator ?? throw new ArgumentNullException(nameof(statusAggregator));
        _logger = logger ?? NullLogger<RepoQlServiceImpl>.Instance;
    }

    public override async Task<RawQueryResponse> ExecuteRawQuery(RawQueryRequest request, ServerCallContext context)
    {
        // No barrier - queries execute immediately with whatever data is available.
        // XrayTool handles "call again to wait" pattern for semantic readiness.
        var resp = new RawQueryResponse();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Substitute parameters into SQL (DuckDbDataStore.Query does not support params)
            var sql = SubstituteParameters(request.Sql, request.Parameters);
            var rows = _db.Query(sql);

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
            resp.Truncated = limited && (first is not null) && (_db.Query(sql).Skip(resp.Rows.Count).Any());

            // Check token budget and potentially summarize
            if (request.TokenBudget > 0 && resp.Rows.Count > 0)
            {
                var formatted = FormatResponseForTokenEstimation(resp);
                var estimatedTokens = TokenEstimator.EstimateTokens(formatted);

                if (estimatedTokens > request.TokenBudget)
                {
                    var intent = ExtractSqlComment(request.Sql);

                    if (!string.IsNullOrWhiteSpace(intent) && _llmProvider is { Enabled: true })
                    {
                        try
                        {
                            var originalRowCount = resp.RowCount;
                            var summary = await _llmProvider.SummarizeAsync(
                                formatted,
                                intent,
                                maxTokens: request.TokenBudget,
                                repoTree: null,
                                ct: context.CancellationToken).ConfigureAwait(false);

                            // Replace response with summarized version
                            resp.Rows.Clear();
                            resp.Columns.Clear();
                            resp.Columns.Add(new ColumnSchema { Name = "summary", DbType = "VARCHAR" });
                            var summaryRow = new RowData();
                            summaryRow.Values.Add(Value.ForString(summary));
                            resp.Rows.Add(summaryRow);
                            resp.RowCount = 1;
                            resp.Summarized = true;
                            resp.OriginalRowCount = originalRowCount;
                        }
                        catch (Exception ex)
                        {
                            // LLM failed - log and return original response
                            _logger.LogWarning(ex, "LLM summarization failed, returning original response");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
        finally
        {
            sw.Stop();
            resp.ExecutionTimeMs = sw.ElapsedMilliseconds;

            // Populate indexer status
            var status = coordinator.GetPipelineStatus();
            var pending = status.Stages.Sum(s => s.Queued + s.InProgress);
            resp.IndexPending = pending;
            resp.SemanticEnabled = _embeddingMode != EmbeddingMode.None;
            resp.SemanticReady = resp.SemanticEnabled && pending == 0;
        }
        return resp;
    }

    /// <summary>
    /// Extract user intent from SQL comment (-- or /* */).
    /// </summary>
    private static string? ExtractSqlComment(string sql)
    {
        // Single-line comment: -- comment
        var singleLine = Regex.Match(sql, @"--\s*(.+?)(?:\r?\n|$)");
        if (singleLine.Success)
            return singleLine.Groups[1].Value.Trim();

        // Block comment: /* comment */
        var block = Regex.Match(sql, @"/\*\s*([\s\S]*?)\s*\*/");
        if (block.Success)
            return block.Groups[1].Value.Trim();

        return null;
    }

    /// <summary>
    /// Format response as text for token estimation.
    /// </summary>
    private static string FormatResponseForTokenEstimation(RawQueryResponse resp)
    {
        var sb = new StringBuilder();
        var colNames = resp.Columns.Select(c => c.Name).ToArray();
        sb.AppendLine(string.Join("\t", colNames));

        foreach (var row in resp.Rows)
        {
            var values = row.Values.Select(v => v.KindCase switch
            {
                Value.KindOneofCase.StringValue => v.StringValue,
                Value.KindOneofCase.NumberValue => v.NumberValue.ToString(CultureInfo.InvariantCulture),
                Value.KindOneofCase.BoolValue => v.BoolValue.ToString(),
                Value.KindOneofCase.NullValue => "NULL",
                _ => v.ToString()
            });
            sb.AppendLine(string.Join("\t", values));
        }

        return sb.ToString();
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
        catch (IOException ex) when (ex.Message.Contains("client reset the request stream", StringComparison.OrdinalIgnoreCase)) { }
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
                var kindsList = string.Join(',', kinds);
                var sql = $@"SELECT kind, severity, source, rule_id, message,
                                   CASE WHEN {includeData} THEN data ELSE NULL END AS data,
                                   CASE WHEN {includeResolvedTargetUri} THEN resolved_target_uri ELSE NULL END AS resolved_target_uri,
                                   target_node_id, target_edge_id, target_span_id, created_at, expires_at
                            FROM annotations_for('{canonicalUri.Replace("'", "''")}', '{kindsList.Replace("'", "''")}', {(minSeverity is null ? "NULL" : $"'{minSeverity.Replace("'", "''")}'")})";
                var rows = _db.Query(sql);

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
                    var existsSql = $"SELECT 1 FROM node WHERE lower(uri)=lower(repository_uri_container('{canonicalUri.Replace("'", "''")}')) LIMIT 1";
                    var exists = _db.Query(existsSql).Any();
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
                    PropsJson = JsonSerializer.Serialize(node.Props ?? new JsonObject(), PreviewJsonOptions)
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
                    PropsJson = JsonSerializer.Serialize(edge.Props ?? new JsonObject(), PreviewJsonOptions)
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

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "JSON serialization for dynamic data structures; fallback serialization")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JSON serialization for dynamic data structures; fallback serialization")]
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
        ulong ul => Value.ForNumber(ul),
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

    /// <summary>
    /// Substitutes ? placeholders in SQL with properly escaped values from parameters.
    /// DuckDbDataStore.Query doesn't support parameterized queries, so we inline values.
    /// </summary>
    private static string SubstituteParameters(string sql, IList<Value> parameters)
    {
        if (parameters.Count == 0)
            return sql;

        var result = new StringBuilder(sql.Length + parameters.Count * 20);
        var paramIndex = 0;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            // Skip string literals (single quotes)
            if (c == '\'')
            {
                var start = i;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        i += 2; // Skip escaped quote
                        continue;
                    }
                    if (sql[i] == '\'')
                        break;
                    i++;
                }
                result.Append(sql.AsSpan(start, i - start + 1));
                continue;
            }

            // Replace ? with parameter value
            if (c == '?')
            {
                if (paramIndex < parameters.Count)
                {
                    result.Append(ToSqlLiteral(parameters[paramIndex]));
                    paramIndex++;
                }
                else
                {
                    result.Append('?'); // Not enough params, keep placeholder
                }
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>
    /// Converts a protobuf Value to a SQL literal string.
    /// </summary>
    private static string ToSqlLiteral(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.NullValue => "NULL",
        Value.KindOneofCase.BoolValue => value.BoolValue ? "TRUE" : "FALSE",
        Value.KindOneofCase.NumberValue => value.NumberValue.ToString(CultureInfo.InvariantCulture),
        Value.KindOneofCase.StringValue => $"'{value.StringValue.Replace("'", "''")}'",
        _ => "NULL"
    };

    public override async Task ReindexAll(ReindexRequest request, IServerStreamWriter<ReindexProgress> responseStream, ServerCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        await foreach (var progress in coordinator.ReindexAsync(
                         new ReindexRequestOptions(request.Clear),
                         context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(ToProtoProgress(progress)).ConfigureAwait(false);
        }
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
        var sw = Stopwatch.StartNew();
        var uri = request.Uri?.Trim() ?? "";
        var isRemoval = uri.StartsWith('-');
        var displayUri = isRemoval ? uri.Substring(1).Trim() : uri;

        _logger.LogInformation("[Import] Starting {Operation} for {Uri}", isRemoval ? "removal" : "import", displayUri);

        try
        {
            _logger.LogDebug("[Import] Waiting for initial scan barrier...");
            await barrier.InitialScanCompleted.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            _logger.LogDebug("[Import] Initial scan barrier passed ({ElapsedMs}ms)", sw.ElapsedMilliseconds);

            if (string.IsNullOrWhiteSpace(request.Uri))
            {
                _logger.LogWarning("[Import] Rejected: empty URI");
                throw new RpcException(new Status(StatusCode.InvalidArgument, "uri is required."));
            }

            // Handle removal with '-' prefix
            if (isRemoval)
            {
                _logger.LogInformation("[Import] Delegating to removal handler for {Uri}", displayUri);
                return await RemoveImportAsync(displayUri, context).ConfigureAwait(false);
            }

            if (!RepoUri.TryParse(uri, out var repoUri))
            {
                _logger.LogWarning("[Import] Rejected: invalid URI format '{Uri}'", uri);
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid Repo URI '{uri}'."));
            }

            _logger.LogInformation("[Import] Parsed URI: scheme={Scheme}, authority={Authority}, path={Path}",
                repoUri!.Scheme, repoUri.Authority, repoUri.AbsolutePath);

            // Clone/sync the repository
            _logger.LogDebug("[Import] Starting repository clone/sync...");
            var importStart = sw.ElapsedMilliseconds;
            try
            {
                await importService.ImportAsync(repoUri, context.CancellationToken).ConfigureAwait(false);
                _logger.LogInformation("[Import] Clone/sync completed ({ElapsedMs}ms)", sw.ElapsedMilliseconds - importStart);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "[Import] Clone/sync failed with InvalidOperationException after {ElapsedMs}ms", sw.ElapsedMilliseconds - importStart);
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Import] Clone/sync failed with exception after {ElapsedMs}ms", sw.ElapsedMilliseconds - importStart);
                throw new RpcException(new Status(StatusCode.Internal, ex.Message));
            }

            // Wait for pipeline stages
            var waitStages = ResolveImportStages(request);
            if (waitStages.Count > 0)
            {
                _logger.LogDebug("[Import] Waiting for pipeline stages: {Stages}", string.Join(", ", waitStages));
                var pipelineStart = sw.ElapsedMilliseconds;
                await coordinator.WaitForPipelineAsync(waitStages, waitAll: true, context.CancellationToken).ConfigureAwait(false);
                _logger.LogInformation("[Import] Pipeline wait completed ({ElapsedMs}ms)", sw.ElapsedMilliseconds - pipelineStart);
            }
            else
            {
                _logger.LogDebug("[Import] No pipeline stages to wait for");
            }

            // Handle embeddings
            var waitForEmbeddings = waitStages.Contains(CoordinatorPipelineStage.Writer); // Writer = SemanticIndexing
            if (_embeddingProvider is not null && _embeddingProvider.Enabled)
            {
                if (waitForEmbeddings)
                {
                    _logger.LogDebug("[Import] Refreshing embeddings (synchronous)...");
                    var embeddingStart = sw.ElapsedMilliseconds;
                    try
                    {
                        var refresher = new EmbeddingRefresher(_db, _embeddingMode, _logger as ILogger<EmbeddingRefresher>);
                        await refresher.RefreshAsync(_embeddingProvider, context.CancellationToken).ConfigureAwait(false);
                        _logger.LogInformation("[Import] Embedding refresh completed ({ElapsedMs}ms)", sw.ElapsedMilliseconds - embeddingStart);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Import] Embedding refresh failed after {ElapsedMs}ms", sw.ElapsedMilliseconds - embeddingStart);
                        throw new RpcException(new Status(StatusCode.Internal, $"Embedding refresh failed: {ex.Message}"));
                    }
                }
                else
                {
                    _logger.LogDebug("[Import] Scheduling background embedding refresh");
                    var provider = _embeddingProvider;
                    var db = _db;
                    var logger = _logger;
                    var embeddingMode = _embeddingMode;
                    _ = Task.Run(async () =>
                    {
                        var bgStart = Stopwatch.StartNew();
                        try
                        {
                            var refresher = new EmbeddingRefresher(db, embeddingMode, logger as ILogger<EmbeddingRefresher>);
                            await refresher.RefreshAsync(provider, CancellationToken.None).ConfigureAwait(false);
                            logger.LogInformation("[Import] Background embedding refresh completed ({ElapsedMs}ms)", bgStart.ElapsedMilliseconds);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "[Import] Background embedding refresh failed after {ElapsedMs}ms", bgStart.ElapsedMilliseconds);
                        }
                    });
                }
            }
            else
            {
                _logger.LogDebug("[Import] Embeddings not enabled, skipping");
            }

            var snapshot = coordinator.GetPipelineStatus();
            _logger.LogInformation("[Import] Completed successfully for {Uri} in {ElapsedMs}ms", uri, sw.ElapsedMilliseconds);
            return new ImportResponse { Status = ToProtoStatus(snapshot) };
        }
        catch (RpcException)
        {
            throw; // Already logged
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Import] Cancelled for {Uri} after {ElapsedMs}ms", displayUri, sw.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Import] Unexpected error for {Uri} after {ElapsedMs}ms", displayUri, sw.ElapsedMilliseconds);
            throw new RpcException(new Status(StatusCode.Internal, $"Import failed: {ex.Message}"));
        }
    }

    private Task<ImportResponse> RemoveImportAsync(string uri, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(uri))
        {
            _logger.LogWarning("[Import:Remove] Rejected: empty URI");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "URI is required for removal."));
        }

        _logger.LogDebug("[Import:Remove] Searching for mount matching '{Uri}'", uri);

        // Find matching mount by source URI or mount ID pattern
        var mounts = _db.GetAllMounts();
        _logger.LogDebug("[Import:Remove] Found {Count} total mounts to search", mounts.Count);

        var matchingMount = mounts.FirstOrDefault(m =>
            m.SourceUri.Equals(uri, StringComparison.OrdinalIgnoreCase) ||
            m.Id.Contains(uri.Replace("://", ":"), StringComparison.OrdinalIgnoreCase));

        if (matchingMount is null)
        {
            _logger.LogWarning("[Import:Remove] No mount found matching '{Uri}'. Available mounts: {Mounts}",
                uri, string.Join(", ", mounts.Select(m => m.Id)));
            throw new RpcException(new Status(StatusCode.NotFound, $"No import found matching: {uri}"));
        }

        _logger.LogInformation("[Import:Remove] Found mount {MountId} (source: {SourceUri}, local: {LocalPath})",
            matchingMount.Id, matchingMount.SourceUri, matchingMount.LocalPath);

        // Build URI pattern for matching documents
        var docPattern = string.IsNullOrEmpty(matchingMount.Authority)
            ? $"{matchingMount.Scheme}:///{matchingMount.PathPrefix}%"
            : $"{matchingMount.Scheme}://{matchingMount.Authority}/{matchingMount.PathPrefix}%";

        _logger.LogDebug("[Import:Remove] Querying documents with pattern '{Pattern}'", docPattern);

        // Get all document URIs matching this mount, then delete each using DeleteArtifact
        var docUris = _db.Read(
            $"SELECT uri FROM node WHERE kind = 'document' AND uri LIKE '{docPattern.Replace("'", "''")}'",
            r => r.GetString(0));

        _logger.LogInformation("[Import:Remove] Found {Count} documents to delete", docUris.Count);

        var deleteStart = sw.ElapsedMilliseconds;
        var deleted = 0;
        foreach (var docUri in docUris)
        {
            if (RepoUri.TryParse(docUri, out var repoUri))
            {
                _db.DeleteArtifact(repoUri);
                deleted++;
                if (deleted % 100 == 0)
                {
                    _logger.LogDebug("[Import:Remove] Deleted {Count}/{Total} documents ({ElapsedMs}ms)",
                        deleted, docUris.Count, sw.ElapsedMilliseconds - deleteStart);
                }
            }
        }
        _logger.LogInformation("[Import:Remove] Deleted {Count} documents ({ElapsedMs}ms)",
            deleted, sw.ElapsedMilliseconds - deleteStart);

        // Delete the mount record
        _logger.LogDebug("[Import:Remove] Deleting mount record...");
        _db.DeleteMount(matchingMount.Id);

        // Remove mount from memory
        _logger.LogDebug("[Import:Remove] Removing mount from memory...");
        _mountManager.RemoveMount(matchingMount.Id);

        _logger.LogInformation("[Import:Remove] Completed removal of {MountId} ({Count} documents) in {ElapsedMs}ms",
            matchingMount.Id, deleted, sw.ElapsedMilliseconds);

        var snapshot = coordinator.GetPipelineStatus();
        return Task.FromResult(new ImportResponse { Status = ToProtoStatus(snapshot) });
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

    public override async Task<XrayResponse> Xray(XrayRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Get indexer status via SQL
            var status = await GetIndexerStatusAsync(0, context.CancellationToken).ConfigureAwait(false);

            // Map intent
            var intent = request.Intent switch
            {
                XrayIntent.Explore => Intent.Explore,
                XrayIntent.Find => Intent.Find,
                XrayIntent.Examine => Intent.Examine,
                XrayIntent.Understand => Intent.Understand,
                _ => Intent.Explore
            };

            // Build query
            var query = new XrayQuery(
                TokenBudget: request.TokenBudget,
                Intent: intent,
                Scope: string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope,
                Keywords: string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords,
                Boost: string.IsNullOrWhiteSpace(request.Boost) ? null : request.Boost,
                Penalize: string.IsNullOrWhiteSpace(request.Penalize) ? null : request.Penalize,
                Limit: request.Limit > 0 ? request.Limit : null
            );

            // Execute via orchestrator (pass stopwatch for accurate timing in output)
            var result = await _xrayOrchestrator.ExecuteAsync(query, status, context.CancellationToken, sw).ConfigureAwait(false);

            // Update status with elapsed time
            var hasKeywords = !string.IsNullOrWhiteSpace(request.Keywords);
            var isReady = status.IndexPending == 0 && (!hasKeywords || status.SemanticReady);

            // Build response
            var response = new XrayResponse
            {
                Success = true,
                RenderedOutput = result.RenderedOutput,
                Truncated = result.Truncated,
                Status = new XrayIndexerStatus
                {
                    IndexPending = status.IndexPending,
                    SemanticReady = status.SemanticReady,
                    Ready = isReady,
                    ElapsedMs = sw.ElapsedMilliseconds
                }
            };

            // Map structured results
            foreach (var xrayResult in result.Results)
            {
                response.Results.Add(ToProtoXrayResult(xrayResult));
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Xray query failed");
            return new XrayResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public override async Task<ReadResponse> Read(ReadRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Get indexer status
            var status = await GetIndexerStatusAsync(0, context.CancellationToken).ConfigureAwait(false);

            // Create content provider using the database
            var contentProvider = new DatabaseReadContentProvider(_db);

            // Create orchestrator with xray for question handling (large content)
            // and LLM for direct synthesis (small content)
            var orchestrator = new ReadOrchestrator(contentProvider, _xrayOrchestrator, _llmProvider);

            // Execute
            var result = await orchestrator.ExecuteAsync(
                request.Uri,
                request.TokenBudget,
                status,
                context.CancellationToken,
                sw).ConfigureAwait(false);

            return new ReadResponse
            {
                Success = result.Success,
                Error = result.Error ?? "",
                RenderedOutput = result.RenderedOutput ?? "",
                Representation = result.Representation ?? "",
                FilesRead = result.FilesRead,
                FilesOmitted = result.FilesOmitted,
                Status = new XrayIndexerStatus
                {
                    IndexPending = status.IndexPending,
                    SemanticReady = status.SemanticReady,
                    Ready = status.IndexPending == 0,
                    ElapsedMs = sw.ElapsedMilliseconds
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Read operation failed");
            return new ReadResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Implementation of IReadContentProvider that uses DuckDbDataStore.
    /// </summary>
    private sealed class DatabaseReadContentProvider : IReadContentProvider
    {
        private readonly DuckDbDataStore _db;

        public DatabaseReadContentProvider(DuckDbDataStore db) => _db = db;

        public Task<ReadDocument?> FetchDocumentAsync(string uri, CancellationToken cancellationToken)
        {
            var escapedUri = uri.Replace("'", "''", StringComparison.Ordinal);
            var sql = $"""
                SELECT n.uri,
                       a.text_content,
                       a.media_type,
                       a.headline,
                       a.summary,
                       a.structure
                FROM node n
                JOIN artifact a ON a.id = n.artifact_id
                WHERE lower(n.uri) = lower('{escapedUri}')
                LIMIT 1
                """;

            var results = _db.Query(sql);
            var row = results.FirstOrDefault();

            if (row is null)
                return Task.FromResult<ReadDocument?>(null);

            return Task.FromResult<ReadDocument?>(new ReadDocument(
                Uri: row.TryGetValue("uri", out var u) ? u?.ToString() ?? uri : uri,
                TextContent: row.TryGetValue("text_content", out var tc) ? tc?.ToString() : null,
                MediaType: row.TryGetValue("media_type", out var mt) ? mt?.ToString() : null,
                Headline: row.TryGetValue("headline", out var h) ? h?.ToString() : null,
                Summary: row.TryGetValue("summary", out var s) ? s?.ToString() : null,
                Structure: row.TryGetValue("structure", out var st) ? st?.ToString() : null));
        }

        public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string globUri, CancellationToken cancellationToken)
        {
            var escapedGlob = globUri.Replace("'", "''", StringComparison.Ordinal);
            var sql = $"""
                SELECT n.uri,
                       a.text_content,
                       a.media_type,
                       a.headline,
                       a.summary,
                       a.structure
                FROM node n
                JOIN artifact a ON a.id = n.artifact_id
                WHERE n.kind = 'document'
                  AND (glob_match(n.uri, '{escapedGlob}', default_scheme := 'file:///')
                       OR glob_match(n.uri, '{escapedGlob}', default_scheme := 'docs:///'))
                ORDER BY n.uri
                LIMIT 50
                """;

            var results = _db.Query(sql);
            var documents = new List<ReadDocument>();

            foreach (var row in results)
            {
                documents.Add(new ReadDocument(
                    Uri: row.TryGetValue("uri", out var u) ? u?.ToString() ?? "" : "",
                    TextContent: row.TryGetValue("text_content", out var tc) ? tc?.ToString() : null,
                    MediaType: row.TryGetValue("media_type", out var mt) ? mt?.ToString() : null,
                    Headline: row.TryGetValue("headline", out var h) ? h?.ToString() : null,
                    Summary: row.TryGetValue("summary", out var s) ? s?.ToString() : null,
                    Structure: row.TryGetValue("structure", out var st) ? st?.ToString() : null));
            }

            return Task.FromResult<IReadOnlyList<ReadDocument>>(documents);
        }

        public Task<string?> GetRepoTreeAsync(string? scope, CancellationToken cancellationToken)
        {
            // Generate ASCII tree of repository structure using the tree() macro
            // Limit to reasonable size to avoid bloating context
            var sql = """
                SELECT tree(list(uri ORDER BY uri)) as tree_output
                FROM node
                WHERE kind = 'document'
                  AND uri LIKE 'file:///%'
                LIMIT 500
                """;

            try
            {
                var results = _db.Query(sql);
                var row = results.FirstOrDefault();

                if (row is null || !row.TryGetValue("tree_output", out var treeOutput))
                    return Task.FromResult<string?>(null);

                return Task.FromResult(treeOutput?.ToString());
            }
            catch
            {
                // Tree generation failed - not critical, return null
                return Task.FromResult<string?>(null);
            }
        }

        public Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, CancellationToken cancellationToken)
        {
            if (uris.Count == 0)
                return Task.FromResult<string?>(null);

            // Build JSON array of URIs for the tree() UDF
            var jsonArray = System.Text.Json.JsonSerializer.Serialize(uris);
            var escapedJson = jsonArray.Replace("'", "''", StringComparison.Ordinal);

            var sql = $"SELECT tree('{escapedJson}', {(foldersOnly ? "true" : "false")}) as tree_output";

            try
            {
                var results = _db.Query(sql);
                var row = results.FirstOrDefault();

                if (row is null || !row.TryGetValue("tree_output", out var treeOutput))
                    return Task.FromResult<string?>(null);

                return Task.FromResult(treeOutput?.ToString());
            }
            catch
            {
                // Tree generation failed - return null, caller will fall back to list
                return Task.FromResult<string?>(null);
            }
        }
    }

    /// <summary>
    /// Stream real-time status events to the client. Replaces polling for live dashboard updates.
    /// </summary>
    public override async Task WatchStatus(
        WatchStatusRequest request,
        IServerStreamWriter<StatusEvent> responseStream,
        ServerCallContext context)
    {
        _logger.LogDebug("Client subscribed to status stream");

        try
        {
            await foreach (var evt in _statusAggregator.WatchAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(evt, context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - normal
            _logger.LogDebug("Client disconnected from status stream");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Status stream error");
            throw;
        }
    }

    private async Task<IndexerStatus> GetIndexerStatusAsync(long elapsedMs, CancellationToken ct)
    {
        try
        {
            var rows = _db.Query("SELECT indexing_diagnostics() as diag");
            var text = rows.FirstOrDefault()?.TryGetValue("diag", out var val) == true ? val?.ToString() : null;

            if (!string.IsNullOrEmpty(text))
            {
                var values = ParseKeyValueText(text);

                var hotPathDepth = values.TryGetValue("hot_path_depth", out var hp) && int.TryParse(hp, out var hpv) ? hpv : 0;
                var idlePending = values.TryGetValue("idle_pending", out var ip) && int.TryParse(ip, out var ipv) ? ipv : 0;
                var analysisDepth = values.TryGetValue("analysis_depth", out var ad) && int.TryParse(ad, out var adv) ? adv : 0;
                var writerPending = values.TryGetValue("writer_pending", out var wp) && int.TryParse(wp, out var wpv) ? wpv : 0;
                var embedEnabled = values.TryGetValue("query_embed_enabled", out var ee) && bool.TryParse(ee, out var eev) && eev;

                return IndexerStatus.FromDiagnostics(hotPathDepth, idlePending, analysisDepth, writerPending, elapsedMs, embedEnabled);
            }
        }
        catch
        {
            // Fall back to unknown status on any error
        }

        return new IndexerStatus(0, false, false, elapsedMs);
    }

    private static Dictionary<string, string> ParseKeyValueText(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim();
                result[key] = value;
            }
        }
        return result;
    }

    private static XrayResultItem ToProtoXrayResult(XrayResult result)
    {
        var item = new XrayResultItem
        {
            Uri = result.Uri,
            Confidence = result.Confidence
        };

        if (!string.IsNullOrEmpty(result.Kind))
            item.Kind = result.Kind;
        if (!string.IsNullOrEmpty(result.Headline))
            item.Headline = result.Headline;
        if (!string.IsNullOrEmpty(result.Structure))
            item.Structure = result.Structure;
        if (!string.IsNullOrEmpty(result.Snippet))
            item.Snippet = result.Snippet;
        if (!string.IsNullOrEmpty(result.Lang))
            item.Lang = result.Lang;
        if (!string.IsNullOrEmpty(result.SemanticType))
            item.SemanticType = result.SemanticType;

        if (result.ChildObjects is { Count: > 0 })
        {
            foreach (var child in result.ChildObjects)
            {
                item.Children.Add(ToProtoXrayResult(child));
            }
        }

        return item;
    }
}
