using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Diagnostics;

namespace RepoQL.ConsoleApp.Tools;

#if DEBUG
[McpServerToolType]
internal sealed class SelfTestTool(SelfTestRunner runner, IMcpServer mcpServer)
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
        - MCP client capabilities (what the client supports)
        """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "selftest")]
    [Description(ToolDescription)]
    public async Task<string> RunSelfTestAsync(CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        // Add MCP client capabilities first
        sb.AppendLine("=== MCP Client Capabilities ===");
        sb.AppendLine();
        try
        {
            var clientInfo = mcpServer.ClientInfo;
            var clientCaps = mcpServer.ClientCapabilities;

            sb.AppendLine("Client Info:");
            if (clientInfo is not null)
            {
                var clientInfoJson = JsonSerializer.Serialize(clientInfo, new JsonSerializerOptions { WriteIndented = true });
                foreach (var line in clientInfoJson.Split('\n'))
                {
                    sb.AppendLine($"  {line}");
                }
            }
            else
            {
                sb.AppendLine("  (unknown)");
            }
            sb.AppendLine();

            sb.AppendLine("Client Capabilities:");
            if (clientCaps is not null)
            {
                var json = JsonSerializer.Serialize(clientCaps, new JsonSerializerOptions { WriteIndented = true });
                foreach (var line in json.Split('\n'))
                {
                    sb.AppendLine($"  {line}");
                }
            }
            else
            {
                sb.AppendLine("  (none reported)");
            }
            sb.AppendLine();

            sb.AppendLine("MCP Server State:");
            sb.AppendLine($"  LoggingLevel: {mcpServer.LoggingLevel?.ToString() ?? "(not set)"}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Error getting capabilities: {ex.Message}");
        }
        sb.AppendLine();

        // Then add the regular diagnostics
        var diagnostics = await runner.RunAsync(DiagnosticCollectionMode.Full, cancellationToken);
        sb.Append(diagnostics);

        return sb.ToString();
    }
}
#endif
