using System.ComponentModel;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class ExploreTool(
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

        ### Capsule: ExploreIntent
        **Invariant**
        Intent matches knowledge state: Inventory (discovery), Locate (location), Inspect (structure), Explain (synthesis).
        **Example**
        Inventory  → tokenBudget=1000 keywords="payment" scope="file:///docs/**"
        Locate     → tokenBudget=1500 keywords="settlement batch" boost="(?i)payment"
        Inspect  → tokenBudget=3000 keywords="reconciliation logic"
        Explain → tokenBudget=2000 keywords="Why does TokenService use refresh tokens?"
        **Depth**
        - All intents accept: tokenBudget, keywords, scope, boost, penalize
        - Inventory: keywords optional (ranks when present); broad results
        - Locate: keywords required; ranked results with snippets
        - Inspect: keywords required; deep structure with line numbers
        - Explain: keywords required as question; prose synthesis
        - Budget by intent: Inventory 800-2000, Locate 1000-2000, Inspect 2000-5000, Explain 1000-3000
        - Workflow: Inventory→Locate→Inspect→Explain (accumulates knowledge)
        ---
        
        ### Capsule: XRayTargeting
        **Invariant**
        Keywords target semantically; boost/penalize adjust ranking (regex); scope filters path (glob).
        **Example**
        keywords="authentication flow"              semantic targeting
        boost="(?i)oauth|jwt|session"               elevate matches
        penalize="(?i)test|mock|fixture"            demote matches
        scope="file:///docs/service/**/*.md"        path filter
        **Depth**
        - All parameters work with all intents
        - Keywords: 2-5 word phrases; question format for Understand
        - boost/penalize: RE2 regex (`(?i)` case-insensitive, `|` alternation)
        - scope: glob pattern (`*` single level, `**` recursive, `*.md` extension)
        - boost adjusts ranking; scope excludes—choose based on need
        ---
        
        ### Capsule: ExplainNarrow
        **Invariant**
        When using explain, queries must be self-contained; keywords become search terms directly.
        **Example**
        ✓ "What is AuthService responsible for?"
        ✓ "Why does PaymentProcessor use idempotency keys?"
        ✗ "Explain everything about authentication"
        ✗ "What does this service do?"
        **Depth**
        - No pronouns or references—explore has no conversation context
        - Include entity names, service names, specific concepts
        - Broad queries dilute relevance; focused queries concentrate it
        - Derivation section shows evidence; verify citations before trusting
        ---

        <KNOBS>
        tokenBudget:
            investment level → more tokens = richer detail. You set the budget, explore maximizes value.
            if you want to be sure you found everything, set a high budget
            important: budget is exactly how many tokens you want to spend on seeing the answer, it is not a maximum.
            the underlying query is the same regardless of budget - budget controls the level of detail in the response, and attempts to maximize value given the budget and intent

        keywords: search terms for hybrid (semantic + lexical) search
          - Questions + boost patterns work best: keywords="How does auth work?" boost="(?i)Auth.*|Validate.*"
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
        Inventory → Locate → Inspect workflow:
        1. tokenBudget=1000, intent=inventory, scope=file:///src/** → See what modules exist
        2. tokenBudget=1200, intent=locate, keywords="authentication validation" → Locate auth code
        3. tokenBudget=2000, intent=inspect, scope=file:///src/Auth/**, keywords="JWT validation" → Read the code

        Quick references:
        - What docs exist? → intent=inventory, scope=help://**
        - Understand architecture → intent=inventory, scope=file:///src/**, keywords="How is this organized?"
        - Find a feature → intent=locate, keywords="Where is caching implemented?"
        - Debug specific code → intent=inspect, scope=file:///path/to/file.cs, keywords="error handling"
        - Get synthesized explanation → intent=explain, keywords="How does authentication work?"
        </EXAMPLES>

        <REMEMBER>
        Start with INVENTORY when you don't know the codebase vocabulary yet.
        Use LOCATE once you know what concepts/terms to search for.
        Use INSPECT only after LOCATE has shown you which files matter.
        Use EXPLAIN when you want a prose explanation synthesized by LLM.
        Each intent serves a different knowledge state - don't skip steps.
        </REMEMBER>
        """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "explore"), Description(ToolInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> ExploreAsync(
        [Description("Tokens to invest in the response")] int tokenBudget,
        [Description("What you are trying to do - see INTENT_SELECTION")] Intent intent,
        [Description("Where to look (glob pattern), full uri, semicolon delimited list of uris")] string? scope = null,
        [Description("Search terms for hybrid search - full sentences work best (e.g., \"How does JWT token refresh work?\")")] string? keywords = null,
        [Description("Regex patterns to boost matches, comma-separated (e.g., \"Validate.*Token,(?i)auth\")")] string? boost = null,
        [Description("Regex patterns to de-rank matches, comma-separated (e.g., \"(?i)test|mock,\\.generated\\.\")")] string? penalize = null,
        [Description("Cap results shown - used with token budget to decide how things are displayed. Leave blank to have explore optimize it.")] int? limit = null,
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

                Call explore again with the same arguments to wait for indexing to complete before executing.
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
            Intent.Inventory => ExploreIntent.Inventory,
            Intent.Locate => ExploreIntent.Locate,
            Intent.Inspect => ExploreIntent.Inspect,
            Intent.Explain => ExploreIntent.Explain,
            _ => ExploreIntent.Inventory
        };

        // Execute via server
        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.ExploreAsync(
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
            if (ex is RepoQlDiagnosticsException diagnosticsException)
            {
                return $"Error: Search failed. {ExtractErrorMessage(ex)}\n\n{diagnosticsException.Diagnostics}";
            }

            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancellationToken);
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
