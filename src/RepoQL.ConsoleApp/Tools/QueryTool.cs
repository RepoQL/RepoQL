using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Resources;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal partial class QueryTool(QueryExecutor queryExecutor, SelfTestRunner selfTestRunner, RepoResourceService resourceService)
{
    private const string QueryInstructions = """
                                             <CONCEPT>
                                                 Query is very powerful and allows you to do complex analysis and retrieval with all the power of DuckDB
                                                 HOWEVER: 
                                                    use xray first - it will always use less tokens than the query equivilent
                                                    use query when you need more control or your needs are complex
                                             </CONCEPT>
                                             
                                             <PURPOSE>
                                                 - Inventory the contents of a repository
                                                 - Find structures in files with semantic search, avoid reading files you don't need to
                                                 - Understand contents of files without token waste (Structure, relationships, dependencies, technologies)
                                                 - See linting errors across many file types (annotations)
                                                 - Understand "what uses this?" and "What links to this?" and "What breaks if I change this?"
                                                 - Perform complex analysis, regex extraction etc with all the power and flexability of SQL
                                             </PURPOSE>
                                             
                                             <CONTEXT>
                                                 - Dialect is DuckDB flavored SQL with custom UDFs
                                                 - Assume all file types are supported
                                                 - Every entity is represented by a repo URI e.g.
                                                   `file:///repo/lib.cs#symbol=Foo.Bar&line=12,20`
                                                   `docs:///quickstart`
                                                 - Semantic mime type indicates both file type and contents e.g.
                                                   `application/x-protobuf;kind=protobuf.message;schema="https://schemas.corp.com/user.proto";version=3`
                                             </CONTEXT>
                                             
                                             <SCHEMA>
                                                 Everything is a graph. Files are nodes with artifacts (bytes). Entities inside files (headings, functions, etc.) are child nodes connected by edges. Precise locations use spans. Everything else (lint, metrics, outlines) is annotations.
                                                 
                                                 Core Tables
                                                 
                                                 -- Content (bytes + text + x-ray summaries)
                                                 artifact(id, digest, media_type, text_content, headline, summary, structure)
                                                 
                                                 -- Entities (documents and everything inside them)
                                                 node(id, kind, uri, artifact_id, span_id, properties[JSON])
                                                 -- Only 'document' nodes have uri; others addressed via span
                                                 
                                                 -- Relationships (composition=tree, references=graph)
                                                 edge(id, source_node_id, destination_node_id, type, is_composition, ordinal)
                                                 -- type: 'HAS_PART' (composition) or 'REFERS_TO', 'CALLS', etc.
                                                 
                                                 -- Locations (precise line/char ranges)
                                                 span(id, document_id, start_line, end_line, start_byte, end_byte)
                                                 -- Lines: 1-based inclusive. Chars: 0-based half-open
                                                 
                                                 -- Diagnostics & facts (lint, outlines, metrics, etc.)
                                                 annotation(id, kind, severity, source, message, data[JSON], scope_document_id, target_node_id, resolved_target_uri)
                                             </SCHEMA>
                                             
                                             <ESSENTIAL_MACROS>
                                                 SELECT * FROM xray_documents()  -- inventory
                                                 SELECT * FROM snippet('file:///path#line=42', 3)  -- preview

                                                 -- Document search: search(keywords, scope, boost_pattern, k)
                                                 SELECT uri, score FROM search('auth JWT refresh', k := 10)
                                                 SELECT uri, score FROM search('config', scope := 'file:///src/%')
                                                 SELECT uri, score FROM search('parser', boost_pattern := 'markdown', negative_pattern := '(?i)test')

                                                 SELECT * FROM annotations WHERE severity = 'error'  -- diagnostics
                                             </ESSENTIAL_MACROS>

                                             Docs at docs:///quickstart.md, docs:///advanced-search.md

                                             <SEARCH_TIPS>
                                             search(keywords, scope, boost_pattern, k) → documents. search(q, k) → documents + objects
                                             - scope='document': files. scope='object': functions/classes/headings (URIs have #symbol=Foo&line=N)
                                             - Symbol exact match: 4.0 BM25. Objects get 5% boost.
                                             - dense_score NULL → embeddings loading (check: SELECT COUNT(*) FROM document_embedding)
                                             </SEARCH_TIPS>
                                             
                                             ## Examples
                                             
                                             ### List embedded RepoQL documentation
                                             
                                             ```postgresql
                                             SELECT
                                                   n.uri, /* e.g. docs:///querying-markdown.md*/
                                                   a.headline, /* Querying Markdown with RepoQL — querying-markdown.md | markdown.doc | 5725 | 151 lines | lang: sql | topics: Core Schema Mapping, Markdown Views, Markdown-Specific UDFs & Macros*/
                                                   a.summary, /* Most important details of contents, format depends on mime, < 10 lines */
                                                   a.structure /* Expanded details of contents, format depends on mime */
                                               FROM node AS n /* node = file contents, usually 1:1 with artifact */
                                               JOIN artifact AS a ON n.artifact_id = a.id /* artifact = node container (usually file) */
                                               WHERE n.kind = 'document' 
                                                 AND n.uri LIKE 'docs://%' /* docs are embedded, repo files usually file:/// */
                                               ORDER BY LOWER(n.uri);
                                             ```
                                             
                                             ### Fetch Content
                                             
                                             ```postgresql
                                             SELECT a.text_content
                                               FROM node AS n
                                               JOIN artifact AS a ON n.artifact_id = a.id
                                             WHERE n.uri = 'docs:///quickstart.md';
                                             ```
                                             
                                             ### List all markdown docs in repo + headlines
                                             ```postgresql
                                             SELECT
                                                   n.uri,
                                                   a.headline
                                               FROM node AS n
                                               JOIN artifact AS a ON n.artifact_id = a.id
                                               WHERE n.kind = 'document'
                                                 AND a.media_type LIKE '%markdown.doc%'
                                               ORDER BY LOWER(n.uri);
                                             /*
                                             Do this before starting work so that you know what documentation exists
                                             */
                                             ```
                                             
                                             ### Ranked semantic search + snippets
                                             
                                             ```postgresql
                                             WITH search_results AS (
                                                 SELECT uri, score
                                             FROM search('navigation loading progress bar', k := 3)
                                               )
                                               SELECT
                                                 sr.uri,
                                                 sr.score,
                                                 sn.line_number,
                                                 sn.text,
                                                 sn.is_focus
                                               FROM search_results AS sr,
                                                    LATERAL snippet(sr.uri, 2) AS sn
                                               ORDER BY sr.score DESC, sn.line_number;
                                               /*
                                               - search(keywords, ...) combines lexical + semantic (k := 3 limits results).
                                               - snippet(uri, 2) returns two lines of context around each match; is_focus marks the focal line.
                                               - Order by score DESC to get best matches first.
                                               */
                                             ```
                                             
                                             ```postgresql
                                             /* format-specific views */
                                             SELECT view_name FROM duckdb_views() ORDER BY view_name
                                             ```
                                             
                                             ### POSIX Command line
                                             
                                             Repoql is also available as a command line tool - useful for piping
                                             
                                             ```bash
                                             repoql query "WITH gql_headings AS (SELECT heading_uri, document_uri, text FROM markdown_headings snippet(heading_uri, 0, 120) AS paragraph FROM gql_headings" --format JsonLD \
                                               | jq -r '
                                                   .[]
                                                   | [
                                                       .uri,
                                                       .heading,
                                                       (.paragraph | gsub("\n"; " ") | truncate(160))
                                                     ]
                                                   | @tsv
                                               ' \
                                               | column -t -s $'\t'
                                             ```
                                               
                                             <INSTRUCTION>
                                             
                                             - Use Xray for anything you can - it will always use less tokens than Query
                                             - Use ReadMcpResourceTool when you want to read content and you know the URI
                                             - Use query when you need more control or your needs are complex
                                             - Remember that you can import additional repositories if you need more context
                                             
                                             </INSTRUCTION>
                                             """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "query"), Description(QueryInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> Query(
        [Description("DuckDB-style SQL to execute. Pass ':diagnostics:' to run diagnostics. Pass 'read:<uri>' for raw content, or 'read:<uri> // <question>' to summarize with LLM.")] string sql,
        [Description("Maximum number of rows to include when formatting the response.")] int maxRows = 500,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL query cannot be empty.", nameof(sql));

        // Special command: run diagnostics
        if (sql.Trim().Equals(":diagnostics:", StringComparison.OrdinalIgnoreCase))
        {
            return await selfTestRunner.RunAsync(cancel);
        }

        // Special command: read:<uri> [// <guidance>] - return raw content or LLM summary
        if (sql.StartsWith("read:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = sql[5..].Trim();

            // Pattern: <uri> // <guidance> - URI is non-whitespace, separator has spaces
            var match = ReadGuidancePattern().Match(rest);
            if (match.Success)
            {
                return await ReadWithGuidanceAsync(match.Groups[1].Value, match.Groups[2].Value, cancel).ConfigureAwait(false);
            }

            return await ReadResourceContentAsync(rest, cancel).ConfigureAwait(false);
        }

        try
        {
            var result = await queryExecutor.ExecuteAsync(sql, maxRows, ResultFormat.Toon, cancel).ConfigureAwait(false);
            return string.Join(Environment.NewLine, result.Lines);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString());

            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await selfTestRunner.RunAsync(cancel);
                return $"Error: {ex.Message}\n\n{diagnostics}";
            }

            // For user input errors (SQL syntax, etc.), just return the message
            return ex.Message;
        }
    }

    private async Task<string> ReadWithGuidanceAsync(string uri, string guidance, CancellationToken cancel)
    {
        try
        {
            var content = await ReadResourceContentAsync(uri, cancel).ConfigureAwait(false);

            // Extract base URI (without fragment) and starting line offset
            var (baseUri, startLine) = ParseUriAndOffset(uri);

            // Add line numbers so LLM can cite accurately
            var numberedContent = AddLineNumbers(content, startLine);

            // Build prompt that asks for evidence with line citations
            var prompt = $"""
                {guidance}

                IMPORTANT: Cite evidence with URIs in this exact format: {baseUri}#line=START,END (e.g. #line=42,45 not #lines=42-45)
                """;

            var escapedContent = numberedContent.Replace("'", "''", StringComparison.Ordinal);
            var escapedPrompt = prompt.Replace("'", "''", StringComparison.Ordinal);

            var sql = $"SELECT llm_summarize('{escapedContent}', '{escapedPrompt}')";
            var result = await queryExecutor.ExecuteAsync(sql, 1, ResultFormat.Toon, cancel).ConfigureAwait(false);
            return string.Join(Environment.NewLine, result.Lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static (string baseUri, int startLine) ParseUriAndOffset(string uri)
    {
        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex < 0)
            return (uri, 1);

        var baseUri = uri[..hashIndex];
        var fragment = uri[(hashIndex + 1)..];

        // Look for line=N or line=N,M
        var lineMatch = Regex.Match(fragment, @"line=(\d+)");
        var startLine = lineMatch.Success ? int.Parse(lineMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 1;

        return (baseUri, startLine);
    }

    private static string AddLineNumbers(string content, int startLine = 1)
    {
        var lines = content.Split('\n');
        var maxLine = startLine + lines.Length - 1;
        var width = maxLine.ToString(CultureInfo.InvariantCulture).Length;
        return string.Join('\n', lines.Select((line, i) =>
            $"{(startLine + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)}  {line.TrimEnd('\r')}"));
    }

    private async Task<string> ReadResourceContentAsync(string uri, CancellationToken cancel)
    {
        try
        {
            // Glob patterns need special handling - concatenate all matched files
            if (uri.Contains('*') || uri.Contains('?'))
            {
                var results = await resourceService.FetchGlobAsync(uri, cancel).ConfigureAwait(false);
                if (results.Count == 0)
                    return $"No files matched: {uri}";

                return string.Join("\n\n", results.Select(r =>
                    $"--- {r.Uri} ---\n{r.Text ?? "(empty)"}"));
            }

            var resource = await resourceService.FetchResourceAsync(uri, cancel).ConfigureAwait(false);
            return resource.Text ?? "(empty)";
        }
        catch (Exception ex)
        {
            return $"Error reading {uri}: {ex.Message}";
        }
    }

    [GeneratedRegex(@"^(\S+)\s+//\s+(.+)$")]
    private static partial Regex ReadGuidancePattern();
}
