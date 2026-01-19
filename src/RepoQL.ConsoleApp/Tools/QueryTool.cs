using System.ComponentModel;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Xray;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class QueryTool(QueryExecutor queryExecutor, SelfTestRunner selfTestRunner)
{
    /// <summary>
    /// Track the last query that exceeded token budget for "repeat to confirm" pattern.
    /// </summary>
    private static string? _lastBudgetExceededQuery;

    private const string QueryInstructions = """
        <CONCEPT>
        DuckDB SQL for computation on the indexed repository.
        Use query when you need to COMPUTE (aggregate, filter, join, extract) - not just DISCOVER.
        </CONCEPT>
        
        <DECISION>
        | Need | Tool |
        |------|------|
        | "What exists? Where is X?" | xray |
        | "Show me this file/symbol" | read |
        | "How many? Which ones? What pattern?" | **query** |
        
        Query when: aggregating, complex filtering, joining results, regex extraction, graph traversal.
        </DECISION>
        
        <VIEWS>
        Primary views - cover 90% of queries. Start here:
        
        **Files** - documents with diagnostics
        `uri, lang, lines, error_count, warning_count, headline, summary, structure`
        ```sql
        SELECT uri, error_count FROM Files WHERE lang = 'code.csharp' AND error_count > 0;
        SELECT lang, COUNT(*), SUM(lines) FROM Files GROUP BY lang;
        ```
        
        **Functions** - methods/constructors across languages
        `name, qualified_name, declaring_type, signature, return_type, is_async, start_line`
        ```sql
        SELECT name, signature FROM Functions WHERE declaring_type = 'UserService';
        SELECT file_uri, name FROM Functions WHERE is_async AND return_type LIKE '%Task%';
        ```
        
        **Types** - classes/interfaces/structs
        `name, qualified_name, type_kind, namespace, extends, implements, start_line`
        ```sql
        SELECT name, file_uri FROM Types WHERE extends = 'BaseService';
        SELECT name FROM Types WHERE type_kind = 'interface';
        ```
        
        **Annotations** - errors/warnings/lint
        `resolved_target_uri, severity, rule_id, message`
        ```sql
        SELECT resolved_target_uri, message FROM Annotations WHERE severity = 'error';
        SELECT rule_id, COUNT(*) FROM Annotations GROUP BY rule_id ORDER BY 2 DESC;
        ```
        </VIEWS>
        
        <FUNCTIONS>
        **search(q, k)** - semantic + lexical document search
        ```sql
        SELECT uri, score FROM search('authentication', k := 10);
        ```

        **search_symbol(q, scope, kind_filter, k)** - find functions, classes, methods by name
        ```sql
        SELECT symbol, uri FROM search_symbol('ValidateToken');
        SELECT symbol FROM search_symbol('Service', kind_filter := 'type', scope := 'src/**/*.cs');
        ```
        
        **snippet(uri, context)** - code preview around location
        ```sql
        SELECT line_number, text FROM snippet('file:///src/api.cs#line=42', 3);
        ```
        
        **glob_files(pattern)** - path pattern matching
        ```sql
        SELECT uri FROM glob_files('src/**/*.cs;!src/**/tests/**');
        ```
        
        **tree(uris_json, foldersOnly)** - format URIs as ASCII directory tree
        ```sql
        SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**')));
        SELECT tree((SELECT json_group_array(uri) FROM glob_files('src/**')), foldersOnly := true);
        ```
        
        **Composition with LATERAL** - expand each row
        ```sql
        SELECT s.uri, sn.text 
        FROM search('config', k := 5) s, LATERAL snippet(s.uri, 2) sn 
        WHERE sn.is_focus;
        ```
        </FUNCTIONS>

        <MCP>
        **External MCP tools** - call other MCP servers, results as SQL rows
        ```sql
        SELECT * FROM mcp_tools();                    -- list available tools
        SELECT * FROM mcp_tool_params();              -- list parameters with docs
        SELECT * FROM context7_resolve_library_id(libraryname := 'react', query := 'hooks');
        ```

        **parse(text)** - convert CSV/TSV/YAML/JSON/JSONL to rows
        ```sql
        SELECT * FROM parse('id,name\n1,Alice\n2,Bob\n3,Charlie');
        ```

        See `repoql-docs:///repoql/tools/query/functions/mcp.md` for full MCP documentation.
        </MCP>

        <MORE>
        **Format-specific views** - prefixed by format (e.g., `markdown_headings`, `csharp_types`)
        See `repoql-docs:///repoql/tools/query/formats/*` for available views per format.
        
        **Complex format functions** - e.g., `xlsx()`, `xlsx_sheets()` for Excel
        ```sql
        SELECT * FROM xlsx('file:///data/report.xlsx');
        ```
        
        **ask()** - LLM-powered question answering on query results
        ```sql
        SELECT ask((SELECT json_group_array(json_object('uri', uri)) FROM search('auth', k := 5)), 'How is auth implemented?');
        ```
        
        **related()** - find similar documents
        ```sql
        SELECT uri, score FROM related('file:///src/Auth.cs', k := 10);
        ```

        **Git history** - `git_status()`, `git_diff()`, `git_blame()`, `git_hotspots`, `changes_related_to()`. See `repoql-docs:///repoql/tools/query/functions/git.md`.
        </MORE>
        
        <LEARNING>
        Documentation is queryable at `repoql-docs:///`. Discover more:

        ```sql
        SELECT uri, headline FROM Files WHERE uri LIKE 'repoql-docs://%';
        SELECT uri FROM Files WHERE uri LIKE 'repoql-docs:///repoql/tools/query/%';
        ```

        Or: `xray(intent='Find', scope='repoql-docs:///**', keywords='xlsx excel functions')`

        Key docs:
        - `repoql-docs:///quickstart.md` - SQL patterns, capsules
        - `repoql-docs:///repoql/tools/query/views/*` - view details
        - `repoql-docs:///repoql/tools/query/functions/*` - function signatures
        - `repoql-docs:///repoql/tools/query/formats/*` - format-specific features
        </LEARNING>
        
        <ADVANCED>
        **Graph traversal** - "what calls/uses this?"
        ```sql
        SELECT src.uri FROM edge e JOIN node src ON e.source_node_id = src.id
        WHERE e.type = 'CALLS' AND e.destination_node_id = (SELECT id FROM node WHERE uri = @target);
        ```
        
        **Regex extraction** - find patterns across codebase
        ```sql
        SELECT uri, regexp_extract_all(text_content, 'TODO:\s*(.+)', 1) AS todos
        FROM Files f JOIN artifact a ON f.artifact_id = a.id WHERE text_content LIKE '%TODO:%';
        ```
        
        **Base tables** (prefer views): artifact, node, edge, span, annotation
        </ADVANCED>
        
        <BUDGET>
        Large results are auto-summarized when they exceed your token budget.
        Repeat the exact query to bypass summarization and get full results.
        </BUDGET>
        
        <REMEMBER>
        - Views first: Files, Functions, Types, Annotations
        - search() finds, snippet() shows context, LATERAL composes them
        - Format-specific views/functions documented at repoql-docs:///repoql/tools/query/formats/*
        - Large results auto-summarize; repeat query for full output
        - Docs at `repoql-docs:///` - query or xray them to learn more
        </REMEMBER>
        """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "query"), Description(QueryInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> Query(
        [Description("DuckDB-style SQL to execute. Pass ':diagnostics:' to run diagnostics.")] string sql,
        [Description("Token budget for response. If exceeded and SQL contains a comment (intent), server may LLM-summarize. Client checks result and offers repeat-to-confirm if still too large.")] int tokenBudget = 15_000,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL query cannot be empty.", nameof(sql));

        // Special command: run diagnostics
        if (sql.Trim().Equals(":diagnostics:", StringComparison.OrdinalIgnoreCase))
        {
            return await selfTestRunner.RunAsync(cancel);
        }

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
                : "No results. Try a different query, or explore the docs with: xray intent=Explore scope=\"repoql-docs:///**\"";

            // Check token budget (even after server summarization - summary might still exceed)
            if (tokenBudget > 0 && !isRepeatRequest)
            {
                var estimatedTokens = TokenEstimator.EstimateTokens(output);
                if (estimatedTokens > tokenBudget)
                {
                    // Store this query so next identical call bypasses the check
                    _lastBudgetExceededQuery = requestSignature;

                    return FormatBudgetExceededMessage(
                        result.Summarized ? result.OriginalRowCount : result.TotalRowCount,
                        estimatedTokens,
                        tokenBudget,
                        result.Summarized);
                }
            }

            // Add indicator if server summarized the response
            if (result.Summarized && result.OriginalRowCount > 0)
            {
                output = $"(Summarized from {result.OriginalRowCount:N0} rows)\n\n{output}";
            }

            // Append status footer with timing and token count
            var status = new IndexerStatus(
                result.IndexPending,
                result.SemanticReady,
                result.SemanticEnabled,
                result.ExecutionTimeMs);
            var tokens = TokenEstimator.EstimateTokens(output);
            var footer = RepresentationFormatter.FormatStatusFooter(status, tokens);
            return $"{output}\n{footer}";
        }
        catch (Exception ex)
        {
            var cleanMessage = ErrorClassifier.GetCleanMessage(ex);
            await Console.Error.WriteLineAsync(cleanMessage);

            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await selfTestRunner.RunAsync(cancel);
                return $"Error: {cleanMessage}\n\n{diagnostics}";
            }

            // For user input errors (SQL syntax, etc.), just return the message
            return cleanMessage;
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
