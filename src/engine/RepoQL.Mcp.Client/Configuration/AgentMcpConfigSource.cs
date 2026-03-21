using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Base class for loading MCP configurations from AI agent applications.
///
/// Purpose: Provides common infrastructure for loading global MCP server
/// configurations from various AI agents (Claude Code, Claude Desktop,
/// Codex, GitHub Copilot, etc.). Each agent stores configs in different
/// locations with potentially different formats.
///
/// Complexity: Handles cross-platform path resolution and WSL detection.
/// Agent-specific config location and parsing logic is delegated to
/// abstract methods implemented by subclasses.
/// </summary>
public abstract class AgentMcpConfigSource : IMcpConfigSource
{
    protected readonly ILogger Logger;

    /// <summary>
    /// Standard priority for global/user-level agent configurations.
    /// Higher than repo-level, so global configs override if present.
    /// </summary>
    public const int StandardPriority = 200;

    protected AgentMcpConfigSource(ILogger? logger = null)
    {
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The agent type this source loads configs from.
    /// </summary>
    public abstract AgentType AgentType { get; }

    /// <inheritdoc />
    public string Name => $"Agent: {AgentType}";

    /// <inheritdoc />
    public virtual int Priority => StandardPriority;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, McpServerConfig> LoadConfigs()
    {
        try
        {
            var configPaths = GetConfigPaths();
            if (configPaths.Count == 0)
            {
                Logger.LogDebug("No config paths found for agent {AgentType}", AgentType);
                return new Dictionary<string, McpServerConfig>();
            }

            var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in configPaths)
            {
                if (!File.Exists(path))
                {
                    Logger.LogDebug("Agent config file not found: {Path}", path);
                    continue;
                }

                try
                {
                    var fileConfigs = LoadConfigsFromPath(path);
                    foreach (var (name, config) in fileConfigs)
                    {
                        configs[name] = config;
                    }
                    Logger.LogDebug("Loaded {Count} MCP servers from {AgentType} config: {Path}",
                        fileConfigs.Count, AgentType, path);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to load {AgentType} config from {Path}", AgentType, path);
                }
            }

            return configs;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load configs from {AgentType}", AgentType);
            return new Dictionary<string, McpServerConfig>();
        }
    }

    /// <summary>
    /// Gets the potential config file paths for this agent.
    /// Returns multiple paths in order of priority (later overrides earlier).
    /// </summary>
    protected abstract IReadOnlyList<string> GetConfigPaths();

    /// <summary>
    /// Loads MCP server configurations from a specific file path.
    /// Default implementation uses McpConfigLoader for JSON files.
    /// Override for agents with different config formats.
    /// </summary>
    protected virtual Dictionary<string, McpServerConfig> LoadConfigsFromPath(string path)
    {
        return McpConfigLoader.LoadFromFile(path);
    }

    #region Cross-Platform Path Helpers

    protected static string? GetUserHomeDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    protected static string? GetAppDataDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    protected static string? GetLocalAppDataDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    protected static string? GetXdgConfigHome() =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        ?? CombinePath(GetUserHomeDirectory(), ".config");

    protected static bool IsRunningInWsl()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        return File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop")
               || File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop-late");
    }

    protected static string? GetWindowsUserHomeInWsl()
    {
        if (!IsRunningInWsl())
            return null;

        var windowsUser = Environment.GetEnvironmentVariable("LOGNAME")
                          ?? Environment.GetEnvironmentVariable("USER");

        var windowsMounts = new List<string>();
        try
        {
            var mounts = File.ReadAllLines("/proc/mounts");
            foreach (var mount in mounts)
            {
                var parts = mount.Split(' ');
                if (parts.Length >= 3 && parts[2].Equals("drvfs", StringComparison.OrdinalIgnoreCase))
                {
                    windowsMounts.Add(parts[1]);
                }
            }
        }
        catch
        {
            windowsMounts.Add("/mnt/c");
        }

        if (windowsMounts.Count == 0)
            windowsMounts.Add("/mnt/c");

        foreach (var mount in windowsMounts)
        {
            var usersPath = Path.Combine(mount, "Users");
            if (!Directory.Exists(usersPath))
                continue;

            if (!string.IsNullOrEmpty(windowsUser))
            {
                var candidatePath = Path.Combine(usersPath, windowsUser);
                if (Directory.Exists(candidatePath) && Directory.Exists(Path.Combine(candidatePath, "AppData")))
                    return candidatePath;
            }

            try
            {
                foreach (var userDir in Directory.EnumerateDirectories(usersPath))
                {
                    var dirName = Path.GetFileName(userDir);
                    if (dirName.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Directory.Exists(Path.Combine(userDir, "AppData")))
                        return userDir;
                }
            }
            catch
            {
                // Ignore permission errors
            }
        }

        return null;
    }

    protected static string? CombinePath(string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
        }

        return current;
    }

    #endregion
}
