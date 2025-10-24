using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Tools;
using RepoQL.ConsoleApp.Resources;
using ConsoleAppFramework;

namespace RepoQL.ConsoleApp.Commands;

[RegisterCommands]
internal class McpCommands
{
    /// <summary>
    ///    Runs RepoQL as an MCP server 
    /// </summary>
    /// <param name="cancel"></param>
    public async Task Mcp(CancellationToken cancel = default)
    {
           var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
           builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddConsole(consoleLogOptions =>
            {
                // Configure all logs to go to stderr
                consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            builder.Services.AddRepoQlConsoleServices();

            builder.Services
                .AddMcpServer(s =>
                {
                    s.InitializationTimeout = TimeSpan.FromSeconds(45);
                    s.ServerInstructions = """
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
                                           
                                           ### Repository Navigation - RepoQL
                                           
                                           Capsule: **RepoQLVision** 👁️ Navigation
                                           RepoQL = x-ray vision + semantic search + SQL composability without consuming context.
                                           
                                           **Example**
                                           
                                           ```sql
                                           -- See document inventory instantly
                                           SELECT * FROM xray_documents() WHERE file_name LIKE '%docs/%'
                                           
                                           -- vs Read tool: 1 file = full context consumed
                                           ```
                                           
                                           Capsule: **IntentSearch** 🔍 Discovery
                                           file_search() blends lexical + fuzzy + semantic—express intent, not keywords.
                                           
                                           **Example**
                                           
                                           ```sql
                                           -- Natural language intent query
                                           SELECT uri, score, semn
                                           FROM file_search('authentication patterns JWT token refresh', k := 10)
                                           ORDER BY semn DESC NULLS LAST
                                           ```
                                           
                                           Capsule: **SQLCompose** 🔗 Power
                                           JOIN semantic search with structural views for deep insight.
                                           
                                           **Example**
                                           
                                           ```sql
                                           -- Find top matches + see their structure
                                           WITH hits AS (
                                             SELECT uri, score FROM file_search('authentication login', k := 5)
                                           )
                                           SELECT h.uri, mh.level, mh.text
                                           FROM hits h
                                           JOIN markdown_headings mh ON mh.document_uri = h.uri
                                           ORDER BY h.score DESC, mh.start_line
                                           ```
                                           
                                           Capsule: **SelfDocumenting** 📚 Meta
                                           Documentation lives IN the database as embed:// URIs—query to learn.
                                           
                                           **Example**
                                           
                                           ```sql
                                           -- Read embedded docs
                                           SELECT text_content FROM artifact a
                                           JOIN node n ON n.artifact_id = a.id
                                           WHERE n.uri = 'embed:///quickstart.md'
                                           ```
                                           
                                           Capsule: **ProgressiveSemantics** ⏳ Async
                                           semn (semantic score) fills progressively after startup—may be NULL initially.
                                           
                                           **Example**
                                           
                                           ```sql
                                           -- Order by semantic when ready, lexical fallback
                                           SELECT uri, COALESCE(semn, bm25n) as relevance
                                           FROM file_search('topic', k := 20)
                                           ORDER BY relevance DESC
                                           ```
                                           
                                           **☑ RepoQL Non-Negotiables**
                                           ☑ Use artifact.headline, artifact.summary, artifact.structure for inventory, file_search() for discovery
                                           ☑ Set k parameter to limit breadth (k := 10, k := 50)
                                           ☑ Query duckdb_views() to discover available views
                                           ☑ Compose with JOINs—search → structure → insight
                                           ☑ Map territory with queries BEFORE reading files
                                           
                                           ## Tool Selection: The Decision Process
                                           
                                           ### Core Mental Model
                                           
                                           Capsule: **IntentFirst** 🎯 Core  
                                           Intent determines tool, not features.
                                           
                                           Capsule: **ThreeQuestions** ❓ Decision  
                                           What doing? What kind? How specific?
                                           
                                           **Example**
                                           
                                           ```
                                           1. Finding something? Reading? Changing?
                                           2. Code? Knowledge/docs? Files? Web?
                                           3. Exact match? Concept? Explore?
                                           ```
                                           
                                           Capsule: **UniversalFlow** 🌊 Pattern  
                                           Search→Read→Edit. Always this order.
                                           """;
                })
                .WithStdioServerTransport()
                .WithTools<QueryTool>()
                .WithTools<XrayTool>()
                .WithListResourceTemplatesHandler((ctx, ct) =>
                {
                    ArgumentNullException.ThrowIfNull(ctx.Services);
                    var service = ctx.Services.GetRequiredService<RepoResourceService>();
                    return service.ListTemplatesAsync(ctx, ct);
                })
                .WithReadResourceHandler((ctx, ct) =>
                {
                    ArgumentNullException.ThrowIfNull(ctx.Services);
                    var service = ctx.Services.GetRequiredService<RepoResourceService>();
                    return service.ReadResourceAsync(ctx, ct);
                })
                .WithListResourcesHandler((ctx, ct) =>
                {
                    ArgumentNullException.ThrowIfNull(ctx.Services);
                    var service = ctx.Services.GetRequiredService<RepoResourceService>();
                    return service.ListResourcesAsync(ctx, ct);
                });

        await builder.Build().RunAsync(cancel);
    }
}
