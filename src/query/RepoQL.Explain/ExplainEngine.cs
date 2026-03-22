using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Inference;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.Explain;

/// <summary>
/// Purpose: Synthesize answers to natural-language questions about a codebase.
/// Complexity: Orchestrates keyword extraction (LLM), broad explore search, tree context,
/// and multi-round LLM synthesis with tool use. No transport knowledge — pure business logic.
/// </summary>
public sealed class ExplainEngine : IExplainEngine
{
    private readonly ExploreOrchestrator _explore;
    private readonly ReadOrchestrator _read;
    private readonly IInferenceProvider _inference;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public ExplainEngine(
        ExploreOrchestrator explore,
        ReadOrchestrator read,
        IInferenceProvider inference,
        ILogger<ExplainEngine>? logger = null)
    {
        _explore = explore;
        _read = read;
        _inference = inference;
        _logger = logger ?? NullLogger<ExplainEngine>.Instance;
    }

    public async Task<ExplainResult> ExecuteAsync(
        ExplainRequest request,
        TrustSignal status,
        CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.Question))
            return ExplainResult.Failure("Question cannot be empty.");

        if (request.TokenBudget <= 0)
            return ExplainResult.Failure("token_budget must be a positive integer.");

        if (!_inference.Available)
            return ExplainResult.Failure("Inference service not configured (set inference.service_url and cloud.api_key)");

        // Step 1: Extract keywords if not provided
        string searchKeywords;
        if (!string.IsNullOrWhiteSpace(request.Keywords))
        {
            searchKeywords = request.Keywords.Trim();
        }
        else
        {
            var extracted = await _inference.CompleteAsync(
                new InferenceRequest
                {
                    Prompt = BuildKeywordExtractionPrompt(request.Question),
                    Effort = InferenceEffort.Low
                },
                cancel).ConfigureAwait(false);
            searchKeywords = string.IsNullOrWhiteSpace(extracted.Content)
                ? request.Question
                : extracted.Content.Trim();
        }

        // Step 2: Broad explore search
        var query = new ExploreQuery(
            TokenBudget: 50_000,
            Breadth: 2,
            Scope: request.Scope,
            Keywords: searchKeywords,
            Boost: null,
            Penalize: null,
            Limit: null);

        var exploreResult = await _explore.ExecuteAsync(query, status, cancel, sw).ConfigureAwait(false);

        // Step 3: Get tree context for structure awareness
        var treeUri = string.IsNullOrWhiteSpace(request.Scope)
            ? "file://** => tree: folders"
            : $"{request.Scope} => tree: folders";
        var treeResult = await _read.ExecuteAsync(treeUri, 8_000, status, cancel).ConfigureAwait(false);
        var treeContext = treeResult.Success && !string.IsNullOrWhiteSpace(treeResult.RenderedOutput)
            ? $"## Codebase structure\n\n{treeResult.RenderedOutput}\n\n## Search results\n\n{exploreResult.RenderedOutput}"
            : exploreResult.RenderedOutput;

        // Step 4: LLM synthesis with tool use
        var toolCallLog = new List<ExplainToolCall>();
        var synthesized = await _inference.CompleteWithToolsAsync(
            new InferenceRequest
            {
                System = SystemPrompt,
                Context = treeContext,
                Prompt = request.Question,
                Effort = InferenceEffort.High,
                MaxTokens = Math.Max(500, request.TokenBudget)
            },
            new ToolOptions
            {
                Tools = request.Tools ?? [],
                ToolTokenBudget = request.ToolTokenBudget,
                MaxRounds = request.MaxRounds
            },
            async (toolCall, ct) =>
            {
                var toolResult = await ExecuteReadToolAsync(toolCall, status, ct).ConfigureAwait(false);
                var uri = TryExtractToolCallUri(toolCall);
                toolCallLog.Add(new ExplainToolCall
                {
                    Uri = uri ?? toolCall.Tool,
                    TokensUsed = toolResult.TokensUsed,
                    IsError = toolResult.IsError
                });
                return toolResult;
            },
            cancel).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(synthesized.Reasoning))
            _logger.LogDebug("Explain reasoning trace: {Reasoning}", synthesized.Reasoning);

        var contextTokens = TokenEstimator.EstimateTokens(exploreResult.RenderedOutput);

        return new ExplainResult
        {
            Success = true,
            RenderedOutput = $"## {request.Question}\n\n{synthesized.Content}",
            MatchCount = exploreResult.Results.Count,
            ContextTokens = contextTokens,
            InputTokens = synthesized.InputTokens,
            OutputTokens = synthesized.OutputTokens,
            ToolCalls = toolCallLog,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private async Task<ToolCallResult> ExecuteReadToolAsync(
        ToolCall toolCall,
        TrustSignal status,
        CancellationToken ct)
    {
        if (!string.Equals(toolCall.Tool, "read", StringComparison.Ordinal))
            return MakeToolError($"Unsupported tool: {toolCall.Tool}");

        ReadToolArguments? args;
        try
        {
            args = JsonSerializer.Deserialize<ReadToolArguments>(toolCall.ArgumentsJson, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return MakeToolError($"Malformed read tool arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(args?.UriGlob))
            return MakeToolError("read uriGlob is required");

        if (args.TokenBudget <= 0)
            return MakeToolError("read tokenBudget must be a positive integer");

        if (args.UriGlob.Contains("=> question:", StringComparison.OrdinalIgnoreCase))
            return MakeToolError("read => question: is not allowed during inference tool execution");

        try
        {
            var result = await _read.ExecuteAsync(args.UriGlob, args.TokenBudget, status, ct).ConfigureAwait(false);
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
            return MakeToolError(ex.Message);
        }
    }

    private static string? TryExtractToolCallUri(ToolCall toolCall)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolCall.ArgumentsJson);
            if (doc.RootElement.TryGetProperty("uriGlob", out var uri) ||
                doc.RootElement.TryGetProperty("uri", out uri))
                return uri.GetString();
        }
        catch { /* best effort */ }
        return null;
    }

    private static ToolCallResult MakeToolError(string message) => new()
    {
        Content = message,
        IsError = true,
        TokensUsed = TokenEstimator.EstimateTokens(message)
    };

    private static string BuildKeywordExtractionPrompt(string question)
        => $"""
            Extract search keywords from this question. Return ONLY space-separated keywords, no explanation.
            Include technical terms, class names, function names that might appear in code.

            Question: {question}

            Keywords:
            """;

    private sealed class ReadToolArguments
    {
        public string? UriGlob { get; set; }
        public int TokenBudget { get; set; }
    }

    #region System Prompt

    internal const string SystemPrompt = """
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

    #endregion
}

