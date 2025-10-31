using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
            console.MarkupLine("Supported agents: Claude Desktop, Claude CLI, Claude Code, Codex");
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

        // Gather other agent configurations
        var candidateConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidatesFromDirectory(string? root, int maxDepth)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            foreach (var file in EnumerateMcpConfigFiles(root, maxDepth))
            {
                candidateConfigs.Add(file);
            }
        }

        AddCandidatesFromDirectory(homeDir, 1);
        AddCandidatesFromDirectory(Path.Combine(homeDir, ".config"), 2);
        AddCandidatesFromDirectory(Path.Combine(homeDir, ".claude"), 2);
        AddCandidatesFromDirectory(Path.Combine(homeDir, ".anthropic"), 2);
        AddCandidatesFromDirectory(Path.Combine(homeDir, ".local", "share"), 2);

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(homeDir, ".config");
        AddCandidatesFromDirectory(xdgConfigHome, 2);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            AddCandidatesFromDirectory(Path.Combine(appData, "Codex"), 3);
            AddCandidatesFromDirectory(Path.Combine(appData, "You"), 3);
            AddCandidatesFromDirectory(Path.Combine(appData, "Anthropic"), 3);
            AddCandidatesFromDirectory(Path.Combine(appData, "Claude"), 3);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var library = Path.Combine(homeDir, "Library", "Application Support");
            AddCandidatesFromDirectory(Path.Combine(library, "Codex"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "You"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "Anthropic"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "Claude"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "Claude Code"), 3);
        }

        foreach (var candidate in candidateConfigs)
        {
            await TryAddAgentFromConfigAsync(candidate, agents).ConfigureAwait(false);
        }

        return agents;
    }

    private static IEnumerable<string> EnumerateMcpConfigFiles(string root, int maxDepth)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (!Directory.Exists(current))
                continue;

            IEnumerable<string> EnumerateFiles(string pattern)
            {
                try
                {
                    return Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }

            foreach (var file in EnumerateFiles("*.mcp.json"))
            {
                yield return file;
            }

            foreach (var file in EnumerateFiles("config.toml"))
            {
                var normalized = file.Replace('\\', '/').ToLowerInvariant();
                if (normalized.Contains("/.codex/") || normalized.Contains("/codex/"))
                    yield return file;
            }

            if (depth >= maxDepth)
                continue;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(current))
                {
                    stack.Push((dir, depth + 1));
                }
            }
            catch
            {
            }
        }
    }

    private async Task TryAddAgentFromConfigAsync(string configPath, List<AgentInfo> agents)
    {
        if (!File.Exists(configPath))
            return;

        AgentType? agentType;
        var extension = Path.GetExtension(configPath);
        if (string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase))
        {
            agentType = DetermineAgentTypeFromTomlConfig(configPath);
        }
        else
        {
            agentType = await DetermineAgentTypeFromJsonConfigAsync(configPath).ConfigureAwait(false);
        }

        if (agentType is null)
            return;

        if (agents.Any(a => string.Equals(a.ConfigPath, configPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var displayName = agentType switch
        {
            AgentType.ClaudeCLI => "Claude CLI",
            AgentType.ClaudeCode => "Claude Code",
            AgentType.Codex => "Codex",
            _ => agentType.ToString()
        };

        agents.Add(new AgentInfo
        {
            Name = displayName,
            Type = agentType.Value,
            ConfigPath = configPath
        });
    }

    private async Task<AgentType?> DetermineAgentTypeFromJsonConfigAsync(string configPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("mcpServers", out var servers))
            {
                foreach (var server in servers.EnumerateObject())
                {
                    if (string.Equals(server.Name, "repoql", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (server.Value.TryGetProperty("command", out var cmd))
                    {
                        var command = cmd.ValueKind == JsonValueKind.String ? cmd.GetString() : null;
                        if (TryClassifyAgent(command, out var agentFromCommand))
                            return agentFromCommand;
                    }

                    if (server.Value.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                    {
                        var joined = string.Join(' ',
                            args.EnumerateArray()
                                .Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null)
                                .Where(s => !string.IsNullOrWhiteSpace(s))!);
                        if (TryClassifyAgent(joined, out var agentFromArgs))
                            return agentFromArgs;
                    }

                    if (TryClassifyAgent(server.Name, out var agentFromName))
                        return agentFromName;
                }
            }

            if (TryClassifyAgent(configPath, out var agentFromPath))
                return agentFromPath;
        }
        catch
        {
        }

        return null;
    }

    private static AgentType? DetermineAgentTypeFromTomlConfig(string configPath)
    {
        var normalized = configPath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Contains("/.codex/config.toml") ||
            normalized.EndsWith("/codex/config.toml") ||
            normalized.Contains("/application support/codex/config.toml"))
            return AgentType.Codex;

        try
        {
            var content = File.ReadAllText(configPath);
            if (content.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase) >= 0)
                return AgentType.ClaudeCode;
            if (content.IndexOf("[mcp_servers", StringComparison.OrdinalIgnoreCase) >= 0 ||
                content.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0)
                return AgentType.Codex;
        }
        catch
        {
        }

        return null;
    }

    internal static bool TryClassifyAgent(string? text, out AgentType agentType)
    {
        agentType = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.ToLowerInvariant();

        bool Contains(params string[] tokens) => tokens.Any(t => normalized.Contains(t, StringComparison.Ordinal));

        if (Contains("claude-code", "claudecode"))
        {
            agentType = AgentType.ClaudeCode;
            return true;
        }

        if ((Contains("anthropic", "claude") && !Contains("desktop")) || Contains("claude_cli"))
        {
            agentType = AgentType.ClaudeCLI;
            return true;
        }

        if (Contains("codex") || Contains("you-cli", "you.com", "you_cli", "youapp", "you-app"))
        {
            agentType = AgentType.Codex;
            return true;
        }

        return false;
    }

    private async Task InstallToAgentAsync(AgentInfo agent, CancellationToken cancel)
    {
        var workingDir = agent.WorkingDirectory ?? GetDefaultWorkingDirectory(agent.Type);

        if (TryGetCliDefinition(agent.Type, out var cliDefinition))
        {
            var cliResult = await InstallUsingCliAsync(agent, cliDefinition, workingDir, cancel).ConfigureAwait(false);
            if (cliResult is { Success: true })
            {
                console.MarkupLine($"[green]  ✓ Installed to {agent.Name}[/]");
                if (!string.IsNullOrEmpty(workingDir))
                {
                    console.MarkupLine($"[dim]    Working directory: {workingDir}[/]");
                }
                return;
            }

            var message = !string.IsNullOrWhiteSpace(cliResult.StandardError)
                ? cliResult.StandardError
                : (!string.IsNullOrWhiteSpace(cliResult.ErrorMessage) ? cliResult.ErrorMessage : $"{cliDefinition.ExecutableName} command failed.");

            if (agent.Type == AgentType.Codex)
            {
                console.MarkupLine($"[red]  ✗ Failed to install to {agent.Name}: {message.Trim()}[/]");
                console.MarkupLine("[dim]  Ensure the Codex CLI is installed and on PATH, then rerun 'repoql install'.[/]");
                return;
            }

            console.MarkupLine($"[yellow]  CLI install for {agent.Name} failed: {message.Trim()}[/]");
            console.MarkupLine("[dim]  Falling back to updating the configuration file directly.[/]");
        }

        await InstallViaConfigFileAsync(agent, workingDir, cancel).ConfigureAwait(false);
    }

    private async Task<CliCommandResult> InstallUsingCliAsync(AgentInfo agent, AgentCliDefinition definition, string? workingDir, CancellationToken cancel)
    {
        CliCommandResult? addResult = null;

        await console.Status()
            .StartAsync($"Installing to {agent.Name} via {definition.ExecutableName}...", async _ =>
            {
                var removeArgs = definition.BuildRemoveArguments();
                await RunCliCommandAsync(definition.ExecutableName, removeArgs, cancel, ignoreErrors: true).ConfigureAwait(false);

                var addArgs = definition.BuildAddArguments(workingDir);
                addResult = await RunCliCommandAsync(definition.ExecutableName, addArgs, cancel).ConfigureAwait(false);
            });

        return addResult ?? new CliCommandResult(false, -1, string.Empty, string.Empty, $"Failed to execute {definition.ExecutableName} CLI.");
    }

    private async Task InstallViaConfigFileAsync(AgentInfo agent, string? workingDir, CancellationToken cancel)
    {
        await console.Status()
            .StartAsync($"Installing to {agent.Name}...", async _ =>
            {
                try
                {
                    JsonNode? config;
                    if (File.Exists(agent.ConfigPath))
                    {
                        var json = await File.ReadAllTextAsync(agent.ConfigPath, cancel).ConfigureAwait(false);
                        config = JsonNode.Parse(json);
                    }
                    else
                    {
                        config = new JsonObject();
                        var directory = Path.GetDirectoryName(agent.ConfigPath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                    }

                    if (config["mcpServers"] is null)
                    {
                        config["mcpServers"] = new JsonObject();
                    }

                    var mcpServers = config["mcpServers"]!.AsObject();

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

                    var repoqlConfig = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = "repoql",
                        ["args"] = new JsonArray("mcp")
                    };

                    if (!string.IsNullOrEmpty(workingDir))
                    {
                        repoqlConfig["env"] = new JsonObject
                        {
                            ["REPOQL_CWD"] = workingDir
                        };
                    }

                    mcpServers["repoql"] = repoqlConfig;

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    var updatedJson = config.ToJsonString(options);
                    await File.WriteAllTextAsync(agent.ConfigPath, updatedJson, cancel).ConfigureAwait(false);

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

    private static bool TryGetCliDefinition(AgentType agentType, out AgentCliDefinition definition)
    {
        if (CliDefinitions.TryGetValue(agentType, out var value))
        {
            definition = value;
            return true;
        }

        definition = null!;
        return false;
    }

    internal static readonly IReadOnlyDictionary<AgentType, AgentCliDefinition> CliDefinitions =
        new Dictionary<AgentType, AgentCliDefinition>
        {
            [AgentType.Codex] = new AgentCliDefinition(
                ExecutableName: "codex",
                BuildAddArguments: workingDir =>
                {
                    var args = new List<string> { "mcp", "add" };
                    if (!string.IsNullOrWhiteSpace(workingDir))
                    {
                        args.Add("--env");
                        args.Add($"REPOQL_CWD={workingDir}");
                    }

                    args.Add("repoql"); // command
                    args.Add("mcp");    // command argument
                    args.Add("repoql"); // name
                    return args;
                },
                BuildRemoveArguments: () => new[] { "mcp", "remove", "repoql" }),

            [AgentType.ClaudeCLI] = new AgentCliDefinition(
                ExecutableName: "claude",
                BuildAddArguments: workingDir =>
                {
                    var args = new List<string>
                    {
                        "mcp",
                        "add",
                        "--scope",
                        "user",
                        "--transport",
                        "stdio"
                    };

                    if (!string.IsNullOrWhiteSpace(workingDir))
                    {
                        args.Add("--env");
                        args.Add($"REPOQL_CWD={workingDir}");
                    }

                    args.Add("repoql");
                    args.Add("--");
                    args.Add("repoql");
                    args.Add("mcp");
                    return args;
                },
                BuildRemoveArguments: () => new[] { "mcp", "remove", "--scope", "user", "repoql" }),

            [AgentType.ClaudeCode] = new AgentCliDefinition(
                ExecutableName: "claude",
                BuildAddArguments: workingDir =>
                {
                    var args = new List<string>
                    {
                        "mcp",
                        "add",
                        "--scope",
                        "user",
                        "--transport",
                        "stdio"
                    };

                    if (!string.IsNullOrWhiteSpace(workingDir))
                    {
                        args.Add("--env");
                        args.Add($"REPOQL_CWD={workingDir}");
                    }

                    args.Add("repoql");
                    args.Add("--");
                    args.Add("repoql");
                    args.Add("mcp");
                    return args;
                },
                BuildRemoveArguments: () => new[] { "mcp", "remove", "--scope", "user", "repoql" })
        };

    private async Task<CliCommandResult> RunCliCommandAsync(string command, IEnumerable<string> arguments, CancellationToken cancel, bool ignoreErrors = false)
    {
        var executable = LocateExecutable(command);
        if (string.IsNullOrEmpty(executable))
        {
            var message = $"Unable to locate '{command}' on PATH.";
            return new CliCommandResult(ignoreErrors, -1, string.Empty, string.Empty, ignoreErrors ? null : message);
        }

        var psi = CreateProcessStartInfo(executable, arguments);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new CliCommandResult(ignoreErrors, -1, string.Empty, string.Empty, ignoreErrors ? null : $"Failed to start '{command}'.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            var success = process.ExitCode == 0 || ignoreErrors;
            var errorMessage = success ? null : $"{command} exited with code {process.ExitCode}";
            return new CliCommandResult(success, process.ExitCode, stdout, stderr, errorMessage);
        }
        catch (Exception ex)
        {
            return new CliCommandResult(ignoreErrors, -1, string.Empty, string.Empty, ignoreErrors ? null : ex.Message);
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string executablePath, IEnumerable<string> arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            var extension = Path.GetExtension(executablePath);
            if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                var commandLine = BuildWindowsCommandLine(executablePath, arguments);
                var psiCmd = new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psiCmd.ArgumentList.Add("/C");
                psiCmd.ArgumentList.Add(commandLine);
                return psiCmd;
            }
        }

        var psi = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static string BuildWindowsCommandLine(string executable, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteForShell(executable));
        foreach (var arg in arguments)
        {
            builder.Append(' ');
            builder.Append(QuoteForShell(arg));
        }
        return builder.ToString();
    }

    private static string QuoteForShell(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        var needsQuotes = value.Any(char.IsWhiteSpace) || value.Contains('"');
        if (!needsQuotes)
            return value;

        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string? LocateExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return TryResolveWithExtensions(command);

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var candidate = TryResolveWithExtensions(Path.Combine(trimmed, command));
            if (candidate is not null)
                return candidate;
        }

        return null;
    }

    private static string? TryResolveWithExtensions(string path)
    {
        var executableExtensions = GetExecutableExtensions().ToArray();

        if (File.Exists(path))
        {
            if (!OperatingSystem.IsWindows() || HasExecutableExtension(Path.GetExtension(path), executableExtensions))
                return path;
        }

        foreach (var ext in executableExtensions)
        {
            var candidate = path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? path : path + ext;
            if (File.Exists(candidate))
                return candidate;
        }

        return File.Exists(path) ? path : null;
    }

    private static IEnumerable<string> GetExecutableExtensions()
    {
        if (!OperatingSystem.IsWindows())
            return new[] { string.Empty };

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrEmpty(pathext))
            return new[] { ".exe", ".cmd", ".bat", ".com" };

        return pathext.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(ext => ext.StartsWith('.') ? ext : "." + ext)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasExecutableExtension(string? extension, string[] executableExtensions)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        if (string.IsNullOrEmpty(extension))
            return false;

        return executableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record AgentCliDefinition(
        string ExecutableName,
        Func<string?, IReadOnlyList<string>> BuildAddArguments,
        Func<IReadOnlyList<string>> BuildRemoveArguments);

    private sealed record CliCommandResult(bool Success, int ExitCode, string StandardOutput, string StandardError, string? ErrorMessage);

    private string? GetDefaultWorkingDirectory(AgentType agentType) =>
        agentType switch
        {
            AgentType.Codex => "{workspace}",
            _ => null
        };

    private record AgentInfo
    {
        public required string Name { get; init; }
        public required AgentType Type { get; init; }
        public required string ConfigPath { get; init; }
        public string? WorkingDirectory { get; set; }
    }

    internal enum AgentType
    {
        ClaudeDesktop,
        ClaudeCLI,
        ClaudeCode,
        Codex
    }
}
