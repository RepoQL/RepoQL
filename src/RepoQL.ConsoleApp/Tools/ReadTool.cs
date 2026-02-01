using System.ComponentModel;
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
    SelfTestRunner selfTestRunner)
{
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    private readonly SelfTestRunner _selfTestRunner = selfTestRunner ?? throw new ArgumentNullException(nameof(selfTestRunner));

    private const string ReadInstructions = """
        Fetch repository content by URI with token-budget-aware representation selection.

        ### Capsule: ReadBasic
        **Invariant**
        `read(uri, budget)` returns content at the richest level that fits the budget.
        **Example**
        read("file:///src/Auth.cs", 5000)              -> full content if <=5000 tokens
        read("file:///src/Auth.cs", 500)               -> headline + structure if full too large
        read("file:///src/Auth.cs", 50)                -> headline only
        **Depth**
        - Progressive disclosure: full -> structure -> headline
        - Globs distribute budget across matches: read("file:///src/**/*.cs", 10000)
        - Fragments work: #line=10,50, #symbol=Foo.Bar
        ---

        ### Capsule: ReadWithQuestion
        **Invariant**
        Append ` // <question>` for LLM-synthesized answer with citations.
        **Example**
        read("file:///src/Auth.cs // How does JWT validation work?", 2000)
        read("file:///src/**/*.cs // What patterns are used for error handling?", 3000)
        **Depth**
        - Internally uses explore Understand pipeline (search + LLM synthesis)
        - Budget controls LLM response size
        - Citations as file:///path#line=N,M - always verify before trusting
        - Broad questions dilute; focused questions concentrate relevance
        ---

        ### Capsule: WhenToUse
        **Invariant**
        Use read when you KNOW the URI; use explore when you need to FIND it.
        **Example**
        + read("file:///src/Auth.cs", 2000)           -> you know the file
        + read("help:///quickstart.md // How?", 1500) -> known doc, specific question
        - read("file:///src/**/*.cs", 50000)          -> too broad, use explore Examine
        **Depth**
        - explore: discover what exists, find by concept, understand architecture
        - read: retrieve known content, answer questions about specific files
        - Workflow: explore Explore -> explore Find -> read specific files
        ---

        <EXAMPLES>
        Single file, full content:
          read("file:///src/Auth.cs", 5000)

        Line range:
          read("file:///src/Auth.cs#line=42,100", 2000)

        Symbol:
          read("file:///src/Auth.cs#symbol=ValidateToken", 1500)

        Symbol pattern (all descendants):
          read("file:///src/Auth.cs#symbol=AuthService.**", 3000)

        Glob pattern:
          read("file:///src/Services/**/*.cs", 8000)

        Compound pattern (multiple includes):
          read("file:///src/**/*.cs;file:///lib/**/*.cs", 10000)

        Compound with exclusions:
          read("file:///src/**/*.cs;!file:///src/tests/**", 8000)

        With question (LLM synthesis):
          read("file:///docs/API.md // What authentication methods are supported?", 2000)

        Multiple files with question:
          read("file:///src/Auth/**/*.cs // How is the refresh token rotated?", 3000)

        Tree overview (default shows files):
          read("file:///src/** => tree", 2000)

        Tree with folders only:
          read("file:///src/** => tree: folders", 500)

        Tree with headlines:
          read("file:///src/** => tree: headlines", 5000)

        History with keyword filter:
          read("file:///src/Auth.cs => history: token", 1500)
        </EXAMPLES>

        ### Capsule: Modifiers
        **Invariant**
        Append ` => modifier` to request a specific view of the content.
        **Example**
        read("file:///src/** => tree", 2000)           // folder structure with files
        read("file:///src/** => tree: folders", 500)   // folders only with file counts
        read("file:///src/** => tree: headlines", 3000) // folders + files + summaries
        read("file:///src/Auth.cs => history", 1500)   // what changed
        //BOUNDARY: Default is content; modifiers override progressive disclosure.
        **Depth**
        - tree: folder structure (detail: `folders`, `files` (default), `headlines`)
        - headline: one-line summary per file
        - structure: signatures without bodies
        - content: full file content (explicit default behavior)
        - history: commits affecting file; `: keyword` filters by message/author
        - blame: git blame for file showing who changed each line
        - lint: diagnostics; `: errors` or `: warnings` filters severity
        - find: semantic search within matched files; `: keywords` to search
        - SeeAlso: `ReadBasic` for default behavior
        ---

        ### Capsule: BudgetAsInvestment
        **Invariant**
        Budget is how much context you spend to get the answer; invest wisely.
        **Example**
        Low confidence what you need? Start small: read("file:///src/**", 500)
        Know exactly what you need? Invest more: read("file:///src/Auth.cs", 5000)
        **Depth**
        - 500: inventory scan; see what exists before committing
        - 1500: understand shape; enough for navigation decisions
        - 3000: read implementation; enough for most single-file tasks
        - 5000+: deep dive; multiple files or complex analysis
        - NotThis: large budget on broad glob wastes tokens on low-relevance files
        ---
        """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "read"), Description(ReadInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> ReadAsync(
        [Description("URI or glob pattern (e.g., file:///path, help:///file.md). Append ' // <question>' for LLM synthesis.")]
        string uri,
        [Description("Token budget - determines representation depth (full/structure/headline)")]
        int tokenBudget,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return "Error: URI cannot be empty.";

        if (tokenBudget <= 0)
            return "Error: tokenBudget must be a positive integer.";

        try
        {
            var client = await _clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            var response = await client.ReadAsync(uri, tokenBudget, cancel).ConfigureAwait(false);

            if (!response.Success)
            {
                return $"Error: {response.Error}";
            }

            return response.RenderedOutput;
        }
        catch (Exception ex)
        {
            var cleanMessage = ErrorClassifier.GetCleanMessage(ex);

            if (ex is RepoQlDiagnosticsException diagnosticsException)
            {
                return $"Error: {cleanMessage}\n\n{diagnosticsException.Diagnostics}";
            }

            // Infrastructure errors get diagnostics appended
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancel).ConfigureAwait(false);
                return $"Error: {cleanMessage}\n\n{diagnostics}";
            }
            return $"Error: {cleanMessage}";
        }
    }
}
