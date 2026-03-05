using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;

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

        <BREADTH>
        Breadth controls how tokens are distributed across results.
        - Low (1-3): Depth. Few results with full structure and children. Use when you know what you're looking for.
        - Medium (4-6): Balanced. Good detail on top hits, awareness of the rest. Default.
        - High (7-10): Coverage. Many results with headlines. Use for surveying what exists.
        Default is 5. Combine with tokenBudget to control the tradeoff.
        </BREADTH>

        <PARAMETERS>
        **tokenBudget** (required): How many tokens you're willing to spend. This is a bet—you don't know exactly what you'll get.
        - Start low (500-1000) and increase if you need more
        - Different scopes, breadth levels, and content will consume budget differently
        - The tool maximizes value within your budget, but outcomes vary
        Consider the stakes: if an incomplete answer has serious consequences, bet more. When the cost of being wrong is low, bet small and iterate.

        **keywords**: Search terms — code words and synonyms.
        - "login authentication" — synonyms widen the net
        - "cache invalidation TTL" — related terms that co-occur
        - Avoid generic words: "layer", "flow", "strategy", "handling" match everywhere
        - Optional for high-breadth survey mode

        **uriGlob**: Optional. Omit to search everywhere (the default and usually the best choice).
        Only narrow when you already know where to look or need to exclude noise.
        - file:///src/**/*.cs — C# files only
        - file:///src/**;!**/tests/** — source without tests
        - help://** — documentation only
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

        Example: Find auth implementations, not tests:
        → breadth=5, keywords="authenticate authorize", penalize="(?i)test|mock"

        Example: Find service contracts:
        → breadth=5, keywords="service", boost="(?i)interface|abstract"
        </LAYERED_APPROACH>

        <QUICK_PATTERNS>
        Ranked survey:
        → breadth=8, keywords="Controller", tokenBudget=3000

        Find where something is:
        → breadth=5, keywords="cache", tokenBudget=1500

        Examine specific code:
        → breadth=2, keywords="cache invalidation", tokenBudget=2500

        Find production code only:
        → breadth=5, keywords="database connection", penalize="(?i)test|mock", tokenBudget=1500

        Find contracts/interfaces:
        → breadth=5, keywords="service", boost="(?i)interface|abstract", tokenBudget=1500

        Narrow to a specific area (when needed):
        → breadth=8, uriGlob="help://**", tokenBudget=1000
        </QUICK_PATTERNS>

        <TIPS>
        Results too noisy? Add penalize="(?i)test|mock" or narrow with uriGlob.
        Too sparse? Increase tokenBudget or broaden keywords with synonyms.
        </TIPS>
        """;

    [McpServerTool(Name = "explore", Title = "Explore Repository", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false), Description(ToolInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<CallToolResult> ExploreAsync(
        [Description("Tokens to invest in the response")] int tokenBudget,
        [Description("How to distribute tokens: 1=depth, 10=breadth (default 5)")] int breadth = 5,
        [Description("URI glob pattern to filter results (e.g., file:///src/**, help://**). Combine with ; exclude with !")] string? uriGlob = null,
        [Description("Search terms — code words and synonyms (e.g., \"login authentication\", \"cache invalidation TTL\")")] string? keywords = null,
        [Description("Regex patterns to boost matches, comma-separated (e.g., \"Validate.*Token,(?i)auth\")")] string? boost = null,
        [Description("Regex patterns to de-rank matches, comma-separated (e.g., \"(?i)test|mock,\\.generated\\.\")")] string? penalize = null,
        [Description("Cap results shown - used with token budget to decide how things are displayed. Leave blank to have explore optimize it.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (tokenBudget <= 0)
            return ToolResult.Error("Error: tokenBudget must be a positive integer.");
        if (breadth < 1 || breadth > 10)
            return ToolResult.Error("Error: breadth must be between 1 and 10.");

        // Check orientation
        var orientationFooter = _sessionOrientation.CheckOrientation(uriGlob);

        // Create request signature for "call again to wait" pattern
        var requestSignature = $"{tokenBudget}|{breadth}|{uriGlob}|{keywords}|{boost}|{penalize}|{limit}";
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
                return ToolResult.Success(RepoQlClientScopeExtensions.FormatScopeNotReadyMessage(scopeStatus, uriGlob));
            }

            if (!scopeStatus.IsReady && isRepeatRequest)
            {
                // Repeat request - wait until scope is ready
                await client.WaitForScopeAsync(uriGlob, cancellationToken).ConfigureAwait(false);
            }
        }

        // Clear the pending request now that we're executing
        _lastRequestSignature = null;

        // Execute via server
        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.ExploreAsync(
                tokenBudget,
                breadth,
                uriGlob,  // Maps to internal 'scope' parameter
                keywords,
                boost,
                penalize,
                limit,
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
                return ToolResult.Error($"Error: Search failed. {ExtractErrorMessage(ex)}\n\n{diagnosticsException.Diagnostics}");
            }

            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancellationToken);
                return ToolResult.Error($"Error: Search failed. {ExtractErrorMessage(ex)}\n\n{diagnostics}");
            }
            return ToolResult.Error($"Error: Search failed. {ExtractErrorMessage(ex)}");
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
