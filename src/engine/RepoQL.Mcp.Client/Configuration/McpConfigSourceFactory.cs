using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Factory for creating MCP configuration sources.
///
/// Purpose: Centralizes creation of config sources, making it easy to
/// add new agent types and customize which sources are enabled.
///
/// Complexity: Simple factory pattern. Source-specific complexity is
/// isolated in individual source implementations.
/// </summary>
public static class McpConfigSourceFactory
{
    /// <summary>
    /// Creates all available config sources for a repository directory.
    /// </summary>
    /// <param name="repoDirectory">The repository root directory</param>
    /// <param name="enabledAgents">
    /// Which global agents to include. If null, includes all detected agents.
    /// Pass empty array to disable global config loading.
    /// </param>
    /// <param name="logger">Optional logger for config source diagnostics</param>
    /// <returns>List of config sources ordered by priority (lowest first)</returns>
    public static IReadOnlyList<IMcpConfigSource> CreateAll(
        string repoDirectory,
        IEnumerable<AgentType>? enabledAgents = null,
        ILogger? logger = null)
    {
        var sources = new List<IMcpConfigSource>();

        // Always include repo-level config source
        sources.Add(new DirectoryMcpConfigSource(repoDirectory, logger));

        // Add global agent sources
        var agentsToInclude = enabledAgents?.ToHashSet() ?? GetAllAgentTypes();

        foreach (var agentType in agentsToInclude)
        {
            var source = CreateAgentSource(agentType, logger);
            if (source != null)
            {
                sources.Add(source);
            }
        }

        // Sort by priority (lower values load first, higher values override)
        return sources.OrderBy(s => s.Priority).ToList();
    }

    /// <summary>
    /// Creates only the directory-based config source for a repository.
    /// Use this when you want to disable global config loading.
    /// </summary>
    public static IMcpConfigSource CreateDirectorySource(string repoDirectory, ILogger? logger = null)
    {
        return new DirectoryMcpConfigSource(repoDirectory, logger);
    }

    /// <summary>
    /// Creates a config source for a specific agent type.
    /// </summary>
    public static IMcpConfigSource? CreateAgentSource(AgentType agentType, ILogger? logger = null)
    {
        return agentType switch
        {
            AgentType.ClaudeCode => new ClaudeCodeConfigSource(logger),
            AgentType.ClaudeDesktop => new ClaudeDesktopConfigSource(logger),
            // TODO: Add other agents as needed
            // AgentType.Codex => new CodexConfigSource(logger),
            // AgentType.GitHubCopilotVSCode => new GitHubCopilotVSCodeConfigSource(logger),
            _ => null
        };
    }

    /// <summary>
    /// Gets all supported agent types.
    /// </summary>
    public static HashSet<AgentType> GetAllAgentTypes()
    {
        // Only return agents that have implementations
        return new HashSet<AgentType>
        {
            AgentType.ClaudeCode,
            AgentType.ClaudeDesktop
        };
    }

    /// <summary>
    /// Loads and merges configs from multiple sources.
    /// Later sources (higher priority) override earlier sources.
    /// </summary>
    /// <param name="sources">Config sources in priority order</param>
    /// <param name="selfServerName">Server name to exclude (prevents self-reference)</param>
    /// <returns>Merged configuration dictionary</returns>
    public static IReadOnlyDictionary<string, McpServerConfig> LoadAndMerge(
        IEnumerable<IMcpConfigSource> sources,
        string selfServerName = "repoql")
    {
        var merged = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources.OrderBy(s => s.Priority))
        {
            var configs = source.LoadConfigs();
            foreach (var (name, config) in configs)
            {
                // Skip self-references to prevent recursion
                if (string.Equals(name, selfServerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                merged[name] = config;
            }
        }

        return merged;
    }
}
