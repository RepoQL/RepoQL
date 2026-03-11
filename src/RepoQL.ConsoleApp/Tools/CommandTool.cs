using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.Commands;

namespace RepoQL.ConsoleApp.Tools;

/// <summary>
/// Purpose: MCP tool for imperative commands — diagnostics, reindex, config, etc.
/// Complexity: Thin adapter from MCP input to CommandParser + CommandRegistry dispatch.
/// </summary>
[McpServerToolType]
internal sealed class CommandTool(CommandRegistry commandRegistry)
{
    private const string CommandInstructions = """
        <CONCEPT>
        Imperative commands for administration and diagnostics.
        Use command when you need to DO something (reindex, configure, diagnose)
        </CONCEPT>

        <DISCOVERY>
        List all available commands:
        command(command="?")

        Get help for a specific command:
        command(command="diagnostics --help")

        </DISCOVERY>

        <COMMON>
        `diagnostics` | Run full system health diagnostics
        `diagnostics.fast` | Quick health checks
        `diagnostics.memory` | Host memory breakdown
        `diagnostics.memory.heap` | Top managed heap types (expensive)
        `config` | View/change configuration
        `host.stop` | Stop the repoql host
        `host.restart` | Restart the repoql host
        `queue.cancel` | Cancel one file at next stage boundary
        `queue.skip` | Persistently skip one file
        `queue.retry` | Re-enqueue one failed/skipped file
        `dashboard` | show the user a dashboard of the current state of the database
        </COMMON>
        """;

    [McpServerTool(Name = "command", Title = "Run Command", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false), Description(CommandInstructions)]
    [McpMeta("defer_loading", false)]
    public async Task<CallToolResult> RunAsync(
        [Description("Command to run (e.g. 'diagnostics', 'diagnostics[fast]', 'config --help', '?')")] string command,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("Command cannot be empty. Use command(command=\"?\") to list available commands.");

        // Ensure :: prefix for the parser — agents shouldn't need to type it
        var input = command.Trim();
        if (!input.StartsWith("::"))
            input = $"::{input}";

        var parsed = CommandParser.TryParse(input);
        if (parsed == null)
            return ToolResult.Error("Could not parse command. Use command(command=\"?\") to list available commands.");

        var result = await commandRegistry.ExecuteAsync(parsed, cancel);
        return result.IsError ? ToolResult.Error(result.Text) : ToolResult.Success(result.Text);
    }
}
