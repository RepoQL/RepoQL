using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.Client.Diagnostics;
using RepoQL.McpServer.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;
using RepoQL.Client.Helpers;

namespace RepoQL.McpServer.Tools;

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
        The index has already parsed everything. Explore reveals what's relevant — ranked by meaning, not literal text. No blind spots, no verification tax. You see what exists before committing tokens to read anything.

        More than search: your first explore teaches you the vocabulary — the real class names, patterns, and terms-of-art you need for every subsequent call. The index is wild magic — composable, responsive to intent, and forgiving. A bad explore costs 1500 tokens. A good one saves you from 30 blind grep calls.
        </WHY>

        <BREADTH>
        Breadth controls how tokens are distributed across results.
        - Low (1-3): Depth. Few results with full structure and children. Use when you need content
        - Medium (4-6): Balanced. Good detail on top hits, awareness of the rest. Default.
        - High (7-10): Coverage. Many results with headlines. Use for surveying what exists.
        Omit breadth or pass 0 to let the system choose based on result distribution.
        </BREADTH>

        <PARAMETERS>
        **tokenBudget** (optional): Omit to let the system choose based on result quality. Provide an explicit number to spend exactly that many tokens.
        - Omitted / 0: the system picks a budget (~800-3500) based on how many results are strong and how rich their content is. Good default for most explores.
        - Explicit (e.g., 1500): spend exactly this. Use when you know the stakes — low for quick scans, high for deep investigation.
        - Don't spend more than 25000
        Consider the stakes: if an incomplete answer has serious consequences, bet more. When the cost of being wrong is low, omit and let the system decide.

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
        - github://owner/repo/** — imported repository (use query("SELECT * FROM Filesystems") to find URI schemes)
        - Combine with ; exclude with ! e.g. file://**;!**/tests/**

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
        3. question determines result RELEVANCE (natural language search)
        4. boost ranks UP (elevate matches)
        5. penalize ranks DOWN (demote matches)

        Example: Find auth implementations, not tests:
        → breadth=5, uriGlob="file:///src/**", keywords="authenticate authorize", question="How is authentication implemented?", penalize="(?i)test|mock"

        Example: Find service contracts:
        → breadth=5, uriGlob="file:///src/**", keywords="service", question="What service contracts exist?", boost="(?i)interface|abstract"
        </LAYERED_APPROACH>

        <QUICK_PATTERNS>
        Ranked survey:
        → breadth=10, uriGlob="file:///src/**", keywords="Controller", question="What controllers exist and what do they handle?", tokenBudget=3000

        Find where something is (wide search — no scope):
        → breadth=5, keywords="cache", question="Where is caching implemented?", boost="(?i)invalidation|redis|evict", tokenBudget=1500

        Examine specific code:
        → breadth=1, uriGlob="file:///src/**", keywords="cache invalidation", question="How does cache invalidation work?", tokenBudget=2500

        Find production code only:
        → breadth=5, uriGlob="file:///src/**;!**/tests/**", keywords="database connection", question="How are database connections managed?", penalize="(?i)test|mock", tokenBudget=1500

        Find contracts/interfaces:
        → breadth=5, uriGlob="file:///src/**/*.cs", keywords="service", question="What service interfaces are defined?", boost="(?i)interface|abstract", tokenBudget=1500

        Search documentation:
        → breadth=8, uriGlob="help://**", keywords="configuration", question="How is configuration handled?", tokenBudget=1000

        Explore an imported repository:
        → breadth=5, uriGlob="github://owner/repo/**", keywords="middleware routing", question="How does the middleware pipeline work?", tokenBudget=2000
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
        [Description("Token budget. Omit to let the system decide based on result quality (~1000-3500). Provide an explicit number to spend exactly that many tokens.")] int tokenBudget = 0,
        [Description("How to distribute tokens: 1=depth, 10=breadth. Omit or 0 to let the system choose based on result distribution.")] int breadth = 0,
        [Description("URI glob pattern to filter results (e.g., file:///src/**, help://**). Combine with ; exclude with !")] string? uriGlob = null,
        [Description("Search terms — code words and synonyms (e.g., \"login authentication\", \"cache invalidation TTL\")")] string? keywords = null,
        [Description("Regex patterns to boost matches, comma-separated (e.g., \"Validate.*Token,(?i)auth\")")] string? boost = null,
        [Description("Regex patterns to de-rank matches, comma-separated (e.g., \"(?i)test|mock,\\.generated\\.\")")] string? penalize = null,
        [Description("Cap results shown - used with token budget to decide how things are displayed. Leave blank to have explore optimize it.")] int? limit = null,
        [Description("Natural language question for reranking (e.g., \"Which files implement format loaders?\"). Keywords drive retrieval; question drives reranking precision.")] string? question = null,
        CancellationToken cancellationToken = default)
    {
        if (tokenBudget < 0)
            return ToolResult.Error("Error: tokenBudget must be zero (auto) or a positive integer.");
        if (breadth < 0 || breadth > 10)
            return ToolResult.Error("Error: breadth must be 0 (auto) or between 1 and 10.");

        var orientationFooter = _sessionOrientation.CheckOrientation(uriGlob);
        var requestSignature = $"{tokenBudget}|{breadth}|{uriGlob}|{keywords}|{boost}|{penalize}|{limit}|{question}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Scope readiness: first call sends NONE, repeat sends WAIT
        var readiness = isRepeatRequest ? ScopeReadinessMode.Wait : ScopeReadinessMode.None;

        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.ExploreAsync(
                tokenBudget,
                breadth,
                uriGlob,
                keywords,
                boost,
                penalize,
                limit,
                question,
                readiness,
                cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                // Not-ready error from NONE — store signature so repeat sends WAIT
                _lastRequestSignature = requestSignature;
                return ToolResult.Error($"Error: {response.Error}");
            }

            _lastRequestSignature = null;
            return ToolResult.Success(response.RenderedOutput + orientationFooter);
        }
        catch (Exception ex)
        {
            _lastRequestSignature = null;

            if (ex is RepoQlDiagnosticsException diagnosticsException)
                return ToolResult.Error($"Error: Search failed. {ExtractErrorMessage(ex)}\n\n{diagnosticsException.Diagnostics}");

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
