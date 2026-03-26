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
using RepoQL.FileSystem.Physical;
using RepoQL.Indexing.Hosting;
using RepoQL.Explore;
using RepoQL.Read;
using RepoQL.Sandbox;
using RepoQL.Query;
using RepoQL.Import;
using RepoQL.Indexing.Indexing.PostProcessing;
using ProtoPipelineStage = RepoQL.Contracts.PipelineStage;
using ProtoPipelineStatus = RepoQL.Contracts.PipelineStatus;
using ProtoStageStatus = RepoQL.Contracts.StageStatus;
using EngineImportRequest = RepoQL.Import.ImportRequest;

namespace RepoQL.ConsoleApp.Host;

public sealed class RepoQlServiceImpl : Contracts.RepoQL.RepoQLBase
{
    private readonly DuckDbDataStore _db;
    private readonly RepositoryConfiguration repoConfig;
    private readonly IIndexingCoordinator coordinator;
    private readonly IImportEngine _importEngine;
    private readonly IEmbeddingCoordinator _embeddingCoordinator;
    private readonly DocumentPreviewService _previewService;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IInferenceProvider? _inferenceProvider;
    private readonly ExploreOrchestrator _exploreOrchestrator;
    private readonly ReadOrchestrator _readOrchestrator;
    private readonly IReadContentProvider _readContentProvider;
    private readonly IWasmSandbox? _wasmSandbox;
    private readonly GraphvizRenderer? _graphvizRenderer;
    private readonly PandocRenderer? _pandocRenderer;
    private readonly SvgRenderer? _svgRenderer;
    private readonly IModuleRegistry? _moduleRegistry;
    private readonly UriRegistry? _uriRegistry;
    private readonly QueueCommandService? _queueCommandService;
    private readonly QueryEngine _queryEngine;
    private readonly Explain.ExplainEngine _explainEngine;
    private readonly RepoQlConfig.HostSettings _hostSettings;
    private readonly RepoQlConfig.EmbeddingSettings _embeddingSettings;
    private readonly RepoQlConfig.InferenceSettings _inferenceSettings;
    private readonly RepoQlConfig.SandboxSettings _sandboxSettings;
    private readonly DashboardQueryActivityTracker? _dashboardQueryActivity;
    private readonly ILogger<RepoQlServiceImpl> _logger;
    private readonly SemaphoreSlim _queryConcurrency;

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
        IImportEngine importEngine,
        IEmbeddingCoordinator embeddingCoordinator,
        DocumentPreviewService previewService,
        IHostApplicationLifetime hostLifetime,
        ExploreOrchestrator exploreOrchestrator,
        ReadOrchestrator readOrchestrator,
        IReadContentProvider readContentProvider,
        Explain.ExplainEngine explainEngine,
        QueryEngine queryEngine,
        RepoQlConfig config,
        IEmbeddingProvider? embeddingProvider = null,
        IInferenceProvider? inferenceProvider = null,
        IWasmSandbox? wasmSandbox = null,
        GraphvizRenderer? graphvizRenderer = null,
        PandocRenderer? pandocRenderer = null,
        SvgRenderer? svgRenderer = null,
        IModuleRegistry? moduleRegistry = null,
        UriRegistry? uriRegistry = null,
        QueueCommandService? queueCommandService = null,
        DashboardQueryActivityTracker? dashboardQueryActivity = null,
        ILogger<RepoQlServiceImpl>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        this.repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));
        _embeddingProvider = embeddingProvider;
        _inferenceProvider = inferenceProvider;
        _wasmSandbox = wasmSandbox;
        _graphvizRenderer = graphvizRenderer;
        _pandocRenderer = pandocRenderer;
        _svgRenderer = svgRenderer;
        _moduleRegistry = moduleRegistry;
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _importEngine = importEngine ?? throw new ArgumentNullException(nameof(importEngine));
        _embeddingCoordinator = embeddingCoordinator ?? throw new ArgumentNullException(nameof(embeddingCoordinator));
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _hostLifetime = hostLifetime ?? throw new ArgumentNullException(nameof(hostLifetime));
        _exploreOrchestrator = exploreOrchestrator ?? throw new ArgumentNullException(nameof(exploreOrchestrator));
        _readOrchestrator = readOrchestrator ?? throw new ArgumentNullException(nameof(readOrchestrator));
        _readContentProvider = readContentProvider ?? throw new ArgumentNullException(nameof(readContentProvider));
        _explainEngine = explainEngine ?? throw new ArgumentNullException(nameof(explainEngine));
        _queryEngine = queryEngine ?? throw new ArgumentNullException(nameof(queryEngine));
        _uriRegistry = uriRegistry;
        _queueCommandService = queueCommandService;
        _hostSettings = (config ?? throw new ArgumentNullException(nameof(config))).Host;
        _embeddingSettings = config.Embedding;
        _inferenceSettings = config.Inference;
        _sandboxSettings = config.Sandbox;
        _dashboardQueryActivity = dashboardQueryActivity;
        _logger = logger ?? NullLogger<RepoQlServiceImpl>.Instance;
        var maxConcurrent = Math.Clamp(_hostSettings.MaxConcurrentQueries ?? 15, 1, 64);
        _queryConcurrency = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public override async Task<RecheckCloudLoginStateResponse> RecheckCloudLoginState(
        RecheckCloudLoginStateRequest request,
        ServerCallContext context)
    {
        try
        {
            var refreshTriggered = await _embeddingCoordinator
                .RecheckActiveEmbeddingModelAsync(context.CancellationToken)
                .ConfigureAwait(false);
            return new RecheckCloudLoginStateResponse
            {
                RefreshTriggered = refreshTriggered
            };
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Cloud login state recheck was canceled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cloud login state recheck failed");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<RawQueryResponse> ExecuteRawQuery(RawQueryRequest request, ServerCallContext context)
    {
        await _queryConcurrency.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteRawQueryCore(request, context).ConfigureAwait(false);
        }
        finally
        {
            _queryConcurrency.Release();
        }
    }

    private async Task<RawQueryResponse> ExecuteRawQueryCore(RawQueryRequest request, ServerCallContext context)
    {
        // No barrier - queries execute immediately with whatever data is available.
        // ExploreTool handles "call again to wait" pattern for semantic readiness.
        var resp = new RawQueryResponse();
        var activity = _dashboardQueryActivity?.Begin("query", SummarizeSqlForDashboard(request.Sql), request.TokenBudget);

        try
        {
            var engineRequest = new QueryRequest
            {
                Sql = request.Sql,
                Parameters = request.Parameters.Select(ToQueryParameter).ToList(),
                TokenBudget = request.TokenBudget,
                Limit = request.Limit
            };

            var result = await _queryEngine.ExecuteAsync(engineRequest, context.CancellationToken).ConfigureAwait(false);

            // Map QueryResult back to proto RawQueryResponse
            foreach (var col in result.Columns)
                resp.Columns.Add(new ColumnSchema { Name = col.Name, DbType = col.TypeName });

            foreach (var row in result.Rows)
            {
                var rd = new RowData();
                foreach (var value in row)
                    rd.Values.Add(ToProtoValue(value));
                resp.Rows.Add(rd);
            }

            resp.RowCount = result.RowCount;
            resp.Truncated = result.Truncated;
            resp.Summarized = result.Summarized;
            resp.OriginalRowCount = result.OriginalRowCount;
            resp.ExecutionTimeMs = result.ElapsedMs;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            activity?.Cancel("Cancelled");
            throw new RpcException(new Status(StatusCode.Cancelled, "Query request was canceled."));
        }
        catch (Exception ex)
        {
            activity?.Cancel(SummarizeDashboardText(ex.Message));
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }

        var trustSignal = GetTrustSignal(resp.ExecutionTimeMs, context.CancellationToken);
        resp.IndexPending = trustSignal.IndexPending;
        resp.IndexTotal = trustSignal.IndexTotal;
        resp.IndexFailed = trustSignal.IndexFailed;
        resp.IndexStale = trustSignal.IndexStale;
        resp.SemanticEnabled = trustSignal.SemanticEnabled;
        resp.SemanticReady = trustSignal.SemanticReady;
        resp.SemanticPercent = trustSignal.SemanticPercent;

        return activity?.Complete(resp, BuildQueryActivitySummary(resp), EstimateQueryActivityTokens(resp)) ?? resp;
    }
    /// <summary>
    /// Format response as text for token estimation.
    /// Used by EstimateQueryActivityTokens and TryBudgetSummarize.
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

    /// <summary>
    /// Check token budget and LLM-summarize the response if it exceeds the budget and intent is available.
    /// Used by the Execute RPC (sandbox). The query RPC delegates to QueryEngine instead.
    /// </summary>
    private async Task TryBudgetSummarize(RawQueryResponse resp, int tokenBudget, string? intent, CancellationToken ct)
    {
        if (tokenBudget <= 0 || resp.Rows.Count == 0)
            return;

        var formatted = FormatResponseForTokenEstimation(resp);
        var estimatedTokens = TokenEstimator.EstimateTokens(formatted);

        if (estimatedTokens <= tokenBudget)
            return;

        if (string.IsNullOrWhiteSpace(intent) || _inferenceProvider is not { Available: true })
            return;

        try
        {
            var originalRowCount = resp.RowCount;
            var summary = await _inferenceProvider.CompleteAsync(
                new InferenceRequest
                {
                    Context = formatted,
                    Prompt = intent,
                    MaxTokens = tokenBudget
                },
                ct).ConfigureAwait(false);

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
            _logger.LogWarning(ex, "LLM summarization failed, returning original response");
        }
    }

    public override async Task<RawQueryResponse> Execute(ExecuteRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();
        var activity = _dashboardQueryActivity?.Begin("execute",
            SummarizeDashboardText(request.Code, 60), request.TokenBudget);

        try
        {
            if (_wasmSandbox is null)
            {
                activity?.Cancel("Sandbox unavailable");
                throw new RpcException(new Status(StatusCode.Unavailable,
                    "WASM sandbox is not available. Ensure the QuickJS WASM module is built and embedded."));
            }

            var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : 300_000;
            var scopeEnforcer = new SandboxScopeEnforcer(
                ParseSandboxScopes(_sandboxSettings.ReadScopes),
                ParseSandboxScopes(_sandboxSettings.WriteScopes),
                ParseSandboxScopes(_sandboxSettings.DeleteScopes));
            var capabilities = new SandboxCapabilities
            {
                QueryHandler = sql =>
                {
                    var rows = _db.Query(sql, context.CancellationToken);
                    return System.Text.Json.JsonSerializer.Serialize(rows);
                },
                ReadHandler = (uri, budget) =>
                {
                    try
                    {
                        scopeEnforcer.EnforceRead(uri);

                        var documents = _readContentProvider
                            .FetchGlobAsync(uri, context.CancellationToken)
                            .GetAwaiter()
                            .GetResult();

                        var content = RenderSandboxReadContent(uri, documents);
                        var tokensUsed = TokenEstimator.EstimateTokens(content);

                        // Use JsonObject instead of anonymous types — anonymous types
                        // are stripped by the IL trimmer in Release builds, causing
                        // JsonSerializer.Serialize to produce "{}".
                        var result = new System.Text.Json.Nodes.JsonObject
                        {
                            ["content"] = content,
                            ["representation"] = "full",
                            ["tokensUsed"] = tokensUsed
                        };
                        return result.ToJsonString();
                    }
                    catch (Exception ex)
                    {
                        var error = new System.Text.Json.Nodes.JsonObject
                        {
                            ["__repoqlReadError"] = ex.Message
                        };
                        return error.ToJsonString();
                    }
                },
                WriteHandler = (uri, content) =>
                {
                    try
                    {
                        scopeEnforcer.EnforceWrite(uri);
                        var path = ResolveSandboxFilePath(uri);
                        var directory = Path.GetDirectoryName(path);
                        if (!string.IsNullOrWhiteSpace(directory))
                            Directory.CreateDirectory(directory);

                        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        return null;
                    }
                    catch (SandboxScopeException ex)
                    {
                        return ex.Message;
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                },
                DeleteHandler = uri =>
                {
                    try
                    {
                        scopeEnforcer.EnforceDelete(uri);
                        var path = ResolveSandboxFilePath(uri);
                        File.Delete(path);
                        return null;
                    }
                    catch (SandboxScopeException ex)
                    {
                        return ex.Message;
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                },
                ModuleLoaderHandler = specifier =>
                {
                    var builtinSource = BuiltinModuleProvider.Load(specifier);
                    if (builtinSource is not null) return builtinSource;

                    if (_moduleRegistry is null) return null;
                    return _moduleRegistry.LoadSource(specifier);
                },
                FfmpegHandler = new FfmpegProcessRunner(
                    _sandboxSettings.FfmpegPath,
                    ResolveSandboxFilePath,
                    scopeEnforcer.EnforceRead,
                    scopeEnforcer.EnforceWrite,
                    _sandboxSettings.FfmpegTimeoutMs ?? 300_000).Execute,
                GraphvizHandler = _graphvizRenderer is not null
                    ? (dot, engine, format) => _graphvizRenderer.Render(dot, engine, format)
                    : null,
                PandocHandler = _pandocRenderer is not null
                    ? argsJson => ExecutePandocFromJson(argsJson)
                    : null,
                SvgToPngHandler = _svgRenderer is not null
                    ? (svg, width) =>
                    {
                        // width param is scale factor: 0 or 1 = native, 2 = 2x, etc.
                        var scale = width > 0 ? (float)width : 2.0f;
                        return Convert.ToBase64String(_svgRenderer.RenderToPng(svg, scale));
                    }
                    : null
            };
            var input = string.IsNullOrEmpty(request.Input) ? null : request.Input;
            var result = _wasmSandbox.Execute(request.Code, input, timeoutMs, capabilities, context.CancellationToken);

            RawQueryResponse resp;
            if (!result.Success)
            {
                resp = new RawQueryResponse { SandboxError = true };
                resp.Columns.Add(new ColumnSchema { Name = "error", DbType = "VARCHAR" });
                var errorRow = new RowData();
                var errorMsg = FormatSandboxError(result, request.Code);
                errorRow.Values.Add(Value.ForString(errorMsg));
                resp.Rows.Add(errorRow);
                resp.RowCount = 1;
            }
            else
            {
                resp = JsonResultMapper.MapToResponse(result.JsonOutput).ToProto();
                resp.RawJsOutput = result.JsonOutput ?? string.Empty;
            }

            sw.Stop();
            resp.ExecutionTimeMs = sw.ElapsedMilliseconds;

            // Budget overflow — same path as query
            await TryBudgetSummarize(resp, request.TokenBudget, request.Intent, context.CancellationToken)
                .ConfigureAwait(false);

            // Trust signal
            var trustSignal = GetTrustSignal(resp.ExecutionTimeMs, context.CancellationToken);
            resp.IndexPending = trustSignal.IndexPending;
            resp.IndexTotal = trustSignal.IndexTotal;
            resp.IndexFailed = trustSignal.IndexFailed;
            resp.IndexStale = trustSignal.IndexStale;
            resp.SemanticEnabled = trustSignal.SemanticEnabled;
            resp.SemanticReady = trustSignal.SemanticReady;
            resp.SemanticPercent = trustSignal.SemanticPercent;

            return activity?.Complete(resp, BuildQueryActivitySummary(resp), EstimateQueryActivityTokens(resp)) ?? resp;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            activity?.Cancel("Cancelled");
            throw new RpcException(new Status(StatusCode.Cancelled, "Execute request was canceled."));
        }
        catch (RpcException)
        {
            throw; // Re-throw gRPC errors (like our "unavailable" above)
        }
        catch (Exception ex)
        {
            activity?.Cancel(SummarizeDashboardText(ex.Message));
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    private static IReadOnlyList<string>? ParseSandboxScopes(string? configValue)
    {
        if (string.IsNullOrWhiteSpace(configValue))
            return null;

        var scopes = configValue
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static scope => !string.IsNullOrWhiteSpace(scope))
            .ToArray();

        return scopes.Length == 0 ? null : scopes;
    }

    private static string RenderSandboxReadContent(string requestedUri, IReadOnlyList<ReadDocument> documents)
    {
        if (documents.Count == 0)
            throw new InvalidOperationException($"No content found for '{requestedUri}'.");

        if (documents.Count == 1)
        {
            var singleContent = documents[0].TextContent;
            if (string.IsNullOrEmpty(singleContent))
                throw new InvalidOperationException($"No text content available for '{documents[0].Uri}'.");

            return singleContent;
        }

        var builder = new StringBuilder();
        var included = 0;
        foreach (var document in documents)
        {
            if (string.IsNullOrEmpty(document.TextContent))
                continue;

            if (builder.Length > 0)
                builder.Append("\n\n");

            builder.AppendLine($"--- {document.Uri} ---");
            builder.Append(document.TextContent);
            included++;
        }

        if (included == 0)
            throw new InvalidOperationException($"No text content available for '{requestedUri}'.");

        return builder.ToString();
    }

    private string ResolveSandboxFilePath(string uri)
    {
        var canonicalUri = CanonicalizeSandboxFileUri(uri);
        if (!RepoUri.TryParse(canonicalUri, out var repoUri))
            throw new InvalidOperationException($"Invalid URI: {uri}");

        return FileUriPathResolver.ToAbsolutePath(repoConfig.Path, repoUri);
    }

    private static string FormatSandboxError(WasmExecutionResult result, string sourceCode)
    {
        var kind = result.ErrorKind ?? "runtime";
        var message = result.ErrorMessage ?? "Unknown error";

        // Drop the generic suggestion — it's noise. Only keep genuinely helpful suggestions.
        var genericSuggestion = "Check your JavaScript syntax and logic";
        var suggestion = result.ErrorSuggestion;
        if (string.Equals(suggestion, genericSuggestion, StringComparison.Ordinal))
            suggestion = null;

        // Provide context-specific guidance based on error content
        if (suggestion is null)
        {
            if (kind == "syntax")
                suggestion = "Fix the JavaScript syntax error above";
            else if (kind == "timeout")
                suggestion = "Simplify the script or increase the timeout";
            else if (kind == "memory")
                suggestion = "Reduce data size or process in smaller chunks";
            else if (kind == "cancelled")
                suggestion = "Retry the request";
            else if (message.Contains("not defined", StringComparison.Ordinal))
                suggestion = "Check variable and function names";
            else if (message.Contains("not a function", StringComparison.Ordinal))
                suggestion = "The value is not callable — check the type";
            else if (message.Contains("No content found", StringComparison.Ordinal))
                suggestion = "Use explore to find the correct URI, or check that the file exists";
            else if (message.Contains("denied", StringComparison.OrdinalIgnoreCase))
                suggestion = "Adjust scopes via ::config.set";
            else if (message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                suggestion = "Check ffmpeg arguments and file paths";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(message);

        // Show the source line with ^ indicator (like DuckDB errors)
        if (!string.IsNullOrEmpty(result.ErrorStack) && !string.IsNullOrEmpty(sourceCode))
        {
            var (line, col) = ParseStackLocation(result.ErrorStack);
            if (line > 0)
            {
                var lines = sourceCode.Split('\n');
                if (line <= lines.Length)
                {
                    var sourceLine = lines[line - 1].TrimEnd('\r');
                    sb.Append("\n\nLINE ").Append(line).Append(": ").Append(sourceLine);
                    if (col > 0 && col <= sourceLine.Length + 1)
                    {
                        sb.Append('\n').Append(' ', "LINE : ".Length + line.ToString().Length);
                        sb.Append(' ', col - 1).Append('^');
                    }
                }
            }
        }

        if (suggestion is not null)
        {
            sb.Append("\n\n> ");
            sb.Append(suggestion);
        }

        // Include diagnostics (console.log/warn/error output) if any
        if (result.Diagnostics.Count > 0)
        {
            sb.Append("\n\nDiagnostics:\n");
            foreach (var diag in result.Diagnostics)
            {
                var prefix = diag.Level switch
                {
                    "warn" => "[warn] ",
                    "error" => "[error] ",
                    _ => ""
                };
                sb.Append(prefix).AppendLine(diag.Message);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parse the first source location from a QuickJS stack trace.
    /// Format: "at funcName (<eval>:LINE:COL)"
    /// </summary>
    private static (int line, int col) ParseStackLocation(string stack)
    {
        // Match patterns like "<eval>:3:22" or "(<eval>:5:1)"
        var match = System.Text.RegularExpressions.Regex.Match(stack, @"<eval>:(\d+):(\d+)");
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var line) &&
            int.TryParse(match.Groups[2].Value, out var col))
        {
            return (line, col);
        }
        return (0, 0);
    }

    private string ExecutePandocFromJson(string argsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            var input = root.TryGetProperty("input", out var inputProp) ? inputProp.GetString() ?? "" : "";
            var from = root.TryGetProperty("from", out var fromProp) ? fromProp.GetString() ?? "markdown" : "markdown";
            var to = root.TryGetProperty("to", out var toProp) ? toProp.GetString() ?? "html" : "html";

            List<string>? extraArgs = null;
            if (root.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                extraArgs = [];
                foreach (var arg in argsProp.EnumerateArray())
                    if (arg.ValueKind == System.Text.Json.JsonValueKind.String)
                        extraArgs.Add(arg.GetString()!);
            }

            var output = _pandocRenderer!.Convert(input, from, to, extraArgs);

            var result = new System.Text.Json.Nodes.JsonObject
            {
                ["output"] = output,
                ["from"] = from,
                ["to"] = to
            };
            return result.ToJsonString();
        }
        catch (Exception ex)
        {
            return "{\"__repoqlPandocError\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
        }
    }

    private string CanonicalizeSandboxFileUri(string uri)
    {
        var trimmed = uri.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var canonical = trimmed;
        if (!string.IsNullOrWhiteSpace(parsed.Host))
        {
            var normalizedPath = $"{parsed.Host.Trim('/')}{parsed.AbsolutePath}".Replace('\\', '/');
            canonical = $"file:///{normalizedPath.TrimStart('/')}";
        }

        return CanonicalizeRepositoryUri(canonical, repoConfig.Path);
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
                // If already repo-relative (no drive letter), return directly.
                // Absolute Windows paths (e.g. C:/...) must fall through to relativization.
                if (!string.IsNullOrEmpty(relOnly) && (relOnly.Length < 2 || relOnly[1] != ':'))
                    return $"file:///{relOnly.Replace('\\', '/')}";
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

    /// <summary>
    /// Map a protobuf Value parameter to a transport-agnostic QueryParameter.
    /// </summary>
    private static QueryParameter ToQueryParameter(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.NullValue => new QueryParameter { Kind = QueryParameterKind.Null },
        Value.KindOneofCase.BoolValue => new QueryParameter { Kind = QueryParameterKind.Bool, BoolValue = v.BoolValue },
        Value.KindOneofCase.NumberValue => new QueryParameter { Kind = QueryParameterKind.Number, NumberValue = v.NumberValue },
        Value.KindOneofCase.StringValue => new QueryParameter { Kind = QueryParameterKind.String, StringValue = v.StringValue },
        _ => new QueryParameter { Kind = QueryParameterKind.Null }
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

        try
        {
            await coordinator.WaitForPipelineAsync(stages, request.WaitAll, context.CancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, ex.Message));
        }

        var snapshot = coordinator.GetPipelineStatus();
        return new WaitForPipelineResponse { Status = ToProtoStatus(snapshot) };
    }

    public override async Task<ImportResponse> ImportRepository(Contracts.ImportRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();
        var uri = request.Uri?.Trim() ?? "";

        try
        {
            var engineRequest = new EngineImportRequest
            {
                Uri = uri,
                Analyze = request.Analyze
            };

            Import.ImportResult result;
            try
            {
                result = await _importEngine.ExecuteAsync(engineRequest, context.CancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }

            if (!result.Success)
            {
                var code = result.Action == ImportAction.Failed ? StatusCode.InvalidArgument : StatusCode.Internal;
                throw new RpcException(new Status(code, result.Error ?? "Import failed."));
            }

            var snapshot = coordinator.GetPipelineStatus();
            _logger.LogInformation("[Import] Completed {Action} for {Uri} in {ElapsedMs}ms",
                result.Action, uri, sw.ElapsedMilliseconds);

            var response = new ImportResponse
            {
                Status = ToProtoStatus(snapshot),
                TotalFiles = result.TotalFiles,
                IndexedCount = result.IndexedCount,
                FailedCount = result.FailedCount,
                Message = result.Message
            };

            if (result.OperationId is not null)
                response.OperationId = result.OperationId;

            // Preserve existing post-import embedding refresh behavior without blocking the import response.
            var operation = result.Operation;
            if (operation is not null && _embeddingProvider is { Enabled: true } provider)
            {
                var db = _db;
                var logger = _logger;
                var settings = _embeddingSettings;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await operation.Completion.ConfigureAwait(false);
                        var refresher = new EmbeddingRefresher(db, EmbeddingMode.Full, logger, settings);
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
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Import] Cancelled for {Uri} after {ElapsedMs}ms", uri, sw.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Import] Unexpected error for {Uri} after {ElapsedMs}ms", uri, sw.ElapsedMilliseconds);
            throw new RpcException(new Status(StatusCode.Internal, $"Import failed: {ex.Message}"));
        }
    }

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
        await _queryConcurrency.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await ExploreCore(request, context).ConfigureAwait(false);
        }
        finally
        {
            _queryConcurrency.Release();
        }
    }

    private async Task<ExploreResponse> ExploreCore(ExploreRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();
        var activity = _dashboardQueryActivity?.Begin("explore", SummarizeExploreParameters(request), request.TokenBudget);

        try
        {
            var scope = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope;
            var notReady = await CheckScopeReadinessAsync(scope, request.Readiness, context.CancellationToken).ConfigureAwait(false);
            if (notReady is not null)
            {
                var failure = new ExploreResponse { Success = false, Error = notReady };
                return activity?.Fail(failure, notReady, EstimateResponseTokens(notReady)) ?? failure;
            }

            var status = GetTrustSignal(0, context.CancellationToken);

            // Build query
            var query = new ExploreQuery(
                TokenBudget: request.TokenBudget > 0 ? request.TokenBudget : 2000,
                BudgetTier: request.TokenBudget <= 0 ? "medium" : null,
                Breadth: request.Breadth > 0 ? Math.Clamp(request.Breadth, 1, 10) : 5,
                AutoBreadth: request.Breadth <= 0,
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

            return activity?.Complete(response, BuildExploreActivitySummary(response), EstimateResponseTokens(response.RenderedOutput)) ?? response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            activity?.Cancel("Cancelled");
            throw new RpcException(new Status(StatusCode.Cancelled, "Explore request was canceled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explore query failed");
            var failure = new ExploreResponse
            {
                Success = false,
                Error = ex.Message
            };
            return activity?.Fail(failure, ex.Message, EstimateResponseTokens(ex.Message)) ?? failure;
        }
    }


    public override async Task<ExplainResponse> Explain(ExplainRequest request, ServerCallContext context)
    {
        await _queryConcurrency.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await ExplainCore(request, context).ConfigureAwait(false);
        }
        finally
        {
            _queryConcurrency.Release();
        }
    }

    private async Task<ExplainResponse> ExplainCore(ExplainRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();
        var activity = _dashboardQueryActivity?.Begin("explain", SummarizeExplainParameters(request), request.TokenBudget);

        try
        {
            if (string.IsNullOrWhiteSpace(request.Question))
            {
                var failure = new ExplainResponse
                {
                    Success = false,
                    Error = "Question cannot be empty."
                };
                return activity?.Fail(failure, failure.Error, EstimateResponseTokens(failure.Error)) ?? failure;
            }

            if (request.TokenBudget <= 0)
            {
                var failure = new ExplainResponse
                {
                    Success = false,
                    Error = "token_budget must be a positive integer."
                };
                return activity?.Fail(failure, failure.Error, EstimateResponseTokens(failure.Error)) ?? failure;
            }

            var scope = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope;
            var notReady = await CheckScopeReadinessAsync(scope, request.Readiness, context.CancellationToken).ConfigureAwait(false);
            if (notReady is not null)
            {
                var failure = new ExplainResponse { Success = false, Error = notReady };
                return activity?.Fail(failure, notReady, EstimateResponseTokens(notReady)) ?? failure;
            }

            var trustSignal = GetTrustSignal(0, context.CancellationToken);

            var engineRequest = new Explain.ExplainRequest
            {
                Question = request.Question,
                Keywords = string.IsNullOrWhiteSpace(request.Keywords) ? null : request.Keywords,
                Scope = scope,
                TokenBudget = request.TokenBudget,
                Tools = [InferenceReadToolDefinitionFactory.Create()],
                ToolTokenBudget = _inferenceSettings.ToolTokenBudget,
                MaxRounds = _inferenceSettings.MaxRounds
            };

            var result = await _explainEngine.ExecuteAsync(engineRequest, trustSignal, context.CancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                var failure = new ExplainResponse
                {
                    Success = false,
                    Error = result.Error ?? ""
                };
                return activity?.Fail(failure, failure.Error, EstimateResponseTokens(failure.Error)) ?? failure;
            }

            var synthesis = new ExplainSynthesis
            {
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                MatchCount = result.MatchCount,
                ContextTokens = result.ContextTokens
            };
            foreach (var tc in result.ToolCalls)
                synthesis.ToolCalls.Add(new Contracts.ExplainToolCall
                    { Uri = tc.Uri, TokensUsed = tc.TokensUsed, IsError = tc.IsError });

            var response = new ExplainResponse
            {
                Success = true,
                RenderedOutput = result.RenderedOutput ?? "",
                Synthesis = synthesis,
                Status = new ExploreIndexerStatus
                {
                    IndexPending = trustSignal.IndexPending,
                    SemanticReady = trustSignal.SemanticReady,
                    Ready = trustSignal.IndexPending == 0 && trustSignal.SemanticReady,
                    ElapsedMs = result.ElapsedMs,
                    IndexTotal = trustSignal.IndexTotal,
                    IndexFailed = trustSignal.IndexFailed,
                    IndexStale = trustSignal.IndexStale,
                    SemanticPercent = trustSignal.SemanticPercent
                }
            };

            return activity?.Complete(response, BuildExplainActivitySummary(response), EstimateExplainActivityTokens(response)) ?? response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            activity?.Cancel("Cancelled");
            throw new RpcException(new Status(StatusCode.Cancelled, "Explain request was canceled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explain request failed");
            var failure = new ExplainResponse
            {
                Success = false,
                Error = ex.Message
            };
            return activity?.Fail(failure, ex.Message, EstimateResponseTokens(ex.Message)) ?? failure;
        }
    }

    public override async Task<ReadResponse> Read(ReadRequest request, ServerCallContext context)
    {
        await _queryConcurrency.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCore(request, context).ConfigureAwait(false);
        }
        finally
        {
            _queryConcurrency.Release();
        }
    }

    private async Task<ReadResponse> ReadCore(ReadRequest request, ServerCallContext context)
    {
        var sw = Stopwatch.StartNew();
        var activity = _dashboardQueryActivity?.Begin("read", SummarizeReadParameters(request), request.TokenBudget);

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

            var response = new ReadResponse
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

            if (!response.Success)
                return activity?.Fail(response, response.Error, EstimateResponseTokens(response.Error)) ?? response;

            return activity?.Complete(response, BuildReadActivitySummary(response), EstimateResponseTokens(response.RenderedOutput)) ?? response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            activity?.Cancel("Cancelled");
            throw new RpcException(new Status(StatusCode.Cancelled, "Read request was canceled."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Read operation failed");
            var failure = new ReadResponse
            {
                Success = false,
                Error = ex.Message
            };
            return activity?.Fail(failure, ex.Message, EstimateResponseTokens(ex.Message)) ?? failure;
        }
    }


    private static string SummarizeSqlForDashboard(string sql)
    {
        var withoutLineComments = Regex.Replace(sql, @"--.*?(?=\r?\n|$)", " ");
        var withoutComments = Regex.Replace(withoutLineComments, @"/\*[\s\S]*?\*/", " ");
        return SummarizeDashboardText(withoutComments, 96);
    }

    private static string SummarizeExploreParameters(ExploreRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Question))
            parts.Add(request.Question);
        else if (!string.IsNullOrWhiteSpace(request.Keywords))
            parts.Add(request.Keywords);

        if (!string.IsNullOrWhiteSpace(request.Scope))
            parts.Add(request.Scope);

        return SummarizeDashboardText(string.Join(" · ", parts.Where(static part => !string.IsNullOrWhiteSpace(part))), 96);
    }

    private static string SummarizeExplainParameters(ExplainRequest request)
        => SummarizeDashboardText(request.Question, 96);

    private static string SummarizeReadParameters(ReadRequest request)
        => SummarizeDashboardText(request.Uri, 96);

    private static string BuildQueryActivitySummary(RawQueryResponse response)
    {
        if (response.Summarized)
        {
            return response.OriginalRowCount > 0
                ? $"Summarized {response.OriginalRowCount} rows"
                : "Summarized result";
        }

        return response.Truncated
            ? $"{response.RowCount}+ rows"
            : $"{response.RowCount} rows";
    }

    private static string BuildExploreActivitySummary(ExploreResponse response)
    {
        if (!response.Success)
            return SummarizeDashboardText(response.Error, 96);

        return response.Truncated
            ? $"{response.Results.Count}+ results"
            : $"{response.Results.Count} results";
    }

    private static string BuildExplainActivitySummary(ExplainResponse response)
    {
        if (!response.Success)
            return SummarizeDashboardText(response.Error, 96);

        if (response.Synthesis is not null && response.Synthesis.MatchCount > 0)
            return $"{response.Synthesis.MatchCount} matches";

        return "Answer generated";
    }

    private static string BuildReadActivitySummary(ReadResponse response)
    {
        if (!response.Success)
            return SummarizeDashboardText(response.Error, 96);

        var parts = new List<string>();
        if (response.FilesRead > 0)
            parts.Add($"{response.FilesRead} files");
        if (!string.IsNullOrWhiteSpace(response.Representation))
            parts.Add(response.Representation);

        return parts.Count > 0 ? string.Join(" · ", parts) : "Read complete";
    }

    private static int EstimateQueryActivityTokens(RawQueryResponse response)
    {
        if (response.Rows.Count == 0 && response.Columns.Count == 0)
            return 0;

        return TokenEstimator.EstimateTokens(FormatResponseForTokenEstimation(response));
    }

    private static int EstimateExplainActivityTokens(ExplainResponse response)
    {
        if (response.Synthesis is not null && response.Synthesis.OutputTokens > 0)
            return (int)response.Synthesis.OutputTokens;

        return EstimateResponseTokens(response.Success ? response.RenderedOutput : response.Error);
    }

    private static int EstimateResponseTokens(string? content)
        => string.IsNullOrWhiteSpace(content) ? 0 : TokenEstimator.EstimateTokens(content);

    private static string SummarizeDashboardText(string? text, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "(none)";

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..Math.Max(1, maxLength - 3)] + "...";
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
                return TrustSignal.FromSummary(summary, executionTimeMs, _embeddingProvider is { Enabled: true });
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
            embedEnabled: _embeddingProvider is { Enabled: true });
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

}



