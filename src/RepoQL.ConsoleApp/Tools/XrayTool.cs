using System.ComponentModel;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Xray;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class XrayTool(
    RepoQlClientProvider clientProvider,
    SelfTestRunner selfTestRunner)
{
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    private readonly SelfTestRunner _selfTestRunner = selfTestRunner ?? throw new ArgumentNullException(nameof(selfTestRunner));

    // Track last request to implement "call again to wait" pattern (static to persist across tool invocations)
    private static string? _lastRequestSignature;

    private const string ToolInstructions = """
        <CONCEPT>
        The best tool for 95% of your reading and understanding needs.
        X-ray vision files in your repo (code/docs/config/everything). See structure, find things without reading files or knowing keywords.
        </CONCEPT>

        <INTENT_SELECTION>
        CRITICAL: Choose intent based on YOUR CURRENT KNOWLEDGE STATE, not the task type.

        EXPLORE - "I don't know what exists yet"
          Use when: Starting a new investigation, understanding architecture, discovering what's available
          Output: Many results, headlines only, broad coverage
          Keywords: Optional (can explore without searching)
          Example tasks: "What's in this codebase?", "How is this organized?", "What docs exist?"

        FIND - "I know roughly what I'm looking for"
          Use when: You have a concept/feature in mind, need to locate implementations
          Output: Fewer results, headlines + structure, method signatures visible
          Keywords: Required (you must know what to search for)
          Example tasks: "Where is auth implemented?", "Find the caching logic"

        EXAMINE - "I found it, show me the details"
          Use when: You already know WHICH FILES contain what you need
          Output: Few results, full snippets with code context
          Keywords: Required (to focus on specific aspects)
          Example tasks: "Show me the validation logic in AuthService.cs"

        WORKFLOW: Almost always progress Explore → Find → Examine
          1. EXPLORE first to discover what exists and learn vocabulary
          2. FIND to locate specific implementations using discovered terms
          3. EXAMINE to read detailed code in the files you found

        ANTI-PATTERNS (common mistakes):
          ✗ Using Find/Examine for "understand the architecture" → Use Explore first
          ✗ Using Examine before you know which files matter → Use Find first
          ✗ Skipping Explore and going straight to Find → You'll miss context
          ✗ Using same intent repeatedly → Progress through the workflow
        </INTENT_SELECTION>

        <KNOBS>
        tokenBudget:
            investment level → more tokens = richer detail. You set the budget, xray maximizes value.
            if you want to be sure you found everything, set a high budget
            important: budget is exactly how many tokens you want to spend on seeing the answer, it is not a maximum.
            the underlying query is the same regardless of budget - budget controls the level of detail in the response, and attempts to maximize value given the budget and intent

        keywords: search terms for hybrid (semantic + lexical) search
          - Questions + boost patterns work best: keywords="How does auth work?" boost="Auth.*,Validate.*"
          - Questions alone find conceptually related content via semantic search
          - Control results with patterns to boost or penalize matches

        Know the uri(s) of the thing you are a looking for? Use ReadMcpResourceTool - works for objects and files, supports globbing patterns.

        Filter with scope (glob), guide with keywords (semantic), rank with patterns (regex). Results ranked by confidence.
        </KNOBS>

        <PATTERNS>
        boost: RE2 regex patterns to boost matching results (comma-separated)
          - Validate.*Token → boost results containing "ValidateToken", "ValidateAccessToken", etc.
          - (?i)error|exception → boost error handling code (case-insensitive)
          - Auth.* → boost anything starting with "Auth" (AuthService, Authentication, etc.)

        penalize: RE2 regex patterns to de-rank matching results (comma-separated)
          - (?i)test|spec|mock → de-rank test files and mocks
          - \.generated\. → de-rank generated code
          - deprecated|obsolete → de-rank deprecated code

        Note: RE2 regex (no backreferences/lookahead). Patterns applied at SQL level for true filtering.
        </PATTERNS>

        <EXAMPLES>
        Explore → Find → Examine workflow:
        1. tokenBudget=1000, intent=explore, scope=file:///src/** → See what modules exist
        2. tokenBudget=1200, intent=find, keywords="authentication validation" → Locate auth code
        3. tokenBudget=2000, intent=examine, scope=file:///src/Auth/**, keywords="JWT validation" → Read the code

        Quick references:
        - What docs exist? → intent=explore, scope=docs://**
        - Understand architecture → intent=explore, scope=file:///src/**, keywords="How is this organized?"
        - Find a feature → intent=find, keywords="Where is caching implemented?"
        - Debug specific code → intent=examine, scope=file:///path/to/file.cs, keywords="error handling"
        </EXAMPLES>

        <REMEMBER>
        Start with EXPLORE when you don't know the codebase vocabulary yet.
        Use FIND once you know what concepts/terms to search for.
        Use EXAMINE only after FIND has shown you which files matter.
        Each intent serves a different knowledge state - don't skip steps.
        </REMEMBER>
        """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "xray"), Description(ToolInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> XrayAsync(
        [Description("Tokens to invest in the response")] int tokenBudget,
        [Description("Zoom level: explore, find, or examine")] Intent intent,
        [Description("Where to look (glob pattern), full uri, semicolon delimited list of uris")] string? scope = null,
        [Description("Search terms for hybrid search - full sentences work best (e.g., \"How does JWT token refresh work?\")")] string? keywords = null,
        [Description("Regex patterns to boost matches, comma-separated (e.g., \"Validate.*Token,(?i)auth\")")] string? boost = null,
        [Description("Regex patterns to de-rank matches, comma-separated (e.g., \"(?i)test|mock,\\.generated\\.\")")] string? penalize = null,
        [Description("Cap results shown - used with token budget to decide how things are displayed. Leave blank to have xray optimize it.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (tokenBudget <= 0)
            return "Error: tokenBudget must be a positive integer.";

        // Create request signature for "call again to wait" pattern
        var requestSignature = $"{tokenBudget}|{intent}|{scope}|{keywords}|{boost}|{penalize}|{limit}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Check if indexer is ready before executing
        var preStatus = await GetIndexerStatusAsync(cancellationToken).ConfigureAwait(false);
        var needsIndex = preStatus.IndexPending > 0;
        var needsSemantic = !string.IsNullOrWhiteSpace(keywords) && !preStatus.SemanticReady;

        if ((needsIndex || needsSemantic) && !isRepeatRequest)
        {
            // First time seeing this request while not ready - return status and instructions
            _lastRequestSignature = requestSignature;
            var waitingFor = needsIndex ? $"index ({preStatus.IndexPending} pending)" : "semantic index";
            return $"""
                Indexing in progress - {waitingFor}

                Current status: {RepresentationFormatter.FormatStatusFooter(preStatus)}

                Call xray again with the same arguments to wait for indexing to complete before executing.
                """;
        }

        if ((needsIndex || needsSemantic) && isRepeatRequest)
        {
            // Repeat request - wait for ready
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var stage = needsSemantic ? PipelineStage.SemanticIndexing : PipelineStage.Indexing;
            await client.WaitForPipelineAsync(new[] { stage }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Clear the pending request now that we're executing
        _lastRequestSignature = null;

        // Map intent to proto enum
        var protoIntent = intent switch
        {
            Intent.Explore => XrayIntent.Explore,
            Intent.Find => XrayIntent.Find,
            Intent.Examine => XrayIntent.Examine,
            _ => XrayIntent.Explore
        };

        // Execute via server
        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.XrayAsync(
                tokenBudget,
                protoIntent,
                scope,
                keywords,
                boost,
                penalize,
                limit,
                cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                return $"Error: {response.Error}";
            }

            return response.RenderedOutput;
        }
        catch (Exception ex)
        {
            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(cancellationToken);
                return $"Error: Search failed. {ExtractErrorMessage(ex)}\n\n{diagnostics}";
            }
            return $"Error: Search failed. {ExtractErrorMessage(ex)}";
        }
    }

    private async Task<IndexerStatus> GetIndexerStatusAsync(CancellationToken ct)
    {
        try
        {
            var client = await _clientProvider.GetClientAsync(ct).ConfigureAwait(false);
            var result = await client.ExecuteRawQueryAsync("SELECT indexing_diagnostics() as diag", cancellationToken: ct).ConfigureAwait(false);

            if (result.Rows.Count > 0)
            {
                var text = result.Rows[0].Values.FirstOrDefault()?.StringValue;
                if (!string.IsNullOrEmpty(text))
                {
                    // Parse key-value format (key: value\n...)
                    var values = ParseKeyValueText(text);

                    var hotPathDepth = values.TryGetValue("hot_path_depth", out var hp) ? int.Parse(hp) : 0;
                    var idlePending = values.TryGetValue("idle_pending", out var ip) ? int.Parse(ip) : 0;
                    var analysisDepth = values.TryGetValue("analysis_depth", out var ad) ? int.Parse(ad) : 0;
                    var writerPending = values.TryGetValue("writer_pending", out var wp) ? int.Parse(wp) : 0;
                    var embedEnabled = values.TryGetValue("query_embed_enabled", out var ee) && bool.Parse(ee);

                    return IndexerStatus.FromDiagnostics(hotPathDepth, idlePending, analysisDepth, writerPending, 0, embedEnabled);
                }
            }
        }
        catch
        {
            // Fall back to unknown status on any error
        }

        return new IndexerStatus(0, false, false, 0);
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
