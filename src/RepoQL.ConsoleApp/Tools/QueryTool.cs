using System.ComponentModel;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Helpers;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal class QueryTool(QueryExecutor queryExecutor)
{
    private const string QueryInstructions = """
                                             # Repository Query Language

                                             <CONCEPT>
                                             Treat the entities and structures contained inside repo files as a database to quickly understand repository contents and find features in many different file types

                                             **Read unfamiliar files only after searching with  RepoQL first**

                                             </CONCEPT>

                                             <PURPOSE>

                                             - Find structures in files with semantic search, avoid reading files you don't need to
                                             - Understand contents of files without token waste (Structure, relationships, dependencies, technologies)
                                             - See linting errors across many file types (annotations)
                                             - Understand "what uses this?" and "What links to this?" and "What breaks if I change this?"

                                             </PURPOSE>

                                             <CONTEXT>

                                             - Dialect is DuckDB flavored SQL with custom UDFs
                                             - Assume all file types are supported
                                             - Every entity is represented by a repo URI e.g.
                                               `file:///repo/lib.cs#symbol=Foo.Bar&line=12,20`
                                               `embed:///quickstart`
                                             - Semantic mime type indicates both file type and contents e.g.
                                               `application/x-protobuf;kind=protobuf.message;schema="https://schemas.corp.com/user.proto";version=3`

                                             </CONTEXT>

                                             The documentation for RepoQL can be read by querying - consider obtaining it to be the tutorial.

                                             ## Examples

                                             ### List embedded RepoQL documentation

                                             ```postgresql
                                             SELECT
                                                   n.uri, /* e.g. embed:///querying-markdown.md*/
                                                   a.headline, /* Querying Markdown with RepoQL — querying-markdown.md | markdown.doc | 5725 | 151 lines | lang: sql | topics: Core Schema Mapping, Markdown Views, Markdown-Specific UDFs & Macros*/
                                                   a.summary, /* Most important details of contents, format depends on mime, < 10 lines */
                                                   a.structure /* Expanded details of contents, format depends on mime, < 25 lines */
                                               FROM node AS n /* node = file contents, usually 1:1 with artifact */
                                               JOIN artifact AS a ON n.artifact_id = a.id /* artifact = node container (usually file) */
                                               WHERE n.kind = 'document' 
                                                 AND n.uri LIKE 'embed://%' /* docs are embedded, repo files usually file:/// */
                                               ORDER BY LOWER(n.uri);
                                             ```

                                             ### Fetch Content

                                             ```postgresql
                                             SELECT a.text_content
                                               FROM node AS n
                                               JOIN artifact AS a ON n.artifact_id = a.id
                                               WHERE n.uri = 'embed:///quickstart.md';
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
                                                 FROM file_search('navigation loading bar timeout', k := 3)
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
                                               - file_search does the semantic lookup (k := 3 keeps the top three hits).
                                               - snippet(uri, 2) returns two lines of context around each match; is_focus marks the snippet’s focal line.
                                               - Ordering by score first keeps the best semantic hits at the top, then list the snippet lines in-order.
                                               Tweak the search phrase, k, or context window to suit your needs.
                                               */
                                             ```

                                             <INSTRUCTION>

                                             **DO NOW:**

                                             - [ ] List embedded RepoQL documentation 
                                               _So that you know what you don't know_
                                             - [ ] List all markdown docs in repo + headlines
                                               *So that you know what repository docs are available to you. Vital to your success in completing future tasks*
                                             - [ ] Read `embed:///quickstart.md`
                                               *Before attempting complex queries*

                                             <INSTRUCTION>

                                             Do the above before attempting any non-trivial work.
                                             """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "query"), Description(QueryInstructions)]
    public async Task<string> Query(
        [Description("DuckDB-style SQL to execute")] string sql,
        [Description("Maximum number of rows to include when formatting the response.")] int maxRows = 500,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL query cannot be empty.", nameof(sql));

        try
        {
            var result = await queryExecutor.ExecuteAsync(sql, maxRows, ResultFormat.JsonLD, cancel).ConfigureAwait(false);
            return string.Join(Environment.NewLine, result.Lines);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString());
            // Return detailed error information to help debug SQL issues
            return ex.Message;
        }
    }
}
