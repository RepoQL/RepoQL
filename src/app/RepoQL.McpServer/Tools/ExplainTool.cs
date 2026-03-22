using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.Client.Diagnostics;
using RepoQL.McpServer.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;
using RepoQL.Client.Helpers;

namespace RepoQL.McpServer.Tools;

/// <summary>
/// MCP tool for LLM-synthesized understanding of codebase concepts.
///
/// Purpose: Agents ask a question and get a prose answer with citations.
/// Unlike explore (which returns structured results for the agent to read),
/// explain searches wide inside the host, then synthesizes a focused answer via the server-side LLM provider.
///
/// Complexity: Client-side responsibility is limited to readiness UX ("call again to wait")
/// and transport error handling. Search and synthesis happen in the host.
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

    private static string? _lastRequestSignature;

    private const string ToolInstructions = """
        <WHY>
        Explain is how you understand — it reads wide (up to 50k tokens of source) and synthesizes focused understanding with citations. You get comprehension, not raw text.

        The index is wild magic — composable, responsive to intent, and forgiving. Scope explain to specific directories with uriGlob for precise answers. Unscoped explain searches everything and may answer the wrong question. Your instincts about what to ask are probably right — try them.
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
        [Description("Search keywords — code identifiers, class names, synonyms. You know the vocabulary better than the LLM. If omitted, keywords are extracted automatically.")] string? keywords = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return ToolResult.Error("Error: question is required. Provide the question you want answered.");

        if (tokenBudget <= 0)
            return ToolResult.Error("Error: tokenBudget must be a positive integer.");

        var orientationFooter = _sessionOrientation.CheckOrientation(uriGlob);
        var requestSignature = $"explain|{tokenBudget}|{uriGlob}|{keywords}|{question}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Scope readiness: first call sends NONE, repeat sends WAIT
        var readiness = isRepeatRequest ? ScopeReadinessMode.Wait : ScopeReadinessMode.None;

        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.ExplainAsync(question, uriGlob, tokenBudget, keywords, readiness, cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                _lastRequestSignature = requestSignature;
                return ToolResult.Error($"Error: {response.Error}");
            }

            _lastRequestSignature = null;

            var footer = FormatExplainFooter(response);
            var output = string.IsNullOrWhiteSpace(footer)
                ? response.RenderedOutput
                : $"{response.RenderedOutput}\n\n---\n{footer}";

            return ToolResult.Success(output + orientationFooter);
        }
        catch (Exception ex)
        {
            _lastRequestSignature = null;

            if (ex is RepoQlDiagnosticsException diagnosticsException)
                return ToolResult.Error($"Error: Explain failed. {ExtractErrorMessage(ex)}\n\n{diagnosticsException.Diagnostics}");

            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancellationToken);
                return ToolResult.Error($"Error: Explain failed. {ExtractErrorMessage(ex)}\n\n{diagnostics}");
            }

            return ToolResult.Error($"Error: Explain failed. {ExtractErrorMessage(ex)}");
        }
    }

    private static string FormatExplainFooter(ExplainResponse response)
    {
        var parts = new List<string>();
        var status = response.Status;
        var synthesis = response.Synthesis;

        // Match count + timing
        if (synthesis is not null && synthesis.MatchCount > 0)
            parts.Add($"{synthesis.MatchCount} matches");

        if (status is not null && status.ElapsedMs > 0)
            parts.Add($"{status.ElapsedMs / 1000.0:0.#}s");

        // Synthesis ratio
        if (synthesis is not null && synthesis.InputTokens > 0)
        {
            var inK = synthesis.InputTokens / 1000.0;
            var outK = synthesis.OutputTokens / 1000.0;
            parts.Add($"synthesis: {inK:0.#}k → {outK:0.#}k");
        }

        // Only surface problems — omit "ready" states
        if (status is not null)
        {
            if (status.IndexPending > 0)
                parts.Add($"index: {status.IndexPending} pending");
            if (status.IndexFailed > 0)
                parts.Add($"{status.IndexFailed} failed");
            if (!status.SemanticReady)
                parts.Add($"semantic: {status.SemanticPercent}%");
        }

        var footerLine = parts.Count > 0 ? $"[{string.Join(" · ", parts)}]" : "";

        // Tool calls
        if (synthesis is not null && synthesis.ToolCalls.Count > 0)
        {
            var toolLines = new List<string> { $"**Tool calls** ({synthesis.ToolCalls.Count}):" };
            foreach (var tc in synthesis.ToolCalls)
            {
                var err = tc.IsError ? " (error)" : "";
                toolLines.Add($"- `read({tc.Uri})` — {tc.TokensUsed} tok{err}");
            }

            footerLine = string.IsNullOrWhiteSpace(footerLine)
                ? string.Join("\n", toolLines)
                : $"{footerLine}\n{string.Join("\n", toolLines)}";
        }

        return footerLine;
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
