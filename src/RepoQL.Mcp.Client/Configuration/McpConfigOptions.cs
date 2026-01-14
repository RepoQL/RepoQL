namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Configuration options for MCP server discovery and loading.
///
/// Purpose: Allows users to customize which MCP configuration sources
/// are enabled. Can enable/disable global agent configs (Claude Code,
/// Claude Desktop) or restrict to repo-level only.
///
/// Complexity: Simple options record. Environment variable support
/// enables configuration without code changes.
/// </summary>
public sealed record McpConfigOptions
{
    /// <summary>
    /// When true (default), loads MCP servers from global agent configs
    /// (Claude Code, Claude Desktop, etc.) in addition to repo-level configs.
    /// Set to false to only use repo-level .mcp.json files.
    /// </summary>
    public bool IncludeGlobalAgents { get; init; } = true;

    /// <summary>
    /// Specific agent types to include. When null (default), includes all
    /// available agents. Set to empty collection to disable global loading.
    /// </summary>
    public IReadOnlySet<AgentType>? EnabledAgents { get; init; }

    /// <summary>
    /// Server name to exclude to prevent self-reference loops.
    /// Defaults to "repoql".
    /// </summary>
    public string SelfServerName { get; init; } = "repoql";

    /// <summary>
    /// Creates options from environment variables.
    /// REPOQL_MCP_INCLUDE_GLOBALS: "true" or "false"
    /// REPOQL_MCP_ENABLED_AGENTS: comma-separated list of agent types
    /// </summary>
    public static McpConfigOptions FromEnvironment()
    {
        var options = new McpConfigOptions();

        var includeGlobals = Environment.GetEnvironmentVariable("REPOQL_MCP_INCLUDE_GLOBALS");
        if (!string.IsNullOrEmpty(includeGlobals))
        {
            options = options with { IncludeGlobalAgents = !string.Equals(includeGlobals, "false", StringComparison.OrdinalIgnoreCase) };
        }

        var enabledAgentsStr = Environment.GetEnvironmentVariable("REPOQL_MCP_ENABLED_AGENTS");
        if (!string.IsNullOrEmpty(enabledAgentsStr))
        {
            var enabledAgents = new HashSet<AgentType>();
            foreach (var agentStr in enabledAgentsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<AgentType>(agentStr, ignoreCase: true, out var agent))
                {
                    enabledAgents.Add(agent);
                }
            }
            options = options with { EnabledAgents = enabledAgents };
        }

        return options;
    }
}
