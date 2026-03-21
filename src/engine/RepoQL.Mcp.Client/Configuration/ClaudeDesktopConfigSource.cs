using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Loads MCP server configurations from Claude Desktop's settings.
///
/// Purpose: Enables RepoQL to use MCP servers that users have installed
/// in Claude Desktop, providing access to globally configured tools.
///
/// Complexity: Handles cross-platform config file locations (macOS,
/// Windows, Linux). Uses standard mcpServers JSON format.
/// </summary>
public sealed class ClaudeDesktopConfigSource : AgentMcpConfigSource
{
    public ClaudeDesktopConfigSource(ILogger? logger = null)
        : base(logger)
    {
    }

    public override AgentType AgentType => AgentType.ClaudeDesktop;

    protected override IReadOnlyList<string> GetConfigPaths()
    {
        var paths = new List<string>();
        var home = GetUserHomeDirectory();

        if (string.IsNullOrEmpty(home))
            return paths;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            paths.Add(Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = GetAppDataDirectory();
            if (!string.IsNullOrEmpty(appData))
            {
                paths.Add(Path.Combine(appData, "Claude", "claude_desktop_config.json"));
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var xdgConfig = GetXdgConfigHome();
            if (!string.IsNullOrEmpty(xdgConfig))
            {
                paths.Add(Path.Combine(xdgConfig, "Claude", "claude_desktop_config.json"));
                paths.Add(Path.Combine(xdgConfig, "claude", "claude_desktop_config.json"));
            }

            paths.Add(Path.Combine(home, ".claude", "claude_desktop_config.json"));
        }

        // WSL: Check Windows Claude Desktop config via /mnt/c
        var windowsHome = GetWindowsUserHomeInWsl();
        if (!string.IsNullOrEmpty(windowsHome))
        {
            var windowsAppData = Path.Combine(windowsHome, "AppData", "Roaming");
            paths.Add(Path.Combine(windowsAppData, "Claude", "claude_desktop_config.json"));
        }

        return paths;
    }
}
