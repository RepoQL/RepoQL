using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Tools;

/// <summary>
/// MCP tool for reading repository content with progressive disclosure and token budget awareness.
///
/// Purpose: Provides agents with a dedicated read interface that automatically selects the most
/// appropriate representation level (full content, structure, or headline) based on available
/// token budget. Supports both direct content fetch and LLM-powered synthesis via explore Understand.
///
/// Complexity: Delegates all work to the server via gRPC. The client simply forwards the request
/// and returns the pre-rendered response.
/// </summary>
[McpServerToolType]
internal sealed class ReadTool(
    RepoQlClientProvider clientProvider,
    SelfTestRunner selfTestRunner,
    SessionOrientation sessionOrientation)
{
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    private readonly SelfTestRunner _selfTestRunner = selfTestRunner ?? throw new ArgumentNullException(nameof(selfTestRunner));
    private readonly SessionOrientation _sessionOrientation = sessionOrientation ?? throw new ArgumentNullException(nameof(sessionOrientation));

    // Track last request to implement "call again to wait" pattern (static to persist across tool invocations)
    private static string? _lastRequestSignature;

    private const string ReadInstructions = """
        <WHY>
        Explore finds URIs. Read fetches content. This is the second half of the workflow.

        The power: you don't read whole files. Explore gives you symbol URIs like `file:///src/Auth.cs#symbol=ValidateToken`. Read fetches just that function body. Three symbols across three files? One read call, just the bodies, no waste.
        </WHY>

        <CORE>
        `read(uri, budget)` returns content at the richest level that fits your budget.

        Progressive disclosure kicks in automatically:
        - Budget allows full content? You get full content with line numbers.
        - Too large? You get structure (signatures without bodies).
        - Still too large? You get headlines (one-line summaries).

        Globs distribute budget across matches. 100 files at 10k budget = ~100 tokens each = headlines. 1 file at 10k = full content. Narrow your target to get depth.
        </CORE>

        <MODIFIERS>
        Append ` => modifier` to get a specific view instead of content.

        **tree**: Directory structure with progressive detail.
        → `=> tree: folders` — just directories with file counts (cheapest)
        → `=> tree: files` — directories + filenames (default)
        → `=> tree: headlines` — directories + files + one-line summaries

        **headline**: One-line summary per file, flat list (no tree structure).

        **structure**: Signatures without bodies—see the shape without reading code.

        **content**: Full file with line numbers (explicit default).

        **history**: Git commits affecting the file.
        → `=> history` — all commits, newest first
        → `=> history: keyword` — ranks commits by relevance to keyword (doesn't filter)

        **blame**: Line-by-line git attribution showing who changed each line and when.

        **lint**: Diagnostics from the file.
        → `=> lint` — all diagnostics
        → `=> lint: errors` — errors only
        → `=> lint: warnings` — warnings only

        **find**: Semantic search within matched files.
        → `=> find: keywords` — ranks content by relevance, shows snippets
        → Has quality threshold—won't show junk matches

        **question**: LLM synthesis with citations.
        → `=> question: How does X work?` — reads content, synthesizes answer
        → Returns Answer, Evidence (with file:///path#line=N,M citations), Nuance
        → Always verify citations before trusting
        </MODIFIERS>

        <PATTERNS>
        URIs can target precisely or match broadly.

        **Fragments** pinpoint within files:
        → `#symbol=ValidateToken` — exact symbol (fully qualified name matched)
        → `#symbol=AuthService.*` — all direct members of a class
        → `#symbol=AuthService.**` — all descendants (nested types too)
        → `#line=42` — single line
        → `#line=42,100` — line range (inclusive, 1-based)

        **Globs** select many files:
        → `file:///src/**/*.cs` — recursive, all .cs files
        → `file:///src/*.cs` — non-recursive, one level only

        **Combining and excluding**:
        → `a;b;c` — match any of a, b, c
        → `!pattern` — exclude from all includes
        → `file:///src/**;!**/tests/**` — source without tests

        **Symbol wildcards across files** (powerful):
        → `file:///src/**/*Handler.cs#symbol=*Handler.CanHandle` — all CanHandle methods
        → `file:///src/**/*Service.cs#symbol=*Service.*` — all members of all services

        **Multiple specific symbols** (from explore results):
        → `file:///a.cs#symbol=Foo;file:///b.cs#symbol=Bar;file:///c.cs#symbol=Baz`
        → One call, just those three function bodies
        </PATTERNS>

        <BUDGET>
        Budget is how many tokens you're willing to spend. This is a bet—you don't know exactly what you'll get.

        - Start low and increase if you need more
        - Different targets and modifiers consume budget differently
        - Globs distribute across matches: 100 files at 5k = shallow; 1 file at 5k = deep

        Consider the stakes: if missing context has serious consequences, bet more. When the cost of being wrong is low, bet small and iterate.
        </BUDGET>

        <QUICK_PATTERNS>
        Orient in new codebase:
        → read("file:///** => tree: folders", 5000)

        See what's in a directory:
        → read("file:///src/Services/** => tree: headlines", 2000)

        Read a specific file:
        → read("file:///src/Auth.cs", 5000)

        Read just one function:
        → read("file:///src/Auth.cs#symbol=ValidateToken", 2000)

        Read all members of a class:
        → read("file:///src/Auth.cs#symbol=AuthService.*", 3000)

        Read same method across multiple files:
        → read("file:///src/**/*Handler.cs#symbol=*Handler.ExecuteAsync", 3000)

        Combine specific symbols from explore:
        → read("file:///a.cs#symbol=Foo;file:///b.cs#symbol=Bar", 2000)

        Who changed this file:
        → read("file:///src/Auth.cs => blame", 2000)

        What changed recently:
        → read("file:///src/Auth.cs => history", 1500)

        Ask a question about code:
        → read("file:///src/Auth/**/*.cs => question: How is token refresh implemented?", 2500)
        </QUICK_PATTERNS>

        <VS_EXPLORE>
        Use **explore** when you need to FIND something (what exists, where is X, how does Y work).
        Use **read** when you KNOW the URI and want the content.

        Workflow: explore Inventory → explore Locate → read specific URIs
        </VS_EXPLORE>
        """;

    [McpServerTool(Name = "read", Title = "Read Content", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false), Description(ReadInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<CallToolResult> ReadAsync(
        [Description("URI or glob pattern (e.g., file:///path, help:///file.md). Append ' => question: <question>' for LLM synthesis.")]
        string uri,
        [Description("Token budget - determines representation depth (full/structure/headline)")]
        int tokenBudget,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return ToolResult.Error("Error: URI cannot be empty.");

        if (tokenBudget <= 0)
            return ToolResult.Error("Error: tokenBudget must be a positive integer.");

        // Check orientation (reading help:// will mark as oriented)
        var orientationFooter = _sessionOrientation.CheckOrientation(uri);

        // Create request signature for "call again to wait" pattern
        var requestSignature = $"{uri}|{tokenBudget}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Check if URI requires semantic search (find or question modifiers)
        var requiresSemantic = uri.Contains("=> find:", StringComparison.OrdinalIgnoreCase) ||
                               uri.Contains("=> question:", StringComparison.OrdinalIgnoreCase);

        if (requiresSemantic)
        {
            // Extract base URI (before =>) for scope check
            var scopeUri = ExtractBaseUri(uri);

            var client = await _clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            var scopeStatus = await client.GetScopeReadinessAsync(scopeUri, cancel).ConfigureAwait(false);

            if (!scopeStatus.IsReady && !isRepeatRequest)
            {
                // First time seeing this request while scope not ready - return status and instructions
                _lastRequestSignature = requestSignature;
                return ToolResult.Success(RepoQlClientScopeExtensions.FormatScopeNotReadyMessage(scopeStatus, scopeUri));
            }

            if (!scopeStatus.IsReady && isRepeatRequest)
            {
                // Repeat request - wait until scope is ready
                await client.WaitForScopeAsync(scopeUri, cancel).ConfigureAwait(false);
            }
        }

        // Clear the pending request now that we're executing
        _lastRequestSignature = null;

        try
        {
            var client = await _clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            var response = await client.ReadAsync(uri, tokenBudget, cancel).ConfigureAwait(false);

            if (!response.Success)
            {
                return ToolResult.Error($"Error: {response.Error}");
            }

            return ToolResult.Success(response.RenderedOutput + orientationFooter);
        }
        catch (Exception ex)
        {
            var cleanMessage = ErrorClassifier.GetCleanMessage(ex);

            if (ex is RepoQlDiagnosticsException diagnosticsException)
            {
                return ToolResult.Error($"Error: {cleanMessage}\n\n{diagnosticsException.Diagnostics}");
            }

            // Infrastructure errors get diagnostics appended
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancel).ConfigureAwait(false);
                return ToolResult.Error($"Error: {cleanMessage}\n\n{diagnostics}");
            }
            return ToolResult.Error($"Error: {cleanMessage}");
        }
    }

    /// <summary>
    /// Extract the base URI before any modifier (=> ...).
    /// </summary>
    private static string? ExtractBaseUri(string uri)
    {
        var modifierIndex = uri.IndexOf("=>", StringComparison.Ordinal);
        if (modifierIndex <= 0)
            return uri.Trim();

        return uri[..modifierIndex].Trim();
    }
}
