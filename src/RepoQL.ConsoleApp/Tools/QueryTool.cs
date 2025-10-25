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
                                             -- Inventory: what files exist?
                                             SELECT * FROM xray_documents()  -- file_name, media_kind, byte_size, kinds_summary
                                             
                                             -- Explore: what's IN a file? (without reading it)
                                             SELECT * FROM xray_items('md_heading,cs_class', 10)  -- kind, label, uri (with #line=)
                                             
                                             -- Preview: show me lines around a location
                                             SELECT * FROM snippet('file:///path#line=42', 3)  -- context lines before/after
                                             
                                             -- Search: find files by intent (lexical + semantic)
                                             -- CRITICAL: 'question' is a named parameter, use question := syntax
                                             SELECT uri, score FROM file_search('auth token', question := 'How do we refresh JWTs?', k := 10)
                                             SELECT uri, score FROM file_search('DuckDB', question := NULL, k := 5)  -- keywords only
                                             SELECT uri, score, semn FROM file_search('', question := 'How does X work?', k := 5)  -- semantic only

                                             -- Path filters: glob_match(uri, 'docs/**/*.md') -> boolean
                                             SELECT uri, score FROM file_search('markdown', question := 'Where is onboarding documented?', k := 10)
                                             WHERE glob_match(uri, 'docs/**/*.md')
                                             
                                             -- Diagnostics: what's broken/flagged?
                                             SELECT * FROM annotations WHERE severity = 'error'
                                             SELECT * FROM annotations_for('file:///path', 'lint', 'warning')
                                             </ESSENTIAL_MACROS>
                                             
                                             The documentation for RepoQL can be read by querying - consider obtaining it to be the tutorial.

                                             <SEARCH_TIPS>
                                             file_search() returns: score (final), semn (semantic), bm25n (exact), fuzzn (typo tolerance)
                                             - If semn is NULL → embeddings not ready yet (check: SELECT COUNT(*) FROM document_embedding)
                                             - Keywords-only: Fast, exact matches. Question-only: Conceptual discovery. Combined: Best of both.
                                             - Use WHERE glob_match(uri, 'pattern') to filter by path after search
                                             </SEARCH_TIPS>
                                             
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
                                             FROM file_search('navigation loading', question := 'Why does the progress bar stick?', k := 3)
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
                                               - file_search(keywords, question := ...) combines lexical + semantic (k := 3 limits results).
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
                                             
                                             TL;DR: Use xray_documents() to see what exists, file_search(keywords, question) to find things, snippet() to preview, and artifact.headline/summary/structure to understand files without reading them. Everything else is just SQL JOINs on the graph.
                                               
                                             <INSTRUCTION>
                                             
                                             **DO NOW:**
                                             
                                             - [ ] List embedded RepoQL documentation 
                                               _So that you know what you don't know_
                                             - [ ] List all markdown docs in repo + headlines
                                               *So that you know what repository docs are available to you. Vital to your success in completing future tasks*
                                             - [ ] Read `embed:///quickstart.md`
                                               *Before attempting complex queries*
                                             
                                             </INSTRUCTION>
                                             
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
