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
        Don't read blind. Traditional search finds most results—you answer confidently but with gaps. Users run subagents to verify, wasting tokens. Explore searches wide first, so you see what exists before answering. No blind spots, no verification tax.
        </WHY>

        <INTENT>
        Intent matches your knowledge state—and controls how tokens are spent.

        **Inventory**: What exists here?
        Survey mode—see into files through headlines and tags without reading them. Breadth over depth. Keywords optional; when provided, they rank results but you still see the full landscape.
        → tokenBudget=1000, uriGlob="file:///src/**"
        → tokenBudget=1000, uriGlob="file:///docs/**", keywords="failure", boost="(?i)error" (ranked survey)

        **Locate**: You know the concept, not the location.
        Balanced—detail on matches, awareness of the rest. Enough context to decide what to read next.
        → tokenBudget=1500, keywords="authentication validation"

        **Inspect**: You know the target.
        Depth with context—concentrates tokens on relevant content and its surroundings. Shows code snippets, line numbers.
        → tokenBudget=2500, uriGlob="file:///src/Auth/**", keywords="token validation"

        **Explain**: You want understanding, not raw text.
        Massive compression—an LLM reads far more than you'd spend (often 50k → 1k). Returns synthesis, source URIs, and reasoning—verifiable, not a black box.
        → tokenBudget=2000, keywords="How does JWT refresh work in TokenService?"

        Workflow: Inventory → Locate → Inspect → Explain (accumulate knowledge, don't skip steps)
        </INTENT>

        <PARAMETERS>
        **tokenBudget** (required): How many tokens you're willing to spend. This is a bet—you don't know exactly what you'll get.
        - Start low (500-1000) and increase if you need more
        - Different scopes, intents, and content will consume budget differently
        - The tool maximizes value within your budget, but outcomes vary
        Consider the stakes: if an incomplete answer has serious consequences, bet more. When the cost of being wrong is low, bet small and iterate.

        **keywords**: Semantic + lexical search terms.
        - Concepts: "authentication flow", "error handling"
        - Questions work best for Explain: "How does X work?"
        - Optional for Inventory (survey mode)

        **uriGlob**: Filter by path. Use Inventory first to learn structure, then narrow.
        - file:///src/** — all source
        - file:///src/**/*.cs — C# files only
        - file:///src/**;!**/tests/** — exclude tests
        - help://** — embedded documentation
        - Combine with ; exclude with !

        **boost**: Regex to elevate matches (demotes others relatively).
        - (?i)interface|abstract — find contracts
        - (?i)service|handler — find entry points
        - Auth.*|Token.* — specific patterns

        **penalize**: Regex to demote matches (doesn't exclude, just ranks lower).
        - (?i)test|mock|spec|fake — demote test code
        - (?i)generated|\\.g\\. — demote generated
        - Case-insensitive: (?i), alternation: |
        </PARAMETERS>

        <LAYERED_APPROACH>
        Combine parameters for precision:
        1. uriGlob filters WHERE (path matching)
        2. keywords finds WHAT (semantic search)
        3. boost ranks UP (elevate matches)
        4. penalize ranks DOWN (demote matches)

        Example: Find authentication implementations, not tests:
        → intent=Locate, uriGlob="file:///src/**", keywords="authentication", penalize="(?i)test|mock"

        Example: Find interfaces in a specific area:
        → intent=Locate, uriGlob="file:///src/Services/**", keywords="service", boost="(?i)interface|abstract"
        </LAYERED_APPROACH>

        <EXPLAIN_TIPS>
        Explain queries must be self-contained (no conversation context):
        ✓ "What is AuthService responsible for?"
        ✓ "Why does PaymentProcessor use idempotency keys?"
        ✗ "Explain everything about authentication" (too broad)
        ✗ "What does this service do?" (no referent)

        Output includes:
        - Answer: Synthesized explanation
        - Evidence: Code snippets with file:///path#line=N,M citations
        - Nuance: Caveats and related considerations

        Always verify citations—read the actual lines to confirm.
        </EXPLAIN_TIPS>

        <QUICK_PATTERNS>
        Orient in new codebase:
        → intent=Inventory, uriGlob="file:///src/**", tokenBudget=1000

        Ranked survey (see everything, relevant stuff first):
        → intent=Inventory, uriGlob="file:///docs/**", boost="(?i)failure|error", tokenBudget=1000

        Find where something is:
        → intent=Locate, keywords="caching layer", tokenBudget=1500

        Understand specific code:
        → intent=Inspect, uriGlob="file:///src/Cache/**", keywords="invalidation", tokenBudget=2500

        Get explanation with evidence:
        → intent=Explain, keywords="How does the caching layer handle invalidation?", tokenBudget=2500

        Find production code only:
        → intent=Locate, keywords="database connection", penalize="(?i)test|mock", tokenBudget=1500

        Find contracts/interfaces:
        → intent=Locate, keywords="service", boost="(?i)interface|abstract", tokenBudget=1500
        </QUICK_PATTERNS>

        <WHEN_TO_USE_READ>
        Explore finds URIs. Read fetches content. The workflow:
        1. explore(intent=Locate, keywords="validation") → returns URIs with symbols
        2. read("file:///src/Auth.cs#symbol=ValidateToken;file:///src/Token.cs#symbol=Refresh;file:///src/Session.cs#symbol=Check", 3000)
           → fetches just those 3 function bodies in one call

        Other read patterns:
        - read("file:///src/Auth.cs", 3000) — whole file
        - read("file:///src/** => tree: folders", 500) — directory structure
        - read("file:///src/Auth.cs => question: How does this handle expiry?", 2000) — LLM synthesis

        Explore when you don't know where. Read when you have URIs.
        </WHEN_TO_USE_READ>
        """;

    [McpServerTool(Name = "explore", Title = "Explore Repository", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false), Description(ToolInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> ExploreAsync(
        [Description("Tokens to invest in the response")] int tokenBudget,
        [Description("What you are trying to do - see INTENT_SELECTION")] Intent intent,
        [Description("URI glob pattern to filter results (e.g., file:///src/**, help://**). Combine with ; exclude with !")] string? uriGlob = null,
        [Description("Search terms for hybrid search - full sentences work best (e.g., \"How does JWT token refresh work?\")")] string? keywords = null,
        [Description("Regex patterns to boost matches, comma-separated (e.g., \"Validate.*Token,(?i)auth\")")] string? boost = null,
        [Description("Regex patterns to de-rank matches, comma-separated (e.g., \"(?i)test|mock,\\.generated\\.\")")] string? penalize = null,
        [Description("Cap results shown - used with token budget to decide how things are displayed. Leave blank to have explore optimize it.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (tokenBudget <= 0)
            return "Error: tokenBudget must be a positive integer.";

        // Check orientation
        var nudge = _sessionOrientation.CheckOrientation("explore", uriGlob);
        if (nudge != null)
            return nudge;

        // Create request signature for "call again to wait" pattern
        var requestSignature = $"{tokenBudget}|{intent}|{uriGlob}|{keywords}|{boost}|{penalize}|{limit}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Only check scope readiness if doing semantic search (keywords provided)
        var requiresSemantic = !string.IsNullOrWhiteSpace(keywords);

        if (requiresSemantic)
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var scopeStatus = await client.GetScopeReadinessAsync(uriGlob, cancellationToken).ConfigureAwait(false);

            if (!scopeStatus.IsReady && !isRepeatRequest)
            {
                // First time seeing this request while scope not ready - return status and instructions
                _lastRequestSignature = requestSignature;
                return RepoQlClientScopeExtensions.FormatScopeNotReadyMessage(scopeStatus, uriGlob);
            }

            if (!scopeStatus.IsReady && isRepeatRequest)
            {
                // Repeat request - wait until scope is ready
                await client.WaitForScopeAsync(uriGlob, cancellationToken).ConfigureAwait(false);
            }
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
                uriGlob,  // Maps to internal 'scope' parameter
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
