using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Loads MCP server configurations from files in a repository directory.
///
/// Purpose: Provides repo-level MCP server configuration, allowing projects
/// to define their own MCP servers that RepoQL can connect to.
///
/// Complexity: Handles cascading config file discovery (.mcp.json,
/// .repoql.mcp.json, .repoql/.mcp.json) with merge semantics. Later files
/// override earlier ones. The rest of the system sees a unified config set.
/// </summary>
public sealed class DirectoryMcpConfigSource : IMcpConfigSource
{
    private readonly string _directory;
    private readonly ILogger _logger;

    /// <summary>
    /// Standard priority for repository-level configuration.
    /// Lower than global configs, allowing global to override if needed.
    /// </summary>
    public const int StandardPriority = 100;

    public DirectoryMcpConfigSource(string directory, ILogger? logger = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger ?? NullLogger.Instance;
    }

    public string Name => $"Directory: {_directory}";

    public int Priority => StandardPriority;

    public IReadOnlyDictionary<string, McpServerConfig> LoadConfigs()
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // Load configs in order (later overrides earlier)
        var configPaths = new[]
        {
            Path.Combine(_directory, ".mcp.json"),
            Path.Combine(_directory, ".repoql.mcp.json"),
            Path.Combine(_directory, ".repoql", ".mcp.json")
        };

        foreach (var path in configPaths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                var fileConfigs = McpConfigLoader.LoadFromFile(path);
                foreach (var (name, config) in fileConfigs)
                {
                    configs[name] = config;
                }
                _logger.LogDebug("Loaded MCP config from {Path} ({Count} servers)", path, fileConfigs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load MCP config from {Path}", path);
            }
        }

        return configs;
    }
}
