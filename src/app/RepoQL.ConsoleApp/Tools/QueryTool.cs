using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class QueryTool(QueryExecutor queryExecutor, SelfTestRunner selfTestRunner, SessionOrientation sessionOrientation)
{
    /// <summary>
    /// Small overages (within 15%) pass through without requiring a repeat-to-confirm round-trip.
    /// </summary>
    private const double BudgetToleranceFactor = 1.15;

    /// <summary>
    /// Track the last query that exceeded token budget for "repeat to confirm" pattern.
    /// </summary>
    private static string? _lastBudgetExceededQuery;

    private const string QueryInstructions = """
        <CONCEPT>
        DuckDB SQL for computation on the indexed repository.
        Use query when you need to COMPUTE (aggregate, filter, join, extract) - not just DISCOVER.
        Use describe and summarize to understand unfamiliar schema
        
        RepoQL rewards creativity, use your intuition and experiment
        </CONCEPT>

        <DECISION>
        | Need | Tool |
        |------|------|
        | "What exists? Where is X?" | explore |
        | "Show me this file/symbol" | read |
        | "How many? Which ones? What pattern?" | **query** |
        </DECISION>

        <VIEWS>
        Start here — these cover 90% of queries:

        **Files** — `uri, lang, lines, error_count, warning_count, headline, summary, structure`
        ```sql
        SELECT lang, COUNT(*), SUM(lines) FROM Files GROUP BY lang;
        ```

        **Functions** — `name, qualified_name, declaring_type, signature, return_type, is_async`
        ```sql
        SELECT name, signature FROM Functions WHERE declaring_type = 'UserService';
        ```

        **Types** — `name, qualified_name, type_kind, namespace, extends, implements`
        ```sql
        SELECT name, file_uri FROM Types WHERE extends = 'BaseService';
        note supported languages usually have more tailored view prefixed with thier extension e.g. csharp_types, python_imports
        Use the explore tool on help://** to discover them
        ```

        **Annotations** — `resolved_target_uri, severity, rule_id, message`
        ```sql
        SELECT rule_id, COUNT(*) FROM Annotations GROUP BY rule_id ORDER BY 2 DESC;
        ```

        **annotations_for(uri, kinds, min_severity)** — diagnostics for one document
        ```sql
        SELECT rule_id, message FROM annotations_for('file:///src/api.cs', 'lint', 'warning');
        ```
        </VIEWS>

        <FUNCTIONS>
        **search(q, k, scope, boost_pattern, negative_pattern)** → uri, score — semantic + lexical
        ```sql
        SELECT uri, score FROM search('authentication', k := 10);
        SELECT uri, score FROM search('parser', scope := 'file:///src/%', boost_pattern := 'markdown|yaml', negative_pattern := '(?i)test');
        ```

        **search_symbol(q, scope, kind_filter, k)** → symbol, uri — find functions, classes, methods by name
        ```sql
        SELECT symbol, uri FROM search_symbol('ValidateToken');
        SELECT symbol FROM search_symbol('Service', kind_filter := 'type', scope := 'src/**/*.cs');
        ```

        **snippet(uri, context)** → line_number, text, is_focus — code preview
        ```sql
        SELECT line_number, text FROM snippet('file:///src/api.cs#line=42', 3);
        ```
        Fragments: `#line=42`, `#line=42,100`, `#symbol=ClassName.MethodName`, `#char=100,150`

        **glob_files(pattern)** → uri — `SELECT uri FROM glob_files('src/**/*.cs;!**/tests/**');`
        **related(uri, k)** → uri, score — find similar documents
        **ask(context_json, question, max_tokens)** → text — LLM synthesis on query results
        </FUNCTIONS>

        <COMPOSITION>
        Every operation returns a table. SQL joins and CTEs compose them.

        **LATERAL** — expand each row with a correlated function:
        ```sql
        SELECT s.uri, sn.text
        FROM search('config', k := 5) s, LATERAL snippet(s.uri, 2) sn
        WHERE sn.is_focus;
        ```

        **parse()** — inline CSV/JSON/YAML/anything as ad-hoc lookup tables:
        ```sql
        SELECT f.uri, o.team FROM Files f
        JOIN parse('pattern,team\n**/Auth/**,Security\n**/Core/**,Platform') o
        ON f.uri LIKE o.pattern;
        ```

        **Recursive CTEs** — graph traversal through composition tree:
        ```sql
        WITH RECURSIVE parts AS (
          SELECT destination_node_id as id, 1 as depth FROM edge
          WHERE source_node_id = (SELECT id FROM node WHERE uri = 'file:///src/Auth.cs')
          AND type = 'HAS_PART'
          UNION ALL
          SELECT e.destination_node_id, p.depth + 1 FROM edge e
          JOIN parts p ON e.source_node_id = p.id
          WHERE e.type = 'HAS_PART' AND p.depth < 5
        )
        SELECT n.kind, n.name, p.depth FROM parts p
        JOIN node n ON p.id = n.id ORDER BY p.depth;
        ```

        **Search + enrich** — join search results with metadata:
        ```sql
        SELECT s.uri, f.lang, f.lines FROM search('error', k := 20) s JOIN Files f ON s.uri = f.uri;
        ```
        </COMPOSITION>

        <MORE>
        Look up syntax at `help:///repoql/tools/query/` when needed:

        - **Git**: git_status(), git_diff(), git_blame(), git_hotspots, changes_related_to()
        - **MCP**: mcp_tools(), mcp_tool_params() — call external MCP servers from SQL, results as rows
        - **Data**: parse(text) for CSV/JSON/YAML; xlsx(), xlsx_sheets(), xlsx_union() for Excel
        - **Format views**: markdown_headings, csharp_types, etc. — `help:///repoql/tools/query/formats/*`
        - **Regex**: regexp_extract_all() for pattern extraction across codebase
        - **DuckDB patterns**: QUALIFY, PIVOT/UNPIVOT, list comprehensions, window functions
        - **Base tables** (prefer views): artifact, node, edge, span, annotation
        </MORE>

        <BUDGET>
        Large results auto-summarize when they exceed your token budget.
        Repeat the exact query to bypass summarization and get full results.
        </BUDGET>
        """;

    [McpServerTool(Name = "query", Title = "Query Repository", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false), Description(QueryInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<CallToolResult> Query(
        [Description("DuckDB-style SQL to execute.")] string sql,
        [Description("Token budget for response. If exceeded and SQL contains a comment (intent), server may LLM-summarize. Client checks result and offers repeat-to-confirm if still too large.")] int tokenBudget = 15_000,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return ToolResult.Error("SQL query cannot be empty.");

        // Redirect :: commands to the dedicated command tool
        var trimmedSql = sql.AsSpan().Trim();
        if (trimmedSql.StartsWith("::") || trimmedSql.Equals(":diagnostics:", StringComparison.OrdinalIgnoreCase))
        {
            var cmdName = trimmedSql.StartsWith("::")
                ? trimmedSql[2..].Trim().ToString().Split(' ', '[')[0]
                : "diagnostics";  // legacy :diagnostics: redirect
            return ToolResult.Error($"Commands have moved to the 'command' tool. Use command(command=\"{cmdName}\") instead.");
        }

        // Check orientation
        var orientationFooter = sessionOrientation.CheckOrientation(null);

        try
        {
            // Check if this is a repeat of a query that previously exceeded budget
            var requestSignature = $"{sql.Trim()}|{tokenBudget}";
            var isRepeatRequest = _lastBudgetExceededQuery == requestSignature;

            // Use int.MaxValue for maxRows - token budget controls output size
            var result = await queryExecutor.ExecuteAsync(sql, int.MaxValue, ResultFormat.Toon, tokenBudget, cancel).ConfigureAwait(false);

            // Clear the stored query - it's either being repeated (confirmed) or a new query
            _lastBudgetExceededQuery = null;
            var output = result.Lines.Length > 0
                ? string.Join(Environment.NewLine, result.Lines)
                : "No results. Try a different query, or explore the docs with:\n" +
                  "  explore(uriGlob=\"help://**\", keywords=\"topic\", tokenBudget=1500)\n" +
                  "  explain(question=\"your question\", uriGlob=\"help://**\", tokenBudget=2500)";

            // Check token budget (even after server summarization - summary might still exceed)
            if (tokenBudget > 0 && !isRepeatRequest)
            {
                var estimatedTokens = TokenEstimator.EstimateTokens(output);
                if (estimatedTokens > (int)(tokenBudget * BudgetToleranceFactor))
                {
                    // Store this query so next identical call bypasses the check
                    _lastBudgetExceededQuery = requestSignature;

                    return ToolResult.Success(FormatBudgetExceededMessage(
                        result.Summarized ? result.OriginalRowCount : result.TotalRowCount,
                        estimatedTokens,
                        tokenBudget,
                        result.Summarized));
                }
            }

            // Add indicator if server summarized the response
            if (result.Summarized && result.OriginalRowCount > 0)
            {
                output = $"(Summarized from {result.OriginalRowCount:N0} rows)\n\n{output}";
            }

            // Append status footer with timing and token count
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

            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancel);
                return ToolResult.Error($"Error: {cleanMessage}\n\n{diagnostics}");
            }

            // User-input errors (SQL syntax, invalid column, etc.) return Success so the
            // Claude harness doesn't cancel sibling parallel tool calls. The error text
            // is still clearly an error — the agent sees it and can correct the query.
            var enrichedMessage = ErrorClassifier.EnrichSqlError(cleanMessage);
            return ToolResult.Success(enrichedMessage + orientationFooter);
        }
    }

    /// <summary>
    /// Format the message returned when a query exceeds the token budget.
    /// </summary>
    private static string FormatBudgetExceededMessage(long rowCount, int estimatedTokens, int tokenBudget, bool wasSummarized = false)
    {
        var prefix = wasSummarized
            ? $"Response still exceeds budget after LLM summarization"
            : $"Response exceeds token budget";

        return $"""
            {prefix}: {estimatedTokens:N0} tokens (budget: {tokenBudget:N0}), {rowCount:N0} rows.

            **Repeat the query exactly** to receive the full result (budget check bypassed on repeat).
            """;
    }
}
