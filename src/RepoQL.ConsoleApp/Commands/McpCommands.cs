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
                                           RepoQL indexes your codebase into a queryable knowledge graph.

                                           <MENTAL_MODEL>
                                           **Don't read blind.** You have limited context. RepoQL lets you see structure before content, target specific symbols or lines, and control exactly how many tokens you spend. Explore first, then read what matters.

                                           **Everything is a graph.** Files contain symbols. Symbols have relationships. All queryable via SQL.

                                           **Everything is addressable.** URIs pinpoint anything: `file:///path#symbol=Name`, `file:///path#line=10,20`. Globs select many: `src/**/*.cs`. Combine with `;`, exclude with `!`.

                                           **Everything has summaries.** Headline (one line), structure (signatures), content (full text). You choose the level. Don't pay for content when structure answers the question.

                                           **Budget controls detail.** 500 tokens = shape. 2000 = structure. 5000 = depth. Set it based on what you need, not what exists.

                                           **Intent matches your knowledge state.**
                                           - Inventory: You don't know what's there yet. Breadth over depth — survey with or without search criteria. Keywords optional; when provided, they rank by relevance. Returns an index, not content.
                                           - Locate: You know the concept, not the location. Balanced — detail on matches, awareness of the rest. Enough context to decide what to read next.
                                           - Inspect: You know the target. Depth with context — concentrates tokens on relevant content and its surroundings.
                                           - Explain: You want understanding, not raw text. Massive compression — an LLM reads far more than you'd spend (often 50k → 1k). Returns synthesis, source URIs, and reasoning — verifiable, not a black box.

                                           **Why this matters:** Traditional search finds *most* results and you answer confidently — but gaps erode trust. Users run subagents to verify, wasting tokens and time. Explore searches wide first, so you see what exists before answering. No blind spots, no verification tax. You can say "I found everything related to X" — not "I found some things."

                                           You also never get stale data — RepoQL reindexes changes live and holds requests until everything in scope is ready. Complete and current, every time.

                                           This scales: traditional tools grow exponentially harder with codebase size. RepoQL stays flat — 10 million lines same as 10 thousand. Import external repos with `github://owner/repo` and query across them uniformly. Same patterns, same confidence, across boundaries.

                                           **The workflow:** explore → read. Discover the landscape, then commit tokens to what matters. This isn't two tools — it's the pattern.

                                           **More than files.** RepoQL queries git history alongside current state — who changed what, when, why. It parses structured data: JSON, CSV, Parquet, Excel. It calls other MCP servers from SQL, parses their results, and protects you from token bombs. One query surface for code, data, history, and external tools.
                                           </MENTAL_MODEL>

                                           <TOOLS>
                                           **explore** — Search wide, then focus. Explores broadly, ranks by relevance, allocates budget to surface what matters. You see what exists before committing tokens — no confident answers without knowing what you missed.
                                           **read** — Fetch known URIs. Append `=> modifier` for views (tree, history, blame, lint, question).
                                           **query** — SQL for aggregation, graph traversal, git history, cross-file analysis. Also: call MCP servers, parse JSON/CSV/Excel/Parquet.
                                           </TOOLS>

                                           <START>
                                           Orient yourself:
                                             read("file:///** => tree: folders", 500)

                                           Find something:
                                             explore(intent="Locate", keywords="authentication", tokenBudget=1500)
                                           </START>

                                           <HELP>
                                           RepoQL documentation is indexed at `help://` — search it like code, then read what's relevant.

                                           See what exists:
                                             read("help://** => tree: folders", 500)

                                           Find relevant docs:
                                             explore(intent="Locate", scope="help://**", keywords="graph traversal", tokenBudget=1500)

                                           Read specific headings or files (combine with semicolon):
                                             read("help:///quickstart.md#symbol=Search;help:///quickstart.md#symbol=Query;help:///repoql/tools/read/modifiers.md", 3000)
                                           </HELP>
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
