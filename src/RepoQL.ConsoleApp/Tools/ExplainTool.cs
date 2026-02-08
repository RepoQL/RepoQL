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
/// Complexity: Thin MCP layer. Validates parameters, handles scope readiness,
/// delegates to the same gRPC ExploreAsync with Intent.Explain. The orchestrator
/// handles keyword extraction, search, LLM synthesis, and citation formatting.
/// </summary>
[McpServerToolType]
internal sealed class ExplainTool(
    RepoQlClientProvider clientProvider,
    SelfTestRunner selfTestRunner,
    SessionOrientation sessionOrientation)
{
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    private readonly SelfTestRunner _selfTestRunner = selfTestRunner ?? throw new ArgumentNullException(nameof(selfTestRunner));
    private readonly SessionOrientation _sessionOrientation = sessionOrientation ?? throw new ArgumentNullException(nameof(sessionOrientation));

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

    [McpServerTool(Name = "explain", Title = "Explain Code", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false), Description(ToolInstructions)]
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

        // Execute via server — always Explain intent
        try
        {
            client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.ExploreAsync(
                tokenBudget,
                ExploreIntent.Explain,
                uriGlob,
                question,
                boost: null,
                penalize: null,
                limit: null,
                cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                return ToolResult.Error($"Error: {response.Error}");
            }

            return ToolResult.Success(response.RenderedOutput + orientationFooter);
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
