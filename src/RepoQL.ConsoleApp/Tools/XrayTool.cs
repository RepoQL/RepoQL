using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Rendering;
using RepoQL.Rendering.Search;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class XrayTool(
    IXraySearchEngine searchEngine,
    IXrayRenderingEngine renderingEngine,
    RepoQlClientProvider clientProvider,
    SelfTestRunner selfTestRunner)
{
    private readonly IXraySearchEngine _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
    private readonly IXrayRenderingEngine _renderingEngine = renderingEngine ?? throw new ArgumentNullException(nameof(renderingEngine));
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    private readonly SelfTestRunner _selfTestRunner = selfTestRunner ?? throw new ArgumentNullException(nameof(selfTestRunner));

    // Track last request to implement "call again to wait" pattern (static to persist across tool invocations)
    private static string? _lastRequestSignature;

    private const string ToolInstructions = """
        <CONCEPT>
        The best tool for 95% of your reading and understanding needs.
        X-ray vision files in your repo (code/docs/config/everything). See structure, find things without reading files or knowing keywords.
        </CONCEPT>

        <KNOBS>
        tokenBudget:
            investment level → more tokens = richer detail. You set the budget, xray maximizes value.
            if you want to be sure you found everything, set a high budget
            important: budget is exactly how many tokens you want to spend on seeing the answer, it is not a maximum.
            the underlying query is the same regardless of budget - budget controls the level of detail in the response, and attempts to maximize value given the budget and intent

        intent: zoom level
          explore → what's here? (headlines) | Search criteria optional
          find → where is it? (broad context) | Search criteria required
          examine → I know what I want, show me the code and context (detailed snippets across relevant files) | Search criteria required

        keywords: search terms for hybrid (semantic + lexical) search
          - Questions + boost patterns work best: keywords="How does auth work?" boost="Auth.*,Validate.*"
          - Questions alone find conceptually related content via semantic search
          - Keywords alone work for precise symbol searches: "ValidateToken JWT"
          - Combine both approaches: question for context, boost for specific symbols

        Know the uri(s) of the thing you are a looking for? Use ReadMcpResourceTool - works for objects and files, supports globbing patterns.

        Filter with scope (glob), guide with keywords (semantic). Results ranked by confidence.
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
        - What docs exist? → tokenBudget=1000, intent=explore, scope=docs://**
        - Explore with focus → tokenBudget=1200, intent=explore, scope=file:///src/**, keywords="How does error handling work?"
        - Find feature → tokenBudget=800, intent=find, keywords="How does authentication validate JWT tokens?"
        - Cross-boundary search → tokenBudget=800, intent=find, keywords="Where is database connection configured?", scope=file:///**/*.json;file:///**/*.yaml;docs://**
        - Deep dive with boost → tokenBudget=2000, intent=examine, keywords="JWT validation flow", boost="Validate.*,Token.*"
        - Exclude tests → tokenBudget=1500, intent=find, keywords="authentication implementation", penalize="(?i)test|mock"
        - Understand a module → tokenBudget=2000, intent=examine, scope=file:///src/Auth/**, keywords="How does the authentication flow work?"
        - Examine specific file → tokenBudget=1500, intent=examine, scope=file:///**/README.md
        </EXAMPLES>

        <REMEMBER>
        First use: explore available documentation with tokenBudget=1000, intent=explore, scope=docs://**
        For finding code: combine question + boost pattern for best results
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
        // Parse and validate pattern parameters
        var boostPatterns = ParsePatternStrings(boost);
        var penalizePatterns = ParsePatternStrings(penalize);
        var patternStrings = boostPatterns;

        if (tokenBudget <= 0)
            return "Error: tokenBudget must be a positive integer.";

        // Create request signature for "call again to wait" pattern
        var requestSignature = $"{tokenBudget}|{intent}|{scope}|{keywords}|{boost}|{penalize}|{limit}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Check if indexer is ready before executing
        var preStatus = await GetIndexerStatusAsync(0, cancellationToken).ConfigureAwait(false);
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

        // Build search parameters (limit is not passed - it only affects display, not search)
        var searchParams = new SearchParameters(
            Scope: scope,
            Question: keywords,
            Patterns: patternStrings,
            Intent: intent,
            PenalizePatterns: penalizePatterns.Count > 0 ? penalizePatterns : null
        );

        // Execute two-phase search with timing
        var sw = Stopwatch.StartNew();
        SearchEngineResult searchResult;
        try
        {
            searchResult = await _searchEngine.SearchAsync(searchParams, cancellationToken).ConfigureAwait(false);
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
        sw.Stop();

        // Get indexer status with timing
        var indexerStatus = await GetIndexerStatusAsync(sw.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);

        if (searchResult.Results.Count == 0)
        {
            var noResults = string.IsNullOrWhiteSpace(keywords)
                ? $"No results found in scope: {scope ?? "(all)"}"
                : $"No results matching '{keywords}' in scope: {scope ?? "(all)"}";
            return $"{noResults}\n\n{RepresentationFormatter.FormatStatusFooter(indexerStatus)}";
        }

        // Convert to rendering types
        var xrayResults = searchResult.Results.Select(r => ConvertToXrayResult(r)).ToList();

        var hasSearchCriteria = !string.IsNullOrWhiteSpace(keywords) || patternStrings.Count > 0;
        var context = new RenderingContext(
            Intent: intent,
            TokenBudget: tokenBudget,
            Limit: limit,
            HasSearchCriteria: hasSearchCriteria,
            IndexerStatus: indexerStatus
        );

        return _renderingEngine.Render(xrayResults, context);
    }

    private async Task<IndexerStatus> GetIndexerStatusAsync(long elapsedMs, CancellationToken ct)
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
                    var embedEnabled = values.TryGetValue("embed_enabled", out var ee) && bool.Parse(ee);

                    return IndexerStatus.FromDiagnostics(hotPathDepth, idlePending, analysisDepth, writerPending, elapsedMs, embedEnabled);
                }
            }
        }
        catch
        {
            // Fall back to unknown status on any error
        }

        return new IndexerStatus(0, false, false, elapsedMs);
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

    #region Parameter Parsing

    private static List<string> ParsePatternStrings(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
            return [];

        var result = new List<string>();
        foreach (var pattern in patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Validate pattern is parseable before adding
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                result.Add(pattern);
            }
            catch (RegexParseException)
            {
                // Skip invalid patterns
            }
        }
        return result;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Convert a SearchResult to XrayResult, including child objects recursively.
    /// </summary>
    private static XrayResult ConvertToXrayResult(SearchResult result)
    {
        IReadOnlyList<XrayResult>? childObjects = null;
        if (result.ChildObjects is { Count: > 0 })
        {
            childObjects = result.ChildObjects.Select(ConvertToXrayResult).ToList();
        }

        return new XrayResult(
            Uri: result.Uri,
            Confidence: result.Confidence,
            Kind: result.Scope == SearchScope.Symbol ? result.Kind : null,
            Headline: result.Headline,
            Structure: result.Structure,
            Snippet: result.Snippet,
            Lang: result.Lang,
            SemanticType: result.SemanticType,
            ChildObjects: childObjects
        );
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

    #endregion
}
