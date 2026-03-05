using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Tools;

/// <summary>
/// MCP tool for LLM-synthesized understanding of codebase concepts.
///
/// Purpose: Agents ask a question and get a prose answer with citations.
/// Unlike explore (which returns structured results for the agent to read),
/// explain searches wide internally (50k+ tokens), then synthesizes via LLM.
///
/// Complexity: Extracts keywords from question via LLM, calls explore with high budget
/// for deep context, then synthesizes a focused answer via LLM. Falls back to raw
/// explore results if LLM is not configured.
/// </summary>
[McpServerToolType]
internal sealed class ExplainTool(
    RepoQlClientProvider clientProvider,
    SelfTestRunner selfTestRunner,
    SessionOrientation sessionOrientation,
    ILlmProvider llmProvider)
{
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    private readonly SelfTestRunner _selfTestRunner = selfTestRunner ?? throw new ArgumentNullException(nameof(selfTestRunner));
    private readonly SessionOrientation _sessionOrientation = sessionOrientation ?? throw new ArgumentNullException(nameof(sessionOrientation));
    private readonly ILlmProvider _llmProvider = llmProvider ?? throw new ArgumentNullException(nameof(llmProvider));

    // Track last request to implement "call again to wait" pattern (static to persist across tool invocations)
    private static string? _lastRequestSignature;

    private const string ToolInstructions = """
        <WHY>
        You want understanding, not raw text. Explain searches wide (often 50k tokens of context),
        then an LLM synthesizes a focused answer with citations.
        </WHY>

        <WHEN_TO_USE>
        Use explain when you have a specific question about the codebase.
        Use explore when you need to find, survey or read it yourself — explain is for synthesis with evidence.

        Good questions:
        ✓ "How does JWT refresh work in TokenService?"
        ✓ "What authentication methods does service X support?"
        ✓ "Why does PaymentProcessor use idempotency keys?"
        ✓ "What happens when an item is indexed, step by step?"
        ✓ "What are the failure modes for startup of service X?"

        Bad questions:
        ✗ "Explain everything about authentication" (too broad)
        ✗ "What does this service do?" (no referent — use uriGlob to scope it)
        </WHEN_TO_USE>

        <OUTPUT>
        Returns:
        - Answer: Synthesized explanation
        - Evidence: Code snippets with file:///path#line=N,M citations and reasoning
        - Nuance: Caveats and related considerations

        For high stakes questions, verify citations — read the actual lines to confirm.
        </OUTPUT>

        <PARAMETERS>
        **question** (required): The question you want answered. Full sentences work best.

        **tokenBudget** (optional, default 2000): How many tokens to invest in the response.
        Even small budgets produce rich answers. Minimum effective budget is 1000.
        </PARAMETERS>

        <DISTINCTION_FROM_READ_QUESTION>
        - read("file:///path => question: ...") synthesizes from files YOU specify
        - explain searches wide and decides what's relevant — you don't have to know where to look
        </DISTINCTION_FROM_READ_QUESTION>
        """;

    [McpServerTool(Name = "explain", Title = "Explain", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false), Description(ToolInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<CallToolResult> ExplainAsync(
        [Description("The question you want answered — full sentences work best (e.g., \"How does authentication work in MyProduct?\")")] string question,
        [Description("URI glob to scope the search (e.g., file:///src/Auth/**). Omit to search everywhere - this should be your default choice.")] string? uriGlob = null,
        [Description("Token budget for the response (default 2000)")] int tokenBudget = 2000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return ToolResult.Error("Error: question is required. Provide the question you want answered.");

        if (tokenBudget <= 0)
            return ToolResult.Error("Error: tokenBudget must be a positive integer.");

        // Check orientation
        var orientationFooter = _sessionOrientation.CheckOrientation(uriGlob);

        // Create request signature for "call again to wait" pattern
        var requestSignature = $"explain|{tokenBudget}|{uriGlob}|{question}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Explain always requires semantic search (question is always provided)
        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var scopeStatus = await client.GetScopeReadinessAsync(uriGlob, cancellationToken).ConfigureAwait(false);

        if (!scopeStatus.IsReady && !isRepeatRequest)
        {
            // First time seeing this request while scope not ready - return status and instructions
            _lastRequestSignature = requestSignature;
            return ToolResult.Success(RepoQlClientScopeExtensions.FormatScopeNotReadyMessage(scopeStatus, uriGlob));
        }

        if (!scopeStatus.IsReady && isRepeatRequest)
        {
            // Repeat request - wait until scope is ready
            await client.WaitForScopeAsync(uriGlob, cancellationToken).ConfigureAwait(false);
        }

        // Clear the pending request now that we're executing
        _lastRequestSignature = null;

        // Execute: extract keywords → explore wide → synthesize via LLM
        try
        {
            client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);

            // Step 1: Extract search keywords from the question via LLM
            var searchKeywords = question;
            if (_llmProvider.Enabled)
            {
                var extracted = await _llmProvider.ExtractKeywordsAsync(question, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extracted))
                    searchKeywords = extracted;
            }

            // Step 2: Explore with high budget for LLM context (low breadth = deep structure)
            const int llmContextBudget = 50_000;
            var response = await client.ExploreAsync(
                llmContextBudget,
                2, // low breadth = deep, few results with full structure
                uriGlob,
                searchKeywords,
                boost: null,
                penalize: null,
                limit: null,
                cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                return ToolResult.Error($"Error: {response.Error}");
            }

            // Step 3: Synthesize via LLM, or fall back to raw results
            if (!_llmProvider.Enabled)
            {
                return ToolResult.Success(
                    "[LLM not configured — showing raw explore results. Set OPENROUTER_API_KEY for synthesized answers.]\n\n"
                    + response.RenderedOutput + orientationFooter);
            }

            var synthesized = await _llmProvider.SummarizeAsync(
                response.RenderedOutput,
                question,
                maxTokens: Math.Max(500, tokenBudget),
                repoTree: null,
                ct: cancellationToken).ConfigureAwait(false);

            // Extract footer from explore response (the [tok | time | status] line)
            var footer = ExtractFooter(response.RenderedOutput);

            return ToolResult.Success(
                $"## {question}\n\n{synthesized}\n\n---\n{footer}" + orientationFooter);
        }
        catch (Exception ex)
        {
            if (ex is RepoQlDiagnosticsException diagnosticsException)
            {
                return ToolResult.Error($"Error: Explain failed. {ExtractErrorMessage(ex)}\n\n{diagnosticsException.Diagnostics}");
            }

            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancellationToken);
                return ToolResult.Error($"Error: Explain failed. {ExtractErrorMessage(ex)}\n\n{diagnostics}");
            }
            return ToolResult.Error($"Error: Explain failed. {ExtractErrorMessage(ex)}");
        }
    }

    /// <summary>
    /// Extracts the status footer line (e.g. "[1.8k tok | 1.1s | index: ready]") from explore output.
    /// </summary>
    private static string ExtractFooter(string exploreOutput)
    {
        // Footer is the last line starting with '['
        var lines = exploreOutput.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('[') && trimmed.Contains("tok"))
                return trimmed;
        }
        return string.Empty;
    }

    private static string ExtractErrorMessage(Exception ex)
    {
        if (ex is Grpc.Core.RpcException rpcEx)
        {
            var detail = rpcEx.Status.Detail;
            if (!string.IsNullOrWhiteSpace(detail))
                return detail;
        }

        if (ex.InnerException is not null)
        {
            var inner = ExtractErrorMessage(ex.InnerException);
            if (!string.IsNullOrWhiteSpace(inner) && inner != ex.Message)
                return $"{ex.Message} -> {inner}";
        }

        return ex.Message;
    }
}
