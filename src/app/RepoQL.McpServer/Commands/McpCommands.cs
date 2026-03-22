using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoQL.Commands;
using RepoQL.McpServer.Helpers;
using RepoQL.McpServer.Resources;
using RepoQL.McpServer.Tools;
using RepoQL.McpServer.Logging;

namespace RepoQL.McpServer.Commands;

public class McpCommands
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

            builder.Services.AddRepoQlMcpServices();

            builder.Services
                .AddMcpServer(s =>
                {
                    s.InitializationTimeout = TimeSpan.FromSeconds(45);
                    s.ServerInstructions = """
                                           RepoQL gives you a pre-built structural index of the entire codebase — every file, symbol, and relationship already parsed, connected, and summarized.

                                           Think of it as extra senses. You can feel the shape of a thousand files without opening one — the index has three summary levels: headline (one line), structure (signatures), and content. You can see relationships that grep will never find — what calls what, what depends on what. You can hear relevance — explore ranks by meaning, not literal text, showing everything that exists before you commit to reading anything. And you can reach precisely — a single method body, a line range, a glob across every file in the codebase.

                                           <CAPSULES>
                                           ### Capsule: Addressability

                                           **Invariant**
                                           Everything in the index is addressable by URI — files, symbols within files, line ranges, and across globs.

                                           **Example**
                                           `read("file:///src/**/*.cs#symbol=*FileSystem => structure", 3000)` — every filesystem implementation's signatures across the entire codebase. One call.

                                           **Depth**
                                           - `#symbol=Name` targets a symbol; `#symbol=Class.*` all members; `#symbol=Class.**` all descendants
                                           - `#line=42,60` targets a line range. Globs: `file:///src/**/*.cs`. Combine with `;`, exclude with `!`
                                           - `=> structure` shows signatures without bodies. `=> tree: headlines` shows directory overview with summaries
                                           - `=> find: keywords` does semantic search within scope. `=> question: how does X work?` synthesizes an answer
                                           - This is not file reading — it's querying the index for exactly the slice you need

                                           ---

                                           ### Capsule: ExploreFirst

                                           **Invariant**
                                           A broad explore reveals the landscape AND the vocabulary — the class names, patterns, and terms you need for everything after.

                                           **Example**
                                           You need to understand authentication. Your first explore returns: `JwtTokenValidator`, `SessionMiddleware`, `OAuthConfig`, `SecurityPolicy`. Now you know the real names. Your next reads use `#symbol=JwtTokenValidator.Validate => structure` — precise, cheap, informed. Without that first explore, you'd be guessing names and grepping blind.

                                           **Depth**
                                           - explore searches the full index exhaustively — you see everything that matches, ranked by relevance
                                           - Budget is a bet: start at 1500, iterate. Breadth=8 surveys many; breadth=2 examines few deeply
                                           - The first explore is never wasted — even unexpected results teach you what IS there

                                           ---

                                           ### Capsule: WieldWithCreativity

                                           **Invariant**
                                           The index is wild magic — composable, responsive to intent, and forgiving. Your instincts are probably right. Try them.

                                           **Example**
                                           - Glob across symbols: `#symbol=*Handler.Execute*` — every Execute method on every Handler
                                           - Search within a scope: `file:///src/Auth/** => find: token refresh`
                                           - Combine URIs: `file:///a.cs#symbol=Foo;file:///b.cs#symbol=Bar` — two methods, one call
                                           - Ask the code: `file:///src/Auth/** => question: how does token validation work?`
                                           - SQL the graph: `SELECT source_uri, target_uri FROM edge WHERE kind = 'CALLS'`

                                           **Depth**
                                           - A bad query costs 1500 tokens. A good one saves 50k. The risk is always asymmetric — experiment freely
                                           - Combine modifiers with globs and fragments for arbitrarily precise queries
                                           - `explain(question="...", uriGlob="file:///specific/area/**")` synthesizes an answer from exactly the right code — but scope it to what you've already found
                                           </CAPSULES>

                                           <TOOLS>
                                           **explore** — Discover what exists. Reveals the landscape AND the vocabulary. Start here.
                                           **read** — Fetch content by URI. Symbol fragments, globs, modifiers.
                                           **query** — SQL over the graph. Count, list, traverse relationships.
                                           **explain** — Synthesized answer scoped to specific directories. Always scope with uriGlob.
                                           **execute** — JavaScript in a sandboxed WASM environment with access to query and the file system.
                                           **command** — Diagnostics, auth, config. `command(command="?")` lists all.
                                           </TOOLS>

                                           <BOUNDARIES>
                                           - Never read a file to discover its structure — the index has it pre-computed
                                           - Never search without seeing the landscape first — explore teaches you the vocabulary
                                           - Never use explain without scoping it to specific directories
                                           </BOUNDARIES>

                                           <START>
                                           Explore what exists, then read what matters:
                                             explore(keywords="authentication middleware", tokenBudget=1500)

                                           See the shape of the codebase:
                                             read("file:///** => tree: folders", 3000)

                                           Documentation lives at `help://` — queryable with the same tools:
                                             explore(uriGlob="help://**", keywords="modifiers views", tokenBudget=1500)
                                           </START>
                                           """;
                })
                .WithStdioServerTransport()
                .WithTools<QueryTool>()
                .WithTools<ExploreTool>()
                .WithTools<ExplainTool>()
                .WithTools<ReadTool>()
                .WithTools<ImportTool>()
                .WithTools<CommandTool>()
                .WithTools<ExecuteTool>()
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

        var host = builder.Build();
        host.Services.GetRequiredService<CommandRegistry>().DiscoverCommands();
        await host.RunAsync(cancel);
    }
}
