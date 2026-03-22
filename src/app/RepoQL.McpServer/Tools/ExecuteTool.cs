using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.McpServer.Commands;
using RepoQL.Client.Diagnostics;
using RepoQL.McpServer.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;
using RepoQL.Explore;
using RepoQL.Client.Helpers;
using RepoQL.Client.Commands;

namespace RepoQL.McpServer.Tools;

/// <summary>
/// Purpose: Expose sandboxed JavaScript execution with the same budget and footer contract as query.
/// Complexity: Mirrors QueryTool while routing execution through the execute gRPC path.
/// </summary>
[McpServerToolType]
internal sealed class ExecuteTool(QueryExecutor queryExecutor, SelfTestRunner selfTestRunner, SessionOrientation sessionOrientation)
{
    /// <summary>
    /// Small overages (within 15%) pass through without requiring a repeat-to-confirm round-trip.
    /// </summary>
    private const double BudgetToleranceFactor = 1.15;

    /// <summary>
    /// Track the last execute request that exceeded token budget for "repeat to confirm" pattern.
    /// </summary>
    private static string? _lastBudgetExceededRequest;

    /// <summary>
    /// Last successful execution result JSON — available as `input` global in the next call.
    /// </summary>
    private static string? _lastExecuteResult;

    private const string ExecuteInstructions = """
        Execute JavaScript in a WASM sandbox with full access to the repository graph, file system, diagram rendering, document conversion, and media processing.

        <CAPABILITIES>
        **Graph & Data:**
        - `repoql.query(sql)` — SQL against the indexed repository. Returns JS arrays/objects.
        - `repoql.read(uri, {budget})` — read file/symbol content by URI. Returns `{content, representation, tokensUsed}`.

        **File System (scope-enforced):**
        - `repoql.write(uri, content)` — write to `file:///.repoql/tmp/**` (default scope).
        - `repoql.delete(uri)` — delete within write scope.

        **Diagram Rendering (WASM — no install needed):**
        - `repoql.graphviz(dot, engine?, format?)` — render DOT notation to SVG. Engine: "dot" (default), "neato", etc.
        - `repoql.svgToPng(svg, scale?)` — rasterize SVG to base64 PNG. Scale: 2 = 2x resolution.

        **Document Conversion (WASM — no install needed):**
        - `repoql.pandoc({input, from, to, args?})` — convert between formats (markdown, html, latex, rst, org, plain, etc.). Returns `{output, from, to}`.

        **Media Processing (native FFmpeg):**
        - `repoql.ffmpeg({input, output, args?, probe?, activationBytes?})` — run ffmpeg/ffprobe. Input/output are file:// URIs, scope-enforced.

        **Modules:**
        - 20 built-in libraries: `import("yaml")`, `import("semver")`, `import("diff")`, `import("change-case")`, `import("ohash")`, etc.
        - CJS packages: access via `mod.default` (yaml, semver, json5, ini, fuse, mustache, picomatch, toposort, front-matter, parse-diff).
        - ESM packages: named exports directly (change-case, ohash, radash, diff, microdiff, ignore, base64, dayjs, toml, xml).
        - Agent modules: `import('repoql:@agent/name')` — see `::module.list`.

        **Chaining:**
        - `input` global contains the previous execute call's result (JSON). Chain calls to build on prior work.

        **Diagnostics:**
        - `console.log()`, `console.warn()`, `console.error()` — captured in output.
        </CAPABILITIES>

        <EXAMPLES>
        Query and filter:
        ```js
        const types = repoql.query("SELECT name, extends FROM Types WHERE extends IS NOT NULL");
        types.filter(t => t.extends === 'IDisposable').map(t => t.name)
        ```

        Read a file, convert with Pandoc:
        ```js
        var readme = repoql.read('file:///README.md', {budget: 500});
        var html = repoql.pandoc({input: readme.content, from: 'markdown', to: 'html'});
        html.output
        ```

        Generate a diagram from graph data:
        ```js
        var types = repoql.query("SELECT name FROM Types WHERE extends = 'IFormatLoader'");
        var dot = 'digraph { rankdir=BT; node [shape=box,style="filled,rounded"]; ';
        types.forEach(t => { dot += '"' + t.name + '" -> IFormatLoader; '; });
        dot += '}';
        var svg = repoql.graphviz(dot);
        var png = repoql.svgToPng(svg, 2);
        repoql.write('file:///.repoql/tmp/diagram.png.b64', png);
        ```

        Use a built-in module:
        ```js
        import("yaml").then(function(mod) {
          var content = repoql.read("file:///config.yml", {budget: 5000}).content;
          return mod.default.load(content);
        })
        ```

        Chain from previous result:
        ```js
        // input has the previous call's result
        input.filter(x => x.cnt > 100)
        ```
        </EXAMPLES>

        <BUDGET>
        Large results auto-summarize when they exceed your token budget.
        The intent parameter guides LLM summarization — be specific about what you need.
        Repeat the exact request to bypass summarization and get full results.
        </BUDGET>
        """;

    [McpServerTool(Name = "execute", Title = "Execute JavaScript", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false), Description(ExecuteInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<CallToolResult> RunAsync(
        [Description("What you're trying to accomplish — guides result summarization when output exceeds budget.")] string intent,
        [Description("JavaScript source code to execute.")] string code,
        [Description("Token budget for response.")] int tokenBudget = 15_000,
        [Description("Execution timeout in milliseconds.")] int timeout = 300_000,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ToolResult.Error("JavaScript code cannot be empty.");

        var orientationFooter = sessionOrientation.CheckOrientation(null);

        try
        {
            var requestSignature = $"execute:{code.Trim()}|{tokenBudget}";
            var isRepeatRequest = _lastBudgetExceededRequest == requestSignature;

            var result = await queryExecutor.ExecuteCodeAsync(
                code,
                intent,
                tokenBudget,
                timeout,
                ResultFormat.Toon,
                _lastExecuteResult,
                cancel).ConfigureAwait(false);

            _lastBudgetExceededRequest = null;

            // Store raw JS output for next call's `input` global
            if (!result.SandboxError)
                _lastExecuteResult = result.RawJsOutput;

            var output = result.Lines.Length > 0
                ? string.Join(Environment.NewLine, result.Lines)
                : "No output. The script returned null or undefined.";

            // JS execution errors (syntax, runtime, timeout) return isError=true
            // so the agent sees them as failures, unlike query where SQL errors are
            // returned as Success to avoid cancelling sibling parallel calls.
            if (result.SandboxError)
                return ToolResult.Error(output);

            if (tokenBudget > 0 && !isRepeatRequest)
            {
                var estimatedTokens = TokenEstimator.EstimateTokens(output);
                if (estimatedTokens > (int)(tokenBudget * BudgetToleranceFactor))
                {
                    _lastBudgetExceededRequest = requestSignature;
                    return ToolResult.Success(FormatBudgetExceededMessage(
                        result.Summarized ? result.OriginalRowCount : result.TotalRowCount,
                        estimatedTokens,
                        tokenBudget,
                        result.Summarized));
                }
            }

            if (result.Summarized && result.OriginalRowCount > 0)
            {
                output = $"(Summarized from {result.OriginalRowCount:N0} result items)\n\n{output}";
            }

            var status = new TrustSignal(
                result.IndexTotal,
                result.IndexPending,
                result.IndexFailed,
                result.IndexStale,
                result.SemanticEnabled,
                result.SemanticReady,
                result.SemanticPercent,
                result.ExecutionTimeMs);
            var tokens = TokenEstimator.EstimateTokens(output);
            var footer = RepresentationFormatter.FormatStatusFooter(status, tokens);
            return ToolResult.Success($"{output}\n{footer}" + orientationFooter);
        }
        catch (Exception ex)
        {
            var cleanMessage = ErrorClassifier.GetCleanMessage(ex);
            await Console.Error.WriteLineAsync(cleanMessage);

            if (ex is RepoQlDiagnosticsException diagnosticsException)
            {
                return ToolResult.Error($"Error: {cleanMessage}\n\n{diagnosticsException.Diagnostics}");
            }

            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancel);
                return ToolResult.Error($"Error: {cleanMessage}\n\n{diagnostics}");
            }

            return ToolResult.Success(cleanMessage + orientationFooter);
        }
    }

    private static string FormatBudgetExceededMessage(long itemCount, int estimatedTokens, int tokenBudget, bool wasSummarized)
    {
        var prefix = wasSummarized
            ? "Response still exceeds budget after LLM summarization"
            : "Response exceeds token budget";

        return $"""
            {prefix}: {estimatedTokens:N0} tokens (budget: {tokenBudget:N0}), {itemCount:N0} items.

            **Repeat the request exactly** to receive the full result (budget check bypassed on repeat).
            """;
    }
}
