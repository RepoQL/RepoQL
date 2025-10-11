using System.Threading;
using System.Threading.Tasks;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Tools;

namespace RepoQL.ConsoleApp.Commands;

[RegisterCommands]
internal class McpCommands
{
    /// <summary>
    ///    Queries the structure  repository 
    /// </summary>
    /// <param name="cancel"></param>
    public async Task Mcp(CancellationToken cancel = default)
    {
           var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.AddConsole(consoleLogOptions =>
            {
                // Configure all logs to go to stderr
                consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            builder.Services.AddRepoQlConsoleServices();

            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<QueryTool>();
    
    //.WithResources<SimpleResourceType>()

        await builder.Build().RunAsync(cancel);
    }
}
