using System.ComponentModel;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Diagnostics;

namespace RepoQL.ConsoleApp.Tools;

#if DEBUG
[McpServerToolType]
internal sealed class SelfTestTool(SelfTestRunner runner)
{
    private const string ToolDescription = """
        Runs comprehensive diagnostics for debugging RepoQL connection issues.

        Use this tool when:
        - Other tools fail with connection errors
        - The host process isn't starting properly
        - You need to verify the RepoQL environment

        Returns plain text diagnostic output showing:
        - Environment info (working directories, OS, env vars)
        - Repository detection status
        - Socket path and status
        - Host process info and recent output
        - Connection and health check results
        - Database accessibility
        """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "selftest")]
    [Description(ToolDescription)]
    public async Task<string> RunSelfTestAsync(CancellationToken cancellationToken = default)
    {
        return await runner.RunAsync(cancellationToken);
    }
}
#endif