using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConsoleAppFramework;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Commands;

[RegisterCommands]
internal class InstallCommand(IAnsiConsole console)
{
    /// <summary>
    /// Install RepoQL as an MCP server for AI agents (Claude, Codex)
    /// </summary>
    public async Task Install(CancellationToken cancel = default)
    {
        console.MarkupLine("[bold cyan]RepoQL MCP Server Installation[/]");
        console.WriteLine();

        // Detect installed agents
        var agents = await DetectInstalledAgentsAsync();

        if (agents.Count == 0)
        {
            console.MarkupLine("[yellow]No supported agents detected.[/]");
            console.MarkupLine("Supported agents: Claude Desktop, Claude CLI, Codex");
            console.WriteLine();
            console.MarkupLine("You can manually add RepoQL to your agent's MCP configuration:");
            console.WriteLine();
            console.MarkupLine("[dim]{[/]");
            console.MarkupLine("[dim]  \"mcpServers\": {[/]");
            console.MarkupLine("[dim]    \"repoql\": {[/]");
            console.MarkupLine("[dim]      \"type\": \"stdio\",[/]");
            console.MarkupLine("[dim]      \"command\": \"repoql\",[/]");
            console.MarkupLine("[dim]      \"args\": [\"mcp\"],[/]");
            console.MarkupLine("[dim]      \"env\": {[/]");
            console.MarkupLine("[dim]        \"REPOQL_CWD\": \"/path/to/your/repo\"[/]");
            console.MarkupLine("[dim]      }[/]");
            console.MarkupLine("[dim]    }[/]");
            console.MarkupLine("[dim]  }[/]");
            console.MarkupLine("[dim]}[/]");
            console.WriteLine();
            console.MarkupLine("[dim]Note: For Codex/Claude CLI, use \"{{workspace}}\" as the REPOQL_CWD value.[/]");
            return;
        }

        // Show detected agents
        console.MarkupLine($"[green]Found {agents.Count} agent(s):[/]");
        foreach (var agent in agents)
        {
            console.MarkupLine($"  • [cyan]{agent.Name}[/] - {agent.ConfigPath}");
        }
        console.WriteLine();

        // Interactive selection menu
        var choices = agents
            .Select(a => $"{a.Name} ({a.ConfigPath})")
            .Concat(["Install for all detected agents", "Cancel"])
            .ToList();

        var selection = console.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select where to install RepoQL:[/]")
                .PageSize(10)
                .AddChoices(choices));

        if (selection == "Cancel")
        {
            console.MarkupLine("[yellow]Installation cancelled.[/]");
            return;
        }

        // Determine which agents to update
        List<AgentInfo> agentsToUpdate;
        if (selection == "Install for all detected agents")
        {
            agentsToUpdate = agents;
        }
        else
        {
            var selectedAgent = agents.First(a => selection == $"{a.Name} ({a.ConfigPath})");
            agentsToUpdate = [selectedAgent];
        }

        // For Claude Desktop, ask for repository path
        foreach (var agent in agentsToUpdate.Where(a => a.Type == AgentType.ClaudeDesktop))
        {
            console.WriteLine();
            console.MarkupLine($"[cyan]{agent.Name}[/] needs to know which repository to use.");
            console.MarkupLine("[dim]RepoQL requires a working directory to index and query your code.[/]");
            console.WriteLine();

            var repoPath = console.Ask<string>("Enter the [green]full path[/] to your repository:");

            // Validate the path
            if (!Directory.Exists(repoPath))
            {
                console.MarkupLine("[yellow]⚠ Warning: Directory does not exist. Configuration will be created anyway.[/]");
            }

            agent.WorkingDirectory = repoPath;
        }

        // Install to selected agents
        console.WriteLine();
        foreach (var agent in agentsToUpdate)
        {
            await InstallToAgentAsync(agent, cancel);
        }

        console.WriteLine();
        console.MarkupLine("[bold green]✓ Installation complete![/]");
        console.WriteLine();
        console.MarkupLine("[dim]To use RepoQL with your agent:[/]");
        console.MarkupLine("[dim]1. Restart your agent application[/]");
        console.MarkupLine("[dim]2. RepoQL will be available as an MCP server[/]");
        console.MarkupLine("[dim]3. The agent can use 'query' and 'xray' tools to explore repositories[/]");
    }

    private async Task<List<AgentInfo>> DetectInstalledAgentsAsync()
    {
        var agents = new List<AgentInfo>();
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Claude Desktop - macOS
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var claudeDesktopConfig = Path.Combine(
                homeDir,
                "Library",
                "Application Support",
                "Claude",
                "claude_desktop_config.json");

            if (File.Exists(claudeDesktopConfig))
            {
                agents.Add(new AgentInfo
                {
                    Name = "Claude Desktop",
                    Type = AgentType.ClaudeDesktop,
                    ConfigPath = claudeDesktopConfig
                });
            }
        }

        // Claude Desktop - Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var claudeDesktopConfig = Path.Combine(appData, "Claude", "claude_desktop_config.json");

            if (File.Exists(claudeDesktopConfig))
            {
                agents.Add(new AgentInfo
                {
                    Name = "Claude Desktop",
                    Type = AgentType.ClaudeDesktop,
                    ConfigPath = claudeDesktopConfig
                });
            }
        }

        // Claude CLI - Linux/macOS
        var claudeCliConfig = Path.Combine(homeDir, ".config", "claude", ".mcp.json");
        if (File.Exists(claudeCliConfig))
        {
            agents.Add(new AgentInfo
            {
                Name = "Claude CLI",
                Type = AgentType.ClaudeCLI,
                ConfigPath = claudeCliConfig
            });
        }

        // Alternative Claude CLI location
        var altClaudeCliConfig = Path.Combine(homeDir, ".claude", ".mcp.json");
        if (File.Exists(altClaudeCliConfig) && !agents.Any(a => a.Type == AgentType.ClaudeCLI))
        {
            agents.Add(new AgentInfo
            {
                Name = "Claude CLI",
                Type = AgentType.ClaudeCLI,
                ConfigPath = altClaudeCliConfig
            });
        }

        // Codex - check common locations
        var codexConfig = Path.Combine(homeDir, ".mcp.json");
        if (File.Exists(codexConfig))
        {
            // Try to determine if this is a Codex config by checking the content
            var isCodex = await IsCodexConfigAsync(codexConfig);
            if (isCodex)
            {
                agents.Add(new AgentInfo
                {
                    Name = "Codex",
                    Type = AgentType.Codex,
                    ConfigPath = codexConfig
                });
            }
        }

        // Codex - XDG config location
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(homeDir, ".config");
        var codexXdgConfig = Path.Combine(xdgConfigHome, "codex", ".mcp.json");
        if (File.Exists(codexXdgConfig))
        {
            agents.Add(new AgentInfo
            {
                Name = "Codex",
                Type = AgentType.Codex,
                ConfigPath = codexXdgConfig
            });
        }

        return agents;
    }

    private async Task<bool> IsCodexConfigAsync(string configPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var doc = JsonDocument.Parse(json);

            // Check if the config contains codex-specific entries
            if (doc.RootElement.TryGetProperty("mcpServers", out var servers))
            {
                foreach (var server in servers.EnumerateObject())
                {
                    if (server.Value.TryGetProperty("command", out var cmd))
                    {
                        var command = cmd.GetString();
                        if (command?.Contains("codex", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task InstallToAgentAsync(AgentInfo agent, CancellationToken cancel)
    {
        await console.Status()
            .StartAsync($"Installing to {agent.Name}...", async ctx =>
            {
                try
                {
                    // Read existing config
                    JsonNode? config;
                    if (File.Exists(agent.ConfigPath))
                    {
                        var json = await File.ReadAllTextAsync(agent.ConfigPath, cancel);
                        config = JsonNode.Parse(json);
                    }
                    else
                    {
                        // Create new config
                        config = new JsonObject();
                        var directory = Path.GetDirectoryName(agent.ConfigPath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                    }

                    // Ensure mcpServers section exists
                    if (config["mcpServers"] is null)
                    {
                        config["mcpServers"] = new JsonObject();
                    }

                    var mcpServers = config["mcpServers"]!.AsObject();

                    // Check if repoql already exists
                    if (mcpServers.ContainsKey("repoql"))
                    {
                        console.MarkupLine($"[yellow]  RepoQL is already configured in {agent.Name}[/]");

                        var shouldUpdate = console.Confirm(
                            $"  Update the existing RepoQL configuration in {agent.Name}?",
                            defaultValue: true);

                        if (!shouldUpdate)
                        {
                            console.MarkupLine("[dim]  Skipped.[/]");
                            return;
                        }
                    }

                    // Add/update RepoQL configuration
                    var repoqlConfig = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = "repoql",
                        ["args"] = new JsonArray("mcp")
                    };

                    // Add REPOQL_CWD environment variable based on agent type
                    var workingDir = agent.WorkingDirectory ?? GetDefaultWorkingDirectory(agent.Type);
                    if (!string.IsNullOrEmpty(workingDir))
                    {
                        repoqlConfig["env"] = new JsonObject
                        {
                            ["REPOQL_CWD"] = workingDir
                        };
                    }

                    mcpServers["repoql"] = repoqlConfig;

                    // Write back to file
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    var updatedJson = config.ToJsonString(options);
                    await File.WriteAllTextAsync(agent.ConfigPath, updatedJson, cancel);

                    console.MarkupLine($"[green]  ✓ Installed to {agent.Name}[/]");
                    if (!string.IsNullOrEmpty(workingDir))
                    {
                        console.MarkupLine($"[dim]    Working directory: {workingDir}[/]");
                    }
                }
                catch (Exception ex)
                {
                    console.MarkupLine($"[red]  ✗ Failed to install to {agent.Name}: {ex.Message}[/]");
                }
            });
    }

    private string? GetDefaultWorkingDirectory(AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Codex => "{workspace}",
            AgentType.ClaudeCLI => "{workspace}",
            AgentType.ClaudeDesktop => null, // Must be set by user
            _ => null
        };
    }

    private record AgentInfo
    {
        public required string Name { get; init; }
        public required AgentType Type { get; init; }
        public required string ConfigPath { get; init; }
        public string? WorkingDirectory { get; set; }
    }

    private enum AgentType
    {
        ClaudeDesktop,
        ClaudeCLI,
        Codex
    }
}
