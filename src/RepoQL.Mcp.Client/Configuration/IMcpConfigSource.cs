namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Provides MCP server configurations from a specific source.
///
/// Purpose: Abstracts how MCP server configurations are discovered and loaded,
/// enabling extensible support for multiple configuration sources (repo-level,
/// global/user-level from different agents like Claude Code, Claude Desktop, etc.)
///
/// Complexity: Minimal - simple contract for loading configs. Source-specific
/// complexity (file locations, parsing) is isolated in implementations.
/// </summary>
public interface IMcpConfigSource
{
    /// <summary>
    /// Gets a human-readable name identifying this configuration source.
    /// Used for logging and diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the priority of this source. Lower values are loaded first,
    /// allowing higher-priority sources to override configurations.
    /// Standard priorities: Repository=100, Global=200, User=300
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Loads MCP server configurations from this source.
    /// </summary>
    /// <returns>
    /// Dictionary of server name to configuration. Empty if no configs found.
    /// Implementations should handle missing files gracefully.
    /// </returns>
    IReadOnlyDictionary<string, McpServerConfig> LoadConfigs();
}
