using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Loads MCP server configurations from Claude Code's global settings.
///
/// Purpose: Enables RepoQL to use MCP servers that users have installed
/// globally in Claude Code, providing seamless integration with the user's
/// existing Claude Code MCP ecosystem.
///
/// Complexity: Uses 'claude mcp list --json' CLI command for discovery,
/// with fallback to settings files. Handles cross-platform paths and
/// different scopes (user, project, local). The rest of the system sees
/// a standard config dictionary.
/// </summary>
public sealed class ClaudeCodeConfigSource : AgentMcpConfigSource
{
    private readonly string? _claudeExecutable;

    public ClaudeCodeConfigSource(ILogger? logger = null, string? claudeExecutable = null)
        : base(logger)
    {
        _claudeExecutable = claudeExecutable;
    }

    public override AgentType AgentType => AgentType.ClaudeCode;

    protected override IReadOnlyList<string> GetConfigPaths()
    {
        var paths = new List<string>();
        var home = GetUserHomeDirectory();

        if (string.IsNullOrEmpty(home))
            return paths;

        // Claude Code stores settings in ~/.claude/settings.json
        // and ~/.claude/settings.local.json for local overrides
        paths.Add(Path.Combine(home, ".claude", "settings.json"));
        paths.Add(Path.Combine(home, ".claude", "settings.local.json"));

        // Also check XDG config on Linux
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var xdgConfig = GetXdgConfigHome();
            if (!string.IsNullOrEmpty(xdgConfig))
            {
                paths.Add(Path.Combine(xdgConfig, "claude-code", "settings.json"));
            }
        }

        // macOS Application Support
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var appSupport = Path.Combine(home, "Library", "Application Support", "Claude Code");
            paths.Add(Path.Combine(appSupport, "settings.json"));
        }

        return paths;
    }

    /// <summary>
    /// Override to first try CLI discovery, then fall back to file-based loading.
    /// </summary>
    public new IReadOnlyDictionary<string, McpServerConfig> LoadConfigs()
    {
        // First try CLI-based discovery (most reliable, gets all scopes)
        try
        {
            var cliConfigs = LoadConfigsFromCli();
            if (cliConfigs.Count > 0)
            {
                Logger.LogDebug("Loaded {Count} MCP servers from Claude Code CLI", cliConfigs.Count);
                return cliConfigs;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "CLI-based MCP discovery failed, falling back to settings files");
        }

        // Fall back to settings file parsing
        return base.LoadConfigs();
    }

    private Dictionary<string, McpServerConfig> LoadConfigsFromCli()
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        var executable = _claudeExecutable ?? FindClaudeExecutable();
        if (string.IsNullOrEmpty(executable))
        {
            Logger.LogDebug("Claude Code CLI not found");
            return configs;
        }

        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                Arguments = "mcp list --json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return configs;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(10));

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return configs;

            return ParseClaudeCodeMcpList(output);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to run 'claude mcp list'");
            return configs;
        }
    }

    private Dictionary<string, McpServerConfig> ParseClaudeCodeMcpList(string json)
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            // Claude Code CLI outputs an array of server objects
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var config = ParseClaudeCodeServer(item);
                    if (config != null)
                    {
                        configs[config.Name] = config;
                    }
                }
            }
            // Or it might output an object with scopes
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var scope in doc.RootElement.EnumerateObject())
                {
                    // Skip non-object properties
                    if (scope.Value.ValueKind != JsonValueKind.Object &&
                        scope.Value.ValueKind != JsonValueKind.Array)
                        continue;

                    if (scope.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in scope.Value.EnumerateArray())
                        {
                            var config = ParseClaudeCodeServer(item);
                            if (config != null)
                            {
                                configs[config.Name] = config;
                            }
                        }
                    }
                    else
                    {
                        // Object with server definitions
                        foreach (var server in scope.Value.EnumerateObject())
                        {
                            var config = ParseClaudeCodeServerDefinition(server.Name, server.Value);
                            if (config != null)
                            {
                                configs[config.Name] = config;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to parse Claude Code MCP list output");
        }

        return configs;
    }

    private McpServerConfig? ParseClaudeCodeServer(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        // Try to get the server name
        string? name = null;
        if (item.TryGetProperty("name", out var nameProp))
            name = nameProp.GetString();
        else if (item.TryGetProperty("serverName", out var serverNameProp))
            name = serverNameProp.GetString();

        if (string.IsNullOrEmpty(name))
            return null;

        return ParseClaudeCodeServerDefinition(name, item);
    }

    private McpServerConfig? ParseClaudeCodeServerDefinition(string name, JsonElement serverDef)
    {
        // Determine transport type
        var type = "stdio";
        if (serverDef.TryGetProperty("type", out var typeProp))
            type = typeProp.GetString() ?? "stdio";
        else if (serverDef.TryGetProperty("transport", out var transportProp))
            type = transportProp.GetString() ?? "stdio";
        else if (serverDef.TryGetProperty("url", out _))
            type = "http";

        string? command = null;
        string[]? args = null;
        string? url = null;
        Dictionary<string, string>? env = null;
        Dictionary<string, string>? headers = null;

        if (serverDef.TryGetProperty("command", out var cmdProp))
            command = cmdProp.GetString();

        if (serverDef.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
            args = argsProp.EnumerateArray().Select(a => a.GetString() ?? "").ToArray();

        if (serverDef.TryGetProperty("url", out var urlProp))
            url = urlProp.GetString();

        if (serverDef.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
            env = envProp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

        if (serverDef.TryGetProperty("headers", out var headersProp) && headersProp.ValueKind == JsonValueKind.Object)
            headers = headersProp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

        return new McpServerConfig
        {
            Name = name,
            Type = type,
            Command = command,
            Args = args,
            Url = url,
            Env = env,
            Headers = headers
        };
    }

    protected override Dictionary<string, McpServerConfig> LoadConfigsFromPath(string path)
    {
        // Claude Code settings.json has mcpServers in a different structure
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            // Try standard format first
            var configs = McpConfigLoader.LoadFromJsonElement(doc.RootElement);
            if (configs.Count > 0)
                return configs;

            // Claude Code may store in different locations
            // Try looking for mcpServers at different paths in the JSON
            if (doc.RootElement.TryGetProperty("mcpServers", out var servers))
            {
                return ParseServersElement(servers);
            }

            return configs;
        }
        catch
        {
            return new Dictionary<string, McpServerConfig>();
        }
    }

    private Dictionary<string, McpServerConfig> ParseServersElement(JsonElement servers)
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        if (servers.ValueKind == JsonValueKind.Object)
        {
            foreach (var server in servers.EnumerateObject())
            {
                var config = ParseClaudeCodeServerDefinition(server.Name, server.Value);
                if (config != null)
                {
                    configs[config.Name] = config;
                }
            }
        }

        return configs;
    }

    private static string? FindClaudeExecutable()
    {
        // Check PATH first
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir.Trim(), GetClaudeExecutableName());
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Platform-specific fallbacks
        var candidates = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));
            candidates.Add(Path.Combine(appData, "npm", "claude"));

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "Programs", "Claude", "claude.exe"));
            candidates.Add(Path.Combine(localAppData, "Programs", "Claude Code", "claude.exe"));
        }
        else
        {
            candidates.Add("/usr/local/bin/claude");
            candidates.Add("/usr/bin/claude");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                candidates.Add(Path.Combine(home, ".local", "bin", "claude"));
            }

            candidates.Add("/opt/homebrew/bin/claude");
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string GetClaudeExecutableName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "claude.exe" : "claude";
}
