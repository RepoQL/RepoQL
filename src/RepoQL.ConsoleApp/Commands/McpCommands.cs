using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
                                           RepoQL indexes your codebase into a queryable knowledge graph. Find things reliably without exact keywords. Understand structure without reading files. See what uses what, what breaks if you change something.

                                           Everything is addressable via URIs: files, symbols, line ranges, documentation.
                                           Everything has pre-computed summaries: headlines, structure, semantic types.
                                           You rarely need to read full content.

                                           <TOOLS>
                                           **explore** — Find and understand. Start here.
                                           Intents: Inventory (what exists), Locate (where is X), Inspect (show me X), Explain (how does X work)

                                           **read** — Fetch from known URIs.
                                           Content, structure, git history, blame, diagnostics, or answers to questions about the content.

                                           **query** — SQL when you need computation or graph traversal.
                                           </TOOLS>

                                           <CONCEPTS>
                                           **URIs**: `file:///src/Auth.cs`, `#symbol=Validate`, `#line=10,20`, `help:///quickstart.md`
                                           **Globs**: `src/**/*.cs`, `src/**;!**/tests/**`, `#symbol=*Handler`
                                           **Budget**: investment not limit — more tokens = richer detail
                                           </CONCEPTS>

                                           <DOCS>
                                           RepoQL's documentation is embedded and queryable at help://

                                           See what's available:
                                             read("help://** => tree: headlines", 2000)

                                           Learn how to do something:
                                             explore(intent="Explain", scope="help://**", keywords="how do I find all usages of a function")
                                           </DOCS>
                                           """;
                })
                .WithStdioServerTransport()
                .WithTools<QueryTool>()
                .WithTools<ExploreTool>()
                .WithTools<ReadTool>()
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
