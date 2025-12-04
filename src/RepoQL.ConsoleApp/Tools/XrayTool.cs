using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RepoQL.Rendering;
using RepoQL.Rendering.Search;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class XrayTool(IXraySearchEngine searchEngine, IXrayRenderingEngine renderingEngine)
{
    private readonly IXraySearchEngine _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
    private readonly IXrayRenderingEngine _renderingEngine = renderingEngine ?? throw new ArgumentNullException(nameof(renderingEngine));

    private const string ToolInstructions = """
        The best tool for 95% of your reading and understanding needs.
        X-ray vision files in your repo (code/docs/config/everything). See structure, find things without reading files or knowing keywords.

        tokenBudget: investment level → more tokens = richer detail. You set the budget, xray maximizes value.
        intent: zoom level
          explore → what's here? (headlines)
          find → where is it? (broad context)
          read → show me (actual snippets & detailed structure)

        Filter with scope (glob), guide with question (semantic). Results ranked by confidence.

        Examples:
        - What docs exist? → tokenBudget=800, intent=explore, scope=docs://**
        - Explore with focus → tokenBudget=1200, intent=explore, scope=file:///src/**, question="How is error handling implemented?"
        - Find without knowing the words → tokenBudget=600, intent=find, question="Where does retry logic live?"
        - Cross-boundary search → tokenBudget=800, intent=find, question="How are database connections configured?", scope=file:///**/*.json;file:///**/*.yaml;docs://**
        - Deep dive with boost → tokenBudget=2000, intent=read, question="How is JWT validated?", patterns=Validate.*,Token
        - Understand a module → tokenBudget=2000, intent=read, scope=file:///src/Auth/**, question="How does authentication work?"
        - Read specific file → tokenBudget=1500, intent=read, scope=file:///README.md

        Your first use should be to understand what documentation is available to you:
        tokenBudget=1000, intent=explore, scope=docs://**
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

        // Build search parameters (limit is not passed - it only affects display, not search)
        var searchParams = new SearchParameters(
            Scope: scope,
            Question: question,
            Patterns: patternStrings,
            Intent: intent
        );

        // Execute two-phase search
        SearchEngineResult searchResult;
        try
        {
            searchResult = await _searchEngine.SearchAsync(searchParams, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Error: Search failed. {ExtractErrorMessage(ex)}";
        }

        if (searchResult.Results.Count == 0)
        {
            return string.IsNullOrWhiteSpace(question)
                ? "No results found in the specified scope."
                : "No results found matching your query.";
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
            IndexerStatus: searchResult.IndexerStatus
        );

        return _renderingEngine.Render(xrayResults, context);
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
