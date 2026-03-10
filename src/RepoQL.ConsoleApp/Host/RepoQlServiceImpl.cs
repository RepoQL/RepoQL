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
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Inference;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.FileSystems.Imports;
using RepoQL.Indexing.Hosting;
using RepoQL.Sarif;
using RepoQL.Explore;
using RepoQL.Read;
using ProtoPipelineStage = RepoQL.Contracts.PipelineStage;
using ProtoPipelineStatus = RepoQL.Contracts.PipelineStatus;
using ProtoStageStatus = RepoQL.Contracts.StageStatus;

namespace RepoQL.ConsoleApp.Host;

public sealed class RepoQlServiceImpl : Contracts.RepoQL.RepoQLBase
{
    private readonly DuckDbDataStore _db;
    private readonly RepositoryConfiguration repoConfig;
    private readonly IIndexingCoordinator coordinator;
    private readonly IFileSystemImportService importService;
    private readonly ISarifImportService _sarifImportService;
    private readonly ICompositeFileSystemManager _mountManager;
    private readonly DocumentPreviewService _previewService;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IInferenceProvider? _inferenceProvider;
    private readonly EmbeddingMode _embeddingMode;
    private readonly ExploreOrchestrator _exploreOrchestrator;
    private readonly ReadOrchestrator _readOrchestrator;
    private readonly UriRegistry? _uriRegistry;
    private readonly QueueCommandService? _queueCommandService;
    private readonly RepoQlConfig.HostSettings _hostSettings;
    private readonly RepoQlConfig.EmbeddingSettings _embeddingSettings;
    private readonly RepoQlConfig.InferenceSettings _inferenceSettings;
    private readonly ILogger<RepoQlServiceImpl> _logger;

    /// <summary>System prompt for the explain synthesis LLM call. Shapes output into Answer/Evidence/Nuance.</summary>
    private const string ExplainSystemPrompt = """
        # Repository Analysis Agent

        You're augmenting another AI agent's codebase exploration. They're making real decisions—writing code, explaining systems, choosing architectures. They see only your response, not the underlying data. Your synthesis becomes their understanding, your confidence becomes their confidence.

        ## Capsule: TruthStakes
        **Invariant**: Claims become code; wrong information propagates into systems.
        **Example**: Caller asks "does this validate input?" You see partial validation. Say "validates length but not format—SQL injection possible" not just "yes, it validates."
        //BOUNDARY: When uncertain, say so explicitly rather than hedging with weak language.

        ## Capsule: EvidenceRichness
        **Invariant**: Generous inline snippets now save costly follow-ups later.
        **Example**: Don't say "auth is in AuthService.cs:42". Show the URI from the context and include the snippet:
        <uri from context>#line=42,48
        ---
        public bool ValidateToken(string token) {
            return _jwt.Verify(token, _secret);
        }
        ---
        //BOUNDARY: The caller cannot fetch more data. This response is their only view.
        //BOUNDARY: Use the exact URIs from the supplied context — they may be file://, help://, github://, or other schemes. Never fabricate URIs.

        ## Capsule: GapDetection
        **Invariant**: Surface what's missing or anomalous—the caller can't see these patterns.
        **Example**: "Auth checks user permissions, but I don't see where admin permissions are defined. Expected an AdminRole enum or similar."
        //BOUNDARY: Patterns of absence matter as much as patterns of presence.

        ## Capsule: VerifiableSynthesis
        **Invariant**: Connect dots AND show your work—the caller needs both insight and evidence trail.
        //BOUNDARY: Synthesis without evidence is unverifiable; evidence without synthesis wastes their time.

        ## Capsule: UnknownUnknowns
        **Invariant**: The caller asked one question but may need adjacent answers.
        **Example**: Question about AuthService? Note "AuthService depends on TokenCache which isn't in your query—may affect token lifetime behavior."

        ## Capsule: AgentEmpathy
        **Invariant**: The caller is an AI agent like you—answer as you'd want to be answered.
        //BOUNDARY: They're probably mid-task, not starting fresh. Context they already have shouldn't be repeated; context they're missing should be supplied.

        ---

        ## Response Format

        <Answer>
        Synthesis answering the question. If data doesn't fully answer, say what's missing and why.
        </Answer>

        <Evidence>
        Generous snippets grounding your claims. Annotate conclusions alongside snippets.
        Always provide snippets verbatim, or explicitly note when paraphrasing.
        Use the exact URIs from the supplied context (file://, help://, github://, etc.) — never invent URIs.
        </Evidence>

        <Nuance>
        (Optional—only if it genuinely adds value)
        - Context they may not know they need
        - Gaps or anomalies worth flagging
        - Related files worth exploring
        </Nuance>

        If data doesn't answer, say so in Answer.

        ## Remember
        - Every claim should have evidence
        - Gaps and anomalies should be surfaced
        - Be careful about stating something doesn't exist vs wasn't in search results
        - Misleading or unsubstantiated claims are much more damaging than no claims
        """;

    private static readonly JsonSerializerOptions PreviewJsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private int GetIdleGraceSeconds()
    {
        if (IsMcpImplicitSource())
            return 0;

        return _hostSettings.IdleGraceSeconds is > 0 ? _hostSettings.IdleGraceSeconds.Value : 45;
    }

    private static bool IsMcpImplicitSource()
        => string.Equals(
            Environment.GetEnvironmentVariable("REPOQL_IMPLICIT_SOURCE"),
            "mcp",
            StringComparison.OrdinalIgnoreCase);

    public RepoQlServiceImpl(
        DuckDbDataStore db,
        RepositoryConfiguration repoConfig,
        IIndexingCoordinator coordinator,
        IFileSystemImportService importService,
        ISarifImportService sarifImportService,
        ICompositeFileSystemManager mountManager,
        DocumentPreviewService previewService,
        IHostApplicationLifetime hostLifetime,
        ExploreOrchestrator exploreOrchestrator,
        ReadOrchestrator readOrchestrator,
        RepoQlConfig config,
        EmbeddingModeOptions? embeddingModeOptions = null,
        IEmbeddingProvider? embeddingProvider = null,
        IInferenceProvider? inferenceProvider = null,
        UriRegistry? uriRegistry = null,
        QueueCommandService? queueCommandService = null,
        ILogger<RepoQlServiceImpl>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        this.repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));
        _embeddingProvider = embeddingProvider;
        _inferenceProvider = inferenceProvider;
        _embeddingMode = embeddingModeOptions?.Mode ?? EmbeddingMode.Full;
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _sarifImportService = sarifImportService ?? throw new ArgumentNullException(nameof(sarifImportService));
        _mountManager = mountManager ?? throw new ArgumentNullException(nameof(mountManager));
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _hostLifetime = hostLifetime ?? throw new ArgumentNullException(nameof(hostLifetime));
        _exploreOrchestrator = exploreOrchestrator ?? throw new ArgumentNullException(nameof(exploreOrchestrator));
        _readOrchestrator = readOrchestrator ?? throw new ArgumentNullException(nameof(readOrchestrator));
        _uriRegistry = uriRegistry;
        _queueCommandService = queueCommandService;
        _hostSettings = (config ?? throw new ArgumentNullException(nameof(config))).Host;
        _embeddingSettings = config.Embedding;
        _inferenceSettings = config.Inference;
        _logger = logger ?? NullLogger<RepoQlServiceImpl>.Instance;
    }

    public override async Task<RawQueryResponse> ExecuteRawQuery(RawQueryRequest request, ServerCallContext context)
    {
        // No barrier - queries execute immediately with whatever data is available.
        // ExploreTool handles "call again to wait" pattern for semantic readiness.
        var resp = new RawQueryResponse();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Substitute parameters into SQL (DuckDbDataStore.Query does not support params)
            var sql = SubstituteParameters(request.Sql, request.Parameters);
            var rows = _db.Query(sql, context.CancellationToken);

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
            // Detect truncation from the already-fetched results: the full result set was
            // materialized by _db.Query, so if we took fewer rows than exist, it's truncated.
            resp.Truncated = limited && rows.Count > request.Limit;

            // Check token budget and potentially summarize
            if (request.TokenBudget > 0 && resp.Rows.Count > 0)
            {
                var formatted = FormatResponseForTokenEstimation(resp);
                var estimatedTokens = TokenEstimator.EstimateTokens(formatted);

                if (estimatedTokens > request.TokenBudget)
                {
                    var intent = ExtractSqlComment(request.Sql);

                    if (!string.IsNullOrWhiteSpace(intent) && _inferenceProvider is { Available: true })
                    {
                        try
                        {
                            var originalRowCount = resp.RowCount;
                            var summary = await _inferenceProvider.CompleteAsync(
                                new InferenceRequest
                                {
                                    Context = formatted,
                                    Prompt = intent,
                                    MaxTokens = request.TokenBudget
                                },
                                context.CancellationToken).ConfigureAwait(false);

                            // Replace response with summarized version
                            resp.Rows.Clear();
                            resp.Columns.Clear();
                            resp.Columns.Add(new ColumnSchema { Name = "summary", DbType = "VARCHAR" });
                            var summaryRow = new RowData();
                            summaryRow.Values.Add(Value.ForString(summary.Content));
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
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Query request was canceled."));
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }

        sw.Stop();
        resp.ExecutionTimeMs = sw.ElapsedMilliseconds;

        var trustSignal = GetTrustSignal(resp.ExecutionTimeMs, context.CancellationToken);
        resp.IndexPending = trustSignal.IndexPending;
        resp.IndexTotal = trustSignal.IndexTotal;
        resp.IndexFailed = trustSignal.IndexFailed;
        resp.IndexStale = trustSignal.IndexStale;
        resp.SemanticEnabled = trustSignal.SemanticEnabled;
        resp.SemanticReady = trustSignal.SemanticReady;
        resp.SemanticPercent = trustSignal.SemanticPercent;

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
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Client lease stream cancelled");
        }
        catch (IOException ex) when (ex.Message.Contains("client reset the request stream", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(ex, "Client lease stream reset");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                LeaseRegistry.Remove(clientId!);
                _logger.LogInformation("Client disconnected (clientId={ClientId})", clientId);
            }
        }

        var state = context.GetHttpContext().RequestServices.GetRequiredService<HostState>();
        return new ClientLeaseSummary
        {
            ServerStartedAt = state.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ImplicitStart = state.ImplicitStart,
            ActiveClients = LeaseRegistry.Count,
            ShutdownAfterIdleSeconds = GetIdleGraceSeconds()
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
                var rows = _db.Query(sql, context.CancellationToken);

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
                    var exists = _db.Query(existsSql, context.CancellationToken).Any();
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
        // Handle DuckDB collections from read_json_auto - use native protobuf types
        System.Collections.IList list => ToProtoListValue(list),
        System.Collections.IDictionary dict => Value.ForStruct(ToProtoStruct(dict)),
        _ => Value.ForString(value.ToString() ?? string.Empty)
    };

    /// <summary>
    /// Convert a collection to protobuf ListValue.
    /// Used for DuckDB arrays from read_json_auto.
    /// </summary>
    private static Value ToProtoListValue(System.Collections.IList list)
    {
        var listValue = new ListValue();
        foreach (var item in list)
        {
            listValue.Values.Add(ToProtoValue(item));
        }
        return new Value { ListValue = listValue };
    }

    /// <summary>
    /// Convert a dictionary to protobuf Struct.
    /// Used for DuckDB structs from read_json_auto.
    /// </summary>
    private static Struct ToProtoStruct(System.Collections.IDictionary dict)
    {
        var protoStruct = new Struct();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            var key = entry.Key?.ToString() ?? "";
            protoStruct.Fields[key] = ToProtoValue(entry.Value);
        }
        return protoStruct;
    }

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
                         new ReindexRequestOptions(request.Clear, string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope),
                         context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(ToProtoProgress(progress)).ConfigureAwait(false);
        }
    }

    public override Task<QueueControlResponse> QueueControl(QueueControlRequest request, ServerCallContext context)
    {
        if (_queueCommandService is null)
        {
            return Task.FromResult(new QueueControlResponse
            {
                Success = false,
                Message = "Queue control is not available in this host configuration."
            });
        }

        var outcome = _queueCommandService.Execute(request.Action, request.Uri);
        return Task.FromResult(new QueueControlResponse
        {
            Success = outcome.Success,
            Message = outcome.Message
        });
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

            if (string.Equals(repoUri.Scheme, "sarif", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("[Import] Routing to SARIF import service");
                return await ImportSarifAsync(repoUri, context, sw).ConfigureAwait(false);
            }

            // Clone/sync the repository and get the tracking operation
            _logger.LogDebug("[Import] Starting repository clone/sync...");
            var importStart = sw.ElapsedMilliseconds;
            Contracts.IOperation? operation = null;
            try
            {
                var result = await importService.ImportAsync(repoUri, request.Analyze, context.CancellationToken).ConfigureAwait(false);
                operation = result.Operation;
                _logger.LogInformation("[Import] Clone/sync completed ({ElapsedMs}ms), operation={OpId}",
                    sw.ElapsedMilliseconds - importStart, operation?.Id ?? "(none)");
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

            // Flush WAL so follow-up queries see the imported data immediately
            _db.TryCheckpoint();

            var snapshot = coordinator.GetPipelineStatus();
            _logger.LogInformation("[Import] Completed successfully for {Uri} in {ElapsedMs}ms", uri, sw.ElapsedMilliseconds);

            var response = new ImportResponse { Status = ToProtoStatus(snapshot) };
            var opProgress = operation?.Progress;
            if (opProgress is not null)
            {
                response.TotalFiles = opProgress.TotalFiles;
                response.IndexedCount = opProgress.IndexedCount;
                response.EmbeddedCount = opProgress.EmbeddedCount;
                response.FailedCount = opProgress.FailedCount;
            }

            if (operation is not null)
            {
                response.OperationId = operation.Id;
                var fileCount = opProgress?.TotalFiles ?? 0;
                response.Message = $"Importing {fileCount} files from {displayUri} - operation {operation.Id}";
            }
            else
            {
                _logger.LogWarning("[Import] No operation created - UriRegistry may not be configured");
                response.Message = $"Import started for {displayUri}. Operation tracking is unavailable.";
            }

            // Preserve existing post-import embedding refresh behavior without blocking the import response.
            if (operation is not null && _embeddingProvider is { Enabled: true } provider)
            {
                var db = _db;
                var logger = _logger;
                var embeddingMode = _embeddingMode;
                var settings = _embeddingSettings;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await operation.Completion.ConfigureAwait(false);
                        var refresher = new EmbeddingRefresher(db, embeddingMode, logger, settings);
                        await refresher.RefreshAsync(provider, CancellationToken.None).ConfigureAwait(false);
                        logger.LogInformation("[Import] Background batch embedding refresh completed for operation {OpId}", operation.Id);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogDebug("[Import] Background embedding refresh canceled for operation {OpId}", operation.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[Import] Background batch embedding refresh failed for operation {OpId}", operation.Id);
                    }
                });
            }

            return response;
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

    private async Task<ImportResponse> ImportSarifAsync(
        RepoUri repoUri,
        ServerCallContext context,
        Stopwatch sw)
    {
        var sarifPath = ResolveSarifFilePath(repoUri);
        _logger.LogInformation("[Import:SARIF] Importing findings from {Path}", sarifPath);

        RepoQL.Sarif.Models.SarifImportResult importResult;
        try
        {
            importResult = await _sarifImportService.ImportAsync(sarifPath, context.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[Import:SARIF] Import rejected for {Path}", sarifPath);
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Import:SARIF] Import failed for {Path}", sarifPath);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }

        _db.TryCheckpoint();
        var snapshot = coordinator.GetPipelineStatus();
        var message = FormatSarifImportMessage(importResult);
        _logger.LogInformation("[Import:SARIF] Completed in {ElapsedMs}ms with {Total} findings", sw.ElapsedMilliseconds, importResult.TotalFindings);

        return new ImportResponse
        {
            Status = ToProtoStatus(snapshot),
            Message = message
        };
    }

    private string ResolveSarifFilePath(RepoUri repoUri)
    {
        var decodedPath = Uri.UnescapeDataString(repoUri.AbsolutePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(decodedPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sarif:// URI must include a file path."));

        var normalized = decodedPath.Replace('\\', '/');
        if (normalized.StartsWith("/./", StringComparison.Ordinal))
            normalized = normalized[3..];
        else if (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        // file:///C:/... style path embedded in sarif URI absolute path.
        if (normalized.Length >= 3 && normalized[0] == '/' && char.IsLetter(normalized[1]) && normalized[2] == ':')
            normalized = normalized[1..];

        var candidate = normalized.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(candidate))
            return Path.GetFullPath(candidate);

        return Path.GetFullPath(Path.Combine(repoConfig.Path, candidate));
    }

    private static string FormatSarifImportMessage(RepoQL.Sarif.Models.SarifImportResult result)
    {
        var sb = new StringBuilder();
        if (result.Sources.Count == 1)
            sb.AppendLine($"Imported {result.TotalFindings} findings from {result.Sources[0].Source}");
        else
            sb.AppendLine($"Imported {result.TotalFindings} findings from {result.Sources.Count} sources");

        foreach (var source in result.Sources.OrderBy(s => s.Source, StringComparer.Ordinal))
        {
            sb.AppendLine($"{source.Source}: {source.Total} findings");
            sb.AppendLine($"  {source.Resolved} resolved to indexed files, {source.Unresolved} unresolved");
            sb.AppendLine($"  {source.New} new, {source.Updated} updated, {source.Unchanged} unchanged, {source.Expired} expired");
        }

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine("Warnings:");
            foreach (var warning in result.Warnings)
                sb.AppendLine($"- {warning}");
        }

        return sb.ToString().TrimEnd();
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

        // Remove indexed git history for this source so query/read history does not return stale results.
        var historyPrefix = BuildMountHistoryPrefix(matchingMount);
        _db.ExecuteRaw(
            $"""
            DELETE FROM git_file_change
            WHERE starts_with(uri, '{historyPrefix}')
               OR (old_uri IS NOT NULL AND starts_with(old_uri, '{historyPrefix}'));

            DELETE FROM git_commit
            WHERE hash NOT IN (SELECT DISTINCT commit_hash FROM git_file_change);
            """);
        _logger.LogInformation("[Import:Remove] Deleted git history rows matching prefix '{Prefix}'", historyPrefix);

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

    private static string BuildMountHistoryPrefix(FileSystemMountRecord mount)
    {
        var sourceUri = BuildMountSourceUri(mount).TrimEnd('/');
        return EscapeSqlLiteral($"{sourceUri}/");
    }

    private static string BuildMountSourceUri(FileSystemMountRecord mount)
    {
        var scheme = (mount.Scheme ?? string.Empty).Trim().ToLowerInvariant();
        var authority = mount.Authority?.Trim();
        var pathPrefix = (mount.PathPrefix ?? string.Empty).Trim('/').Replace('\\', '/');

        if (string.IsNullOrWhiteSpace(authority))
            return string.IsNullOrWhiteSpace(pathPrefix)
                ? $"{scheme}://"
                : $"{scheme}:///{pathPrefix}";

        return string.IsNullOrWhiteSpace(pathPrefix)
            ? $"{scheme}://{authority}"
            : $"{scheme}://{authority}/{pathPrefix}";
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static ReindexProgress ToProtoProgress(ReindexProgressSnapshot snapshot)
    {
        var proto = new ReindexProgress
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
            PhaseElapsedMs = (ulong)Math.Max(0, snapshot.PhaseElapsed.TotalMilliseconds),
            FailedCount = (uint)snapshot.FailedCount
        };
        if (snapshot.FailureDetails is { Count: > 0 })
            proto.FailureDetails.AddRange(snapshot.FailureDetails);
        if (snapshot.Milestones is { Count: > 0 })
            proto.Milestones.AddRange(snapshot.Milestones);
        return proto;
    }

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
    private static CoordinatorPipelineStage MapStage(ProtoPipelineStage stage)
        => stage switch
        {
            ProtoPipelineStage.Discovery => CoordinatorPipelineStage.Discovery,
            ProtoPipelineStage.Indexing => CoordinatorPipelineStage.Parsing,
            ProtoPipelineStage.Analysis => CoordinatorPipelineStage.Analysis,
            ProtoPipelineStage.SemanticIndexing => CoordinatorPipelineStage.Writer,
            _ => CoordinatorPipelineStage.Discovery
        };

    public override async Task<ExploreResponse> Explore(ExploreRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope;
            var notReady = await CheckScopeReadinessAsync(scope, request.Readiness, context.CancellationToken).ConfigureAwait(false);
            if (notReady is not null)
                return new ExploreResponse { Success = false, Error = notReady };

            var status = GetTrustSignal(0, context.CancellationToken);

            // Build query
            var query = new ExploreQuery(
                TokenBudget: request.TokenBudget,
                Breadth: Math.Clamp(request.Breadth > 0 ? request.Breadth : 5, 1, 10),
                Scope: scope,
                Keywords: string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords,
                Boost: string.IsNullOrWhiteSpace(request.Boost) ? null : request.Boost,
                Penalize: string.IsNullOrWhiteSpace(request.Penalize) ? null : request.Penalize,
                Limit: request.Limit > 0 ? request.Limit : null,
                Question: string.IsNullOrWhiteSpace(request.Question) ? null : request.Question
            );

            // Execute via orchestrator (pass stopwatch for accurate timing in output)
            var result = await _exploreOrchestrator.ExecuteAsync(query, status, context.CancellationToken, sw).ConfigureAwait(false);

            // Update status with elapsed time
            var hasKeywords = !string.IsNullOrWhiteSpace(request.Keywords);
            var isReady = status.IndexPending == 0 && (!hasKeywords || status.SemanticReady);

            // Build response
            var response = new ExploreResponse
            {
                Success = true,
                RenderedOutput = result.RenderedOutput,
                Truncated = result.Truncated,
                Status = new ExploreIndexerStatus
                {
                    IndexPending = status.IndexPending,
                    SemanticReady = status.SemanticReady,
                    Ready = isReady,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    IndexTotal = status.IndexTotal,
                    IndexFailed = status.IndexFailed,
                    IndexStale = status.IndexStale,
                    SemanticPercent = status.SemanticPercent
                }
            };

            // Map structured results
            foreach (var exploreResult in result.Results)
            {
                response.Results.Add(ToProtoExploreResult(exploreResult));
            }

            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Explore request was canceled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explore query failed");
            return new ExploreResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }


    public override async Task<ExplainResponse> Explain(ExplainRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrWhiteSpace(request.Question))
            {
                return new ExplainResponse
                {
                    Success = false,
                    Error = "Question cannot be empty."
                };
            }

            if (request.TokenBudget <= 0)
            {
                return new ExplainResponse
                {
                    Success = false,
                    Error = "token_budget must be a positive integer."
                };
            }

            var scope = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope;
            var notReady = await CheckScopeReadinessAsync(scope, request.Readiness, context.CancellationToken).ConfigureAwait(false);
            if (notReady is not null)
                return new ExplainResponse { Success = false, Error = notReady };

            var status = GetTrustSignal(0, context.CancellationToken);
            if (_inferenceProvider?.Available != true)
            {
                return new ExplainResponse
                {
                    Success = false,
                    Error = "Inference service not configured (set inference.service_url and cloud.api_key)"
                };
            }

            string searchKeywords;
            if (!string.IsNullOrWhiteSpace(request.Keywords))
            {
                searchKeywords = request.Keywords.Trim();
            }
            else
            {
                var extracted = await _inferenceProvider.CompleteAsync(
                    new InferenceRequest
                    {
                        Prompt = BuildKeywordExtractionPrompt(request.Question),
                        Effort = InferenceEffort.Low
                    },
                    context.CancellationToken).ConfigureAwait(false);
                searchKeywords = string.IsNullOrWhiteSpace(extracted.Content)
                    ? request.Question
                    : extracted.Content.Trim();
            }

            var query = new ExploreQuery(
                TokenBudget: 50_000,
                Breadth: 2,
                Scope: scope,
                Keywords: searchKeywords,
                Boost: null,
                Penalize: null,
                Limit: null);

            var result = await _exploreOrchestrator.ExecuteAsync(query, status, context.CancellationToken, sw).ConfigureAwait(false);

            var treeUri = string.IsNullOrWhiteSpace(scope) ? "file://** => tree: folders" : $"{scope} => tree: folders";
            var treeResult = await _readOrchestrator.ExecuteAsync(treeUri, 8_000, status, context.CancellationToken).ConfigureAwait(false);
            var treeContext = treeResult.Success && !string.IsNullOrWhiteSpace(treeResult.RenderedOutput)
                ? $"## Codebase structure\n\n{treeResult.RenderedOutput}\n\n## Search results\n\n{result.RenderedOutput}"
                : result.RenderedOutput;

            var toolCallLog = new List<(string Uri, int Tokens, bool IsError)>();
            var synthesized = await _inferenceProvider.CompleteWithToolsAsync(
                new InferenceRequest
                {
                    System = ExplainSystemPrompt,
                    Context = treeContext,
                    Prompt = request.Question,
                    Effort = InferenceEffort.High,
                    MaxTokens = Math.Max(500, request.TokenBudget)
                },
                new ToolOptions
                {
                    Tools = [InferenceReadToolDefinitionFactory.Create()],
                    ToolTokenBudget = _inferenceSettings.ToolTokenBudget,
                    MaxRounds = _inferenceSettings.MaxRounds
                },
                async (toolCall, ct) =>
                {
                    var uri = TryExtractToolCallUri(toolCall);
                    var toolResult = await ExecuteExplainReadToolAsync(toolCall, status, ct).ConfigureAwait(false);
                    toolCallLog.Add((uri ?? toolCall.Tool, toolResult.TokensUsed, toolResult.IsError));
                    return toolResult;
                },
                context.CancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(synthesized.Reasoning))
                _logger.LogDebug("Explain reasoning trace: {Reasoning}", synthesized.Reasoning);

            var contextTokens = TokenEstimator.EstimateTokens(result.RenderedOutput);
            var synthesis = new ExplainSynthesis
            {
                InputTokens = synthesized.InputTokens,
                OutputTokens = synthesized.OutputTokens,
                MatchCount = result.Results.Count,
                ContextTokens = contextTokens
            };
            foreach (var (uri, tokens, isError) in toolCallLog)
                synthesis.ToolCalls.Add(new ExplainToolCall { Uri = uri, TokensUsed = tokens, IsError = isError });

            return new ExplainResponse
            {
                Success = true,
                RenderedOutput = $"## {request.Question}\n\n{synthesized.Content}",
                Synthesis = synthesis,
                Status = new ExploreIndexerStatus
                {
                    IndexPending = status.IndexPending,
                    SemanticReady = status.SemanticReady,
                    Ready = status.IndexPending == 0 && status.SemanticReady,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    IndexTotal = status.IndexTotal,
                    IndexFailed = status.IndexFailed,
                    IndexStale = status.IndexStale,
                    SemanticPercent = status.SemanticPercent
                }
            };
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Explain request was canceled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explain request failed");
            return new ExplainResponse
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
            var status = GetTrustSignal(0, context.CancellationToken);

            // Execute
            var result = await _readOrchestrator.ExecuteAsync(
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
                Status = new ExploreIndexerStatus
                {
                    IndexPending = status.IndexPending,
                    SemanticReady = status.SemanticReady,
                    Ready = status.IndexPending == 0,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    IndexTotal = status.IndexTotal,
                    IndexFailed = status.IndexFailed,
                    IndexStale = status.IndexStale,
                    SemanticPercent = status.SemanticPercent
                }
            };
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Read request was canceled."));
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
    /// Check scope readiness per the ScopeReadiness enum.
    /// Returns null if ready (or forced), an error message if not ready and readiness is NONE.
    /// Blocks if readiness is WAIT until all in-scope files are indexed + have structure embeddings.
    /// </summary>
    private async Task<string?> CheckScopeReadinessAsync(
        string? scope,
        ScopeReadinessMode readiness,
        CancellationToken ct)
    {
        if (_uriRegistry is null)
            return null; // No registry — can't check, proceed optimistically

        var scopeStatus = _uriRegistry.CheckScope(scope);
        if (scopeStatus.IsIndexed && scopeStatus.PendingEmbedding.Count == 0)
            return null; // Fully ready

        return readiness switch
        {
            ScopeReadinessMode.Force => null,

            ScopeReadinessMode.Wait => await WaitForScopeReadyAsync(scope, ct).ConfigureAwait(false),

            // NONE (default) — fail with actionable error
            _ => FormatScopeNotReadyError(scopeStatus, scope)
        };
    }

    private async Task<string?> WaitForScopeReadyAsync(string? scope, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        var maxDelay = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            var status = _uriRegistry!.CheckScope(scope);
            if (status.IsIndexed && status.PendingEmbedding.Count == 0)
                return null;

            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }

        ct.ThrowIfCancellationRequested();
        return null; // unreachable
    }

    private static string FormatScopeNotReadyError(ScopeReadiness scopeStatus, string? scope)
    {
        var parts = new List<string>();
        parts.Add($"Scope not ready: {scope ?? "(all files)"}");
        parts.Add($"{scopeStatus.TotalFiles} files in scope, {scopeStatus.IndexedCount} indexed, {scopeStatus.EmbeddedCount} embedded");

        if (scopeStatus.PendingIndex.Count > 0)
            parts.Add($"{scopeStatus.PendingIndex.Count} pending indexing");
        if (scopeStatus.PendingEmbedding.Count > 0)
            parts.Add($"{scopeStatus.PendingEmbedding.Count} pending embedding");
        if (scopeStatus.FailedFiles.Count > 0)
            parts.Add($"{scopeStatus.FailedFiles.Count} failed");

        parts.Add("Repeat the request to wait for readiness.");
        return string.Join(". ", parts);
    }

    private TrustSignal GetTrustSignal(long executionTimeMs, CancellationToken ct)
    {
        if (_uriRegistry is not null)
        {
            try
            {
                var summary = _uriRegistry.GetSummary();
                return TrustSignal.FromSummary(summary, executionTimeMs, _embeddingMode != EmbeddingMode.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to compute trust signal from UriRegistry summary. Falling back to diagnostics.");
            }
        }

        try
        {
            var rows = _db.Query("SELECT indexing_diagnostics() as diag", ct);
            var text = rows.FirstOrDefault()?.TryGetValue("diag", out var val) == true ? val?.ToString() : null;

            if (!string.IsNullOrEmpty(text))
            {
                var values = ParseKeyValueText(text);

                var hotPathDepth = values.TryGetValue("hot_path_depth", out var hp) && int.TryParse(hp, out var hpv) ? hpv : 0;
                var idlePending = values.TryGetValue("idle_pending", out var ip) && int.TryParse(ip, out var ipv) ? ipv : 0;
                var analysisDepth = values.TryGetValue("analysis_depth", out var ad) && int.TryParse(ad, out var adv) ? adv : 0;
                var writerPending = values.TryGetValue("writer_pending", out var wp) && int.TryParse(wp, out var wpv) ? wpv : 0;
                var embedEnabled = values.TryGetValue("query_embed_enabled", out var ee) && bool.TryParse(ee, out var eev) && eev;

                return TrustSignal.FromDiagnostics(hotPathDepth, idlePending, analysisDepth, writerPending, executionTimeMs, embedEnabled);
            }
        }
        catch
        {
            // Fall back to unknown status on any error
        }

        return TrustSignal.FromDiagnostics(
            hotPathDepth: 0,
            idlePending: 0,
            analysisDepth: 0,
            writerPending: 0,
            executionTimeMs,
            embedEnabled: _embeddingMode != EmbeddingMode.None);
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

    private static ExploreResultItem ToProtoExploreResult(ExploreResult result)
    {
        var item = new ExploreResultItem
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
                item.Children.Add(ToProtoExploreResult(child));
            }
        }

        return item;
    }

    private static string BuildKeywordExtractionPrompt(string question)
        => $"""
            Extract search keywords from this question. Return ONLY space-separated keywords, no explanation.
            Include technical terms, class names, function names that might appear in code.

            Question: {question}

            Keywords:
            """;

    private async Task<ToolCallResult> ExecuteExplainReadToolAsync(
        ToolCall toolCall,
        TrustSignal status,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(toolCall.Tool, "read", StringComparison.Ordinal))
        {
            var content = $"Unsupported tool: {toolCall.Tool}";
            return new ToolCallResult
            {
                Content = content,
                IsError = true,
                TokensUsed = TokenEstimator.EstimateTokens(content)
            };
        }

        ExplainReadToolArguments? args;
        try
        {
            args = JsonSerializer.Deserialize<ExplainReadToolArguments>(toolCall.ArgumentsJson, PreviewJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var content = $"Malformed read tool arguments: {ex.Message}";
            return new ToolCallResult
            {
                Content = content,
                IsError = true,
                TokensUsed = TokenEstimator.EstimateTokens(content)
            };
        }

        if (string.IsNullOrWhiteSpace(args?.UriGlob))
        {
            var content = "read uriGlob is required";
            return new ToolCallResult
            {
                Content = content,
                IsError = true,
                TokensUsed = TokenEstimator.EstimateTokens(content)
            };
        }

        if (args.TokenBudget <= 0)
        {
            var content = "read tokenBudget must be a positive integer";
            return new ToolCallResult
            {
                Content = content,
                IsError = true,
                TokensUsed = TokenEstimator.EstimateTokens(content)
            };
        }

        if (args.UriGlob.Contains("=> question:", StringComparison.OrdinalIgnoreCase))
        {
            var content = "read => question: is not allowed during inference tool execution";
            return new ToolCallResult
            {
                Content = content,
                IsError = true,
                TokensUsed = TokenEstimator.EstimateTokens(content)
            };
        }

        try
        {
            var result = await _readOrchestrator.ExecuteAsync(
                args.UriGlob,
                args.TokenBudget,
                status,
                cancellationToken).ConfigureAwait(false);

            var content = result.Success
                ? result.RenderedOutput ?? string.Empty
                : result.Error ?? "Read execution failed.";

            return new ToolCallResult
            {
                Content = content,
                IsError = !result.Success,
                TokensUsed = TokenEstimator.EstimateTokens(content)
            };
        }
        catch (Exception ex)
        {
            var content = ex.Message;
            return new ToolCallResult
            {
                Content = content,
                IsError = true,
                TokensUsed = TokenEstimator.EstimateTokens(content)
            };
        }
    }

    private static string? TryExtractToolCallUri(ToolCall toolCall)
    {
        try
        {
            var args = JsonSerializer.Deserialize<ExplainReadToolArguments>(toolCall.ArgumentsJson, PreviewJsonOptions);
            return args?.UriGlob;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ExplainReadToolArguments(string? UriGlob, int TokenBudget);
}
