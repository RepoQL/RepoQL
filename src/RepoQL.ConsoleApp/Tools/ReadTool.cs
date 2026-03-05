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

        The power: you don't read whole files. Explore gives you symbol URIs like `file:///src/Auth.cs#symbol=ValidateToken`. 
        Read fetches just that function body. Three symbols across three files? One read call, just the bodies, no waste.
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
        The URI pattern always controls scope — narrow it to get depth, widen it to get breadth.

        **tree**: Directory structure with progressive detail.
        → `=> tree: folders` — just directories with file counts (cheapest)
        → `=> tree: files` — directories + filenames (default)
        → `=> tree: headlines` — directories + files + one-line summaries

        **headline**: One-line summary per file, flat list (no tree structure).

        **structure**: Signatures without bodies—see the shape without reading code.
        → `file:///src/Auth/**/*.cs => structure` — shape of an entire subsystem in one call
        → Combines with symbol wildcards: `file:///src/**/*.cs#symbol=*Controller => structure` - signature of all controllers

        **content**: Full file with line numbers (explicit default).

        **history**: Git commits affecting matched files.
        → `=> history` — all commits, newest first
        → `=> history: auth refactor` — ranks commits by relevance to keywords (doesn't filter)
        → Globs show cross-file history: `file:///src/Auth/** => history: token validation`

        **blame**: Line-by-line git attribution. Fragments target precisely.
        → `file:///src/Auth.cs => blame` — full file attribution
        → `file:///src/Auth.cs#symbol=ValidateToken => blame` — just that function's history
        → `file:///src/Auth.cs#line=42,60 => blame` — specific line range

        **changes**: Working copy changes grouped by changelist (staged, unstaged, untracked).
        → Shows diffs for modified files, binary markers, and line counts

        **lint**: Diagnostics from matched files.
        → `=> lint` — all diagnostics
        → `=> lint: errors` — errors only
        → `=> lint: warnings` — warnings only
        → Globs aggregate: `file:///src/** => lint: errors` — all errors across the project

        **find**: Semantic search within matched files.
        → `=> find: keywords` — ranks content by relevance, shows snippets
        → Has quality threshold—won't show junk matches
        → The URI pattern controls where you search: `file:///src/tests/** => find: token validation`

        **similar**: Find what's semantically related to a seed — the URI pattern controls where you look.
        → `file:///src/**/*.cs => similar: file:///src/Auth.cs` — sibling implementations
        → `file:///src/tests/** => similar: file:///src/Auth.cs` — tests for this code
        → `file:///docs/** => similar: file:///src/Auth.cs#symbol=ValidateToken` — docs relevant to this method
        → `file:///**/*.sql => similar: file:///src/Auth.cs#line=50,80` — SQL related to these lines
        → The seed is *what*, the URI pattern is *where* — same seed, different scope, different answers

        **grep**: Case-insensitive literal text search within matched files.
        → `=> grep: validateToken` — every line containing the string, with context
        → Scope narrows the haystack: `file:///src/Auth/** => grep: connectionString`

        **regex**: Regular expression search within matched files.
        → `=> regex: validate\w+\(` — pattern match with full regex syntax
        → Scope narrows the haystack: `file:///src/**/*.cs => regex: class\s+\w+Handler`

        **question**: LLM synthesis with citations.
        → `=> question: How does X work?` — reads matched content, synthesizes answer
        → Returns Answer, Evidence (with file:///path#line=N,M citations), Nuance
        → Focused scopes get direct LLM answers. Wide scopes automatically defer to search+synthesis.
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

        Shape of a subsystem:
        → read("file:///src/Auth/**/*.cs => structure", 3000)

        Who wrote this function:
        → read("file:///src/Auth.cs#symbol=ValidateToken => blame", 1500)

        Commits relevant to a topic:
        → read("file:///src/Auth/** => history: token refresh", 2000)

        What's pending in working copy:
        → read("file:///src/Auth/** => changes", 2000)

        Find similar code, tests, or docs (change the scope, not the seed):
        → read("file:///src/**/*.cs => similar: file:///src/Auth/TokenService.cs", 2000)
        → read("file:///src/tests/** => similar: file:///src/Auth/TokenService.cs", 2000)
        → read("file:///docs/** => similar: file:///src/Auth/TokenService.cs#symbol=ValidateToken", 2000)

        Semantic search within a scope:
        → read("file:///src/tests/** => find: token validation", 2000)

        Find exact text in files:
        → read("file:///src/Auth/** => grep: connectionString", 2000)

        Find patterns in files:
        → read("file:///src/**/*.cs => regex: class\s+\w+Handler", 2000)

        Ask a focused question about specific code:
        → read("file:///src/Auth/TokenService.cs => question: How is token refresh implemented?", 2500)
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
        [Description("URI or glob pattern (e.g., file:///path, help:///file.md). Append ' => modifier: param' for views.")]
        string uriGlob,
        [Description("Token budget - determines representation depth (full/structure/headline)")]
        int tokenBudget,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(uriGlob))
            return ToolResult.Error("Error: URI cannot be empty.");

        if (tokenBudget <= 0)
            return ToolResult.Error("Error: tokenBudget must be a positive integer.");

        // Check orientation (reading help:// will mark as oriented)
        var orientationFooter = _sessionOrientation.CheckOrientation(uriGlob);

        // Create request signature for "call again to wait" pattern
        var requestSignature = $"{uriGlob}|{tokenBudget}";
        var isRepeatRequest = _lastRequestSignature == requestSignature;

        // Check if URI requires semantic search (find or question modifiers)
        var requiresSemantic = uriGlob.Contains("=> find:", StringComparison.OrdinalIgnoreCase) ||
                               uriGlob.Contains("=> question:", StringComparison.OrdinalIgnoreCase);

        if (requiresSemantic)
        {
            // Extract base URI (before =>) for scope check
            var scopeUri = ExtractBaseUri(uriGlob);

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
            var response = await client.ReadAsync(uriGlob, tokenBudget, cancel).ConfigureAwait(false);

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
