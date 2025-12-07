using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Rendering;
using RepoQL.Rendering.Search;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class XrayTool(
    IXraySearchEngine searchEngine,
    IXrayRenderingEngine renderingEngine,
    RepoQlClientProvider clientProvider)
{
    private readonly IXraySearchEngine _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
    private readonly IXrayRenderingEngine _renderingEngine = renderingEngine ?? throw new ArgumentNullException(nameof(renderingEngine));
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));

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
          read → show me (actual snippets & detailed structure) | Search criteria required
        
        Know the uri(s) of the thing you are a looking for? Use ReadMcpResourceTool - works for objects and files, supports globbing patterns.

        Filter with scope (glob), guide with question (semantic). Results ranked by confidence.
        </KNOBS>

        <EXAMPLES>
        - What docs exist? → tokenBudget=800, intent=explore, scope=docs://**
        - Explore with focus → tokenBudget=1200, intent=explore, scope=file:///src/**, question="How is error handling implemented?"
        - Find without knowing the words → tokenBudget=600, intent=find, question="Where does retry logic live?"
        - Cross-boundary search → tokenBudget=800, intent=find, question="How are database connections configured?", scope=file:///**/*.json;file:///**/*.yaml;docs://**
        - Deep dive with boost → tokenBudget=2000, intent=read, question="How is JWT validated?", patterns=Validate.*,Token
        - Understand a module → tokenBudget=2000, intent=read, scope=file:///src/Auth/**, question="How does authentication work?"
        - Read specific file → tokenBudget=1500, intent=read, scope=file:///README.md
        </EXAMPLES>

        <REMEMBER>
        Your first use should be to understand what documentation is available to you:
        tokenBudget=1000, intent=explore, scope=docs://**
        </REMEMBER>
        """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "xray"), Description(ToolInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> XrayAsync(
        [Description("Tokens to invest in the response")] int tokenBudget,
        [Description("Zoom level: explore, find, or read")] Intent intent,
        [Description("Where to look (glob pattern), full uri, semicolon delimited list of uris")] string? scope = null,
        [Description("What to find (semantic search)")] string? question = null,
        [Description("Boost matches (.net flavoured regex, comma-separated)")] string? patterns = null,
        [Description("Cap results shown - used with token budget to decide how things are displayed. Leave blank to have xray optimize it.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        // Parse and validate parameters
        var patternStrings = ParsePatternStrings(patterns);

        if (tokenBudget <= 0)
            return "Error: tokenBudget must be a positive integer.";

        // Create request signature for "call again to wait" pattern
        var requestSignature = $"{tokenBudget}|{intent}|{scope}|{question}|{patterns}|{limit}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Check if indexer is ready before executing
        var preStatus = await GetIndexerStatusAsync(0, cancellationToken).ConfigureAwait(false);
        var needsIndex = preStatus.IndexPending > 0;
        var needsSemantic = !string.IsNullOrWhiteSpace(question) && !preStatus.SemanticReady;

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
            Question: question,
            Patterns: patternStrings,
            Intent: intent
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
            return $"Error: Search failed. {ExtractErrorMessage(ex)}";
        }
        sw.Stop();

        // Get indexer status with timing
        var indexerStatus = await GetIndexerStatusAsync(sw.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);

        if (searchResult.Results.Count == 0)
        {
            var noResults = string.IsNullOrWhiteSpace(question)
                ? "No results found in the specified scope."
                : "No results found matching your query.";
            return $"{noResults}\n\n{RepresentationFormatter.FormatStatusFooter(indexerStatus)}";
        }

        // Convert to rendering types
        var xrayResults = searchResult.Results.Select(r => new XrayResult(
            Uri: r.Uri,
            Confidence: r.Confidence,
            Kind: r.Scope == SearchScope.Symbol ? r.Kind : null,
            Headline: r.Headline,
            Structure: r.Structure,
            Snippet: r.Snippet,
            Lang: r.Lang
        )).ToList();

        var hasSearchCriteria = !string.IsNullOrWhiteSpace(question) || patternStrings.Count > 0;
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
            var result = await client.ExecuteRawQueryAsync("SELECT indexing_diagnostics() as json", cancellationToken: ct).ConfigureAwait(false);

            if (result.Rows.Count > 0)
            {
                var json = result.Rows[0].Values.FirstOrDefault()?.StringValue;
                if (!string.IsNullOrEmpty(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var hotPathDepth = root.TryGetProperty("hot_path_depth", out var hp) ? hp.GetInt32() : 0;
                    var idlePending = root.TryGetProperty("idle_pending", out var ip) ? ip.GetInt32() : 0;
                    var analysisDepth = root.TryGetProperty("analysis_depth", out var ad) ? ad.GetInt32() : 0;
                    var writerPending = root.TryGetProperty("writer_pending", out var wp) ? wp.GetInt32() : 0;
                    var embedEnabled = root.TryGetProperty("embed_enabled", out var ee) && ee.GetBoolean();

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
