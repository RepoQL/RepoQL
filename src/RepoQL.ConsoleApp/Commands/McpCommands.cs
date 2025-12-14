using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Resources;
using RepoQL.ConsoleApp.Tools;
using ConsoleAppFramework;
using RepoQL.ConsoleApp.Logging;

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
           // Log startup info to stderr for debugging
           var cwd = Directory.GetCurrentDirectory();
           var repoRoot = RepoQL.Contracts.RepoLocator.FindRepoRoot(cwd);
           await Console.Error.WriteLineAsync($"[MCP] cwd={cwd}").ConfigureAwait(false);
           await Console.Error.WriteLineAsync($"[MCP] repoRoot={repoRoot}").ConfigureAwait(false);

           var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
           // Reduce graceful shutdown timeout from default 30s to 5s for faster exit
           builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(5));
           builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddConsole(consoleLogOptions =>
            {
                // Configure all logs to go to stderr
                consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            builder.Services.AddRepoQlConsoleServices();
            builder.Services.AddSingleton<RepoResourceService>();

            builder.Services
                .AddMcpServer(s =>
                {
                    s.InitializationTimeout = TimeSpan.FromSeconds(45);
                    s.ServerInstructions = """
                                           <CONCEPT>
                                           Treat the entities and structures contained inside repo files as a database to quickly understand repository contents and find features in many different file types
                                           **Read unfamiliar files only after exploring with RepoQL first**
                                           </CONCEPT>

                                           <PURPOSE>
                                           - Find things reliably when you don't know exact keywords using semantic search and regex 
                                           - Scan structures in files, avoid reading files you don't need to
                                           - Understand contents of files without token waste (Structure, relationships, dependencies, technologies)
                                           - See linting across many file types (annotations)
                                           - Understand "what uses this?" and "What links to this?" and "What breaks if I change this?"
                                           </PURPOSE>

                                           <CONTEXT>
                                           - Dialect is DuckDB flavored SQL with custom UDFs and macros
                                           - Assume all file types are supported
                                           - Every entity is represented by a repo URI e.g.
                                             `file:///repo/lib.cs#symbol=Foo.Bar&line=12,20`
                                             `docs:///guidance/writing-mermaid-documents.md`
                                           - Semantic mime type indicates both file type and contents e.g.
                                             `application/x-protobuf;kind=protobuf.message;schema="https://schemas.corp.com/user.proto";version=3`
                                           </CONTEXT>

                                           The documentation for RepoQL is embedded (docs://), and can be read by xray, query or reading resources - consider obtaining it to be the tutorial.
                                           
                                           <REMEMBER>
                                            - Xray should be your first tool for finding and understanding. Intent controls how tokens are spent in the response.
                                            - Use Query to do what xray cannot with all the power of SQL including applying semantic search and regex across files - don't use it to do what xray can.
                                            - Always map the territory with xray before reading whole files
                                            - Use ReadMcpResourceTool to read files or objects within them given a URI. Supports git-style URI globbing e.g.
                                                `file:///repo/src/**/*.cs`
                                                `file:///repo/MyClass.ts#symbol=Foo.*`
                                           </REMEMBER>
                                           """;
                })
                .WithStdioServerTransport()
                .WithTools<QueryTool>()
                .WithTools<XrayTool>()
                .WithTools<ImportTool>()
#if DEBUG
                .WithTools<SelfTestTool>()
#endif
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
                });

        builder.Services.AddHostedService<McpLoggingHostedService>();

        await builder.Build().RunAsync(cancel);
    }
}
