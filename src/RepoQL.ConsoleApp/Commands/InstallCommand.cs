using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConsoleAppFramework;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Commands;

[RegisterCommands]
internal class InstallCommand(IAnsiConsole console)
{
    private readonly Dictionary<string, ConfigStatus> configStatusCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DefaultServerPropertyPriority = new[] { "mcpServers", "servers" };
    private static readonly string[] CopilotServerPropertyPriority = new[] { "servers", "mcpServers" };
    private static readonly string[] KnownServerPropertyNames = new[] { "mcpServers", "servers" };
    private static readonly Lazy<string> RepoqlCommandLazy = new(ResolveRepoqlCommand);
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
            console.MarkupLine("Supported agents: Claude Desktop, Claude Code, Codex, GitHub Copilot (VS Code), GitHub Copilot (Visual Studio), GitHub Copilot (JetBrains)");
            console.WriteLine();
            console.MarkupLine("You can manually add RepoQL to your agent's MCP configuration:");
            console.WriteLine();
            console.MarkupLine("[dim]{[/]");
            var repoqlCommandDisplay = EscapeMarkup(GetRepoqlCommand());
            console.MarkupLine("[dim]  \"mcpServers\": {[/]");
            console.MarkupLine("[dim]    \"repoql\": {[/]");
            console.MarkupLine("[dim]      \"type\": \"stdio\",[/]");
            console.MarkupLine($"[dim]      \"command\": \"{repoqlCommandDisplay}\",[/]");
            console.MarkupLine("[dim]      \"args\": [\"mcp\"][/]");
            console.MarkupLine("[dim]    }[/]");
            console.MarkupLine("[dim]  }[/]");
            console.MarkupLine("[dim]}[/]");
            console.WriteLine();
            console.MarkupLine("[dim]Tip: RepoQL indexes whichever repository the agent runs in unless you override REPOQL_CWD (required for Claude Desktop).[/]");
            return;
        }

        // Show detected agents
        var agentEntries = BuildAgentEntries(agents);
        console.MarkupLine($"[green]Found {agentEntries.Count} agent(s):[/]");
        var labeledEntries = agentEntries
            .Select(entry =>
            {
                var choiceLabel = entry.Agents.Count == 1
                    ? $"{entry.Label} ({DescribeAgentLocation(entry.Agents[0])})"
                    : entry.Label;
                var summary = entry.Agents.Count == 1
                    ? DescribeAgentLocation(entry.Agents[0])
                    : string.Join(", ", entry.Agents.Select(GetCopilotDisplayName));
                return new { Entry = entry, ChoiceLabel = choiceLabel, Summary = summary };
            })
            .ToList();

        foreach (var labeled in labeledEntries)
        {
            console.MarkupLine($"  • [cyan]{EscapeMarkup(labeled.Entry.Label)}[/] - {EscapeMarkup(labeled.Summary)}");
        }
        console.WriteLine();
        console.MarkupLine("[dim]Default behavior: RepoQL indexes the repository that each agent is already working in. Specify a path only when the agent cannot provide one (e.g., Claude Desktop).[/]");
        console.WriteLine();

        // Interactive selection menu
        var choices = labeledEntries
            .Select(a => a.ChoiceLabel)
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
            agentsToUpdate = agentEntries.SelectMany(e => e.Agents).ToList();
        }
        else
        {
            var selectedEntry = labeledEntries.First(a => a.ChoiceLabel == selection).Entry;
            var selectedAgent = SelectAgentFromEntry(selectedEntry);
            agentsToUpdate = selectedAgent is null ? [] : [selectedAgent];
        }

        var installContexts = new List<AgentInstallContext>();
        foreach (var agent in agentsToUpdate)
        {
            var context = PromptForTargetScope(agent);
            if (context is not null)
            {
                installContexts.Add(context);
            }
        }

        if (installContexts.Count == 0)
        {
            console.MarkupLine("[yellow]No installation targets selected.[/]");
            return;
        }

        console.WriteLine();
        foreach (var context in installContexts)
        {
            await InstallToAgentAsync(context, cancel);
        }

        console.WriteLine();
        console.MarkupLine("[bold green]✓ Installation complete![/]");
        console.WriteLine();
        console.MarkupLine("[dim]To use RepoQL with your agent:[/]");
        console.MarkupLine("[dim]1. Restart your agent application[/]");
        console.MarkupLine("[dim]2. RepoQL will be available as an MCP server[/]");
        console.MarkupLine("[dim]3. The agent can use 'query' and 'xray' tools to explore repositories[/]");
    }

    private static void RegisterAgent(List<AgentInfo> agents, AgentType type, string name, string? configPath, string? executablePath = null)
    {
        var existing = FindAgent(agents, type, configPath);
        if (existing is not null)
        {
            if (string.IsNullOrEmpty(existing.ExecutablePath) && !string.IsNullOrEmpty(executablePath))
            {
                existing.ExecutablePath = executablePath;
            }
            return;
        }

        agents.Add(new AgentInfo
        {
            Name = name,
            Type = type,
            ConfigPath = configPath,
            ExecutablePath = executablePath
        });
    }

    private static AgentInfo? FindAgent(List<AgentInfo> agents, AgentType type, string? configPath)
    {
        var exact = agents.FirstOrDefault(a => a.Type == type && PathsEqual(a.ConfigPath, configPath));
        if (exact is not null)
        {
            return exact;
        }

        return configPath is null
            ? agents.FirstOrDefault(a => a.Type == type)
            : null;
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private async Task<List<AgentInfo>> DetectInstalledAgentsAsync()
    {
        var agents = new List<AgentInfo>();
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AddClaudeDesktopCandidates(agents, homeDir);

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
            AddCandidatesFromDirectory(Path.Combine(appData, "Code"), 3);
            AddCandidatesFromDirectory(Path.Combine(appData, "Code - Insiders"), 3);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var library = Path.Combine(homeDir, "Library", "Application Support");
            AddCandidatesFromDirectory(Path.Combine(library, "Codex"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "You"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "Anthropic"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "Claude"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "Claude Code"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "Code"), 3);
            AddCandidatesFromDirectory(Path.Combine(library, "Code - Insiders"), 3);
        }

        foreach (var candidate in candidateConfigs)
        {
            await TryAddAgentFromConfigAsync(candidate, agents).ConfigureAwait(false);
        }

        var executableMap = DetectAgentExecutables();
        foreach (var agent in agents)
        {
            if (executableMap.TryGetValue(agent.Type, out var executablePath))
            {
                agent.ExecutablePath = executablePath;
            }
        }

        AddExecutableOnlyAgents(agents, executableMap);
        AddVisualStudioCopilotAgent(agents);
        AddJetBrainsCopilotAgent(agents);

        return agents;
    }


    private static void AddClaudeDesktopCandidates(List<AgentInfo> agents, string homeDir)
    {
        var paths = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            paths.Add(Path.Combine(homeDir, "Library", "Application Support", "Claude", "claude_desktop_config.json"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            paths.Add(Path.Combine(appData, "Claude", "claude_desktop_config.json"));
        }

        foreach (var path in paths.Where(File.Exists))
        {
            RegisterAgent(agents, AgentType.ClaudeDesktop, GetAgentDisplayName(AgentType.ClaudeDesktop), path);
        }
    }

    private static IReadOnlyDictionary<AgentType, string> DetectAgentExecutables()
    {
        var map = new Dictionary<AgentType, string>();

        var claudePath = DetectClaudeExecutable();
        if (!string.IsNullOrEmpty(claudePath))
        {
            map[AgentType.ClaudeCode] = claudePath;
        }

        var codexPath = DetectCodexExecutable();
        if (!string.IsNullOrEmpty(codexPath))
        {
            map[AgentType.Codex] = codexPath;
        }

        var copilotPath = DetectVsCodeCopilotExecutable();
        if (!string.IsNullOrEmpty(copilotPath))
        {
            map[AgentType.GitHubCopilotVSCode] = copilotPath;
        }

        return map;
    }

    private static void AddExecutableOnlyAgents(List<AgentInfo> agents, IReadOnlyDictionary<AgentType, string> executableMap)
    {
        foreach (var kvp in executableMap)
        {
            var configPath = kvp.Key switch
            {
                AgentType.Codex => GetDefaultCodexConfigPath(),
                AgentType.GitHubCopilotVSCode => GetDefaultVsCodeCopilotConfigPath(),
                _ => null
            };

            RegisterAgent(agents, kvp.Key, GetAgentDisplayName(kvp.Key), configPath, kvp.Value);
        }
    }

    private static void AddVisualStudioCopilotAgent(List<AgentInfo> agents)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (agents.Any(a => a.Type == AgentType.GitHubCopilotVisualStudio))
            return;

        var configCandidates = GetVisualStudioCopilotConfigCandidates().ToList();
        if (configCandidates.Count == 0)
            return;

        var existing = configCandidates.FirstOrDefault(c => File.Exists(c.Path));
        if (existing is null && !HasVisualStudioCopilotHints())
            return;

        var target = existing ?? configCandidates.First();

        RegisterAgent(agents, AgentType.GitHubCopilotVisualStudio, GetAgentDisplayName(AgentType.GitHubCopilotVisualStudio), target.Path);
    }

    private static void AddJetBrainsCopilotAgent(List<AgentInfo> agents)
    {
        if (agents.Any(a => a.Type == AgentType.GitHubCopilotJetBrains))
            return;

        var configPath = GetJetBrainsCopilotConfigPath();
        if (string.IsNullOrEmpty(configPath))
            return;

        var resolvedConfigPath = configPath!;

        if (!HasJetBrainsCopilotHints(resolvedConfigPath) && !File.Exists(resolvedConfigPath))
            return;

        RegisterAgent(agents, AgentType.GitHubCopilotJetBrains, GetAgentDisplayName(AgentType.GitHubCopilotJetBrains), resolvedConfigPath);
    }

    private static string? DetectClaudeExecutable()
    {
        var candidates = new List<string?>();
        candidates.Add(LocateExecutable("claude"));

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.Add(CombineIfNotNull(appData, "npm", "claude"));
            candidates.Add(CombineIfNotNull(appData, "npm", "claude.cmd"));
            candidates.Add(CombineIfNotNull(appData, "npm", "claude.ps1"));

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(CombineIfNotNull(localAppData, "Programs", "Claude", "claude.exe"));
            candidates.Add(CombineIfNotNull(localAppData, "Programs", "Claude Code", "claude.exe"));
        }
        else
        {
            candidates.Add("/usr/local/bin/claude");
            candidates.Add("/usr/bin/claude");
            candidates.Add(CombineIfNotNull(GetUserHomeDirectory(), ".local", "bin", "claude"));
            candidates.Add("/opt/homebrew/bin/claude");
        }

        return FirstExistingPath(candidates);
    }

    private static string? DetectCodexExecutable()
    {
        var candidates = new List<string?>();
        candidates.Add(LocateExecutable("codex"));

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.Add(CombineIfNotNull(appData, "npm", "codex"));
            candidates.Add(CombineIfNotNull(appData, "npm", "codex.cmd"));
            candidates.Add(CombineIfNotNull(appData, "npm", "codex.ps1"));

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(CombineIfNotNull(localAppData, "Programs", "Codex", "codex.exe"));
        }
        else
        {
            candidates.Add("/usr/local/bin/codex");
            candidates.Add("/usr/bin/codex");
            candidates.Add(CombineIfNotNull(GetUserHomeDirectory(), ".local", "bin", "codex"));
            candidates.Add("/opt/homebrew/bin/codex");
        }

        return FirstExistingPath(candidates);
    }

    private static string? DetectVsCodeCopilotExecutable()
    {
        if (!IsVsCodeCopilotInstalled())
            return null;

        return FindVsCodeCliExecutable();
    }

    private static bool IsVsCodeCopilotInstalled()
    {
        foreach (var root in GetVsCodeExtensionRoots())
        {
            if (!Directory.Exists(root))
                continue;

            var hasCopilot = Directory.EnumerateDirectories(root, "github.copilot-*").Any() ||
                             Directory.EnumerateDirectories(root, "github.copilot-chat-*").Any();
            if (hasCopilot)
                return true;
        }

        return false;
    }

    private static string? FindVsCodeCliExecutable()
    {
        var candidates = new List<string?>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(LocateExecutable("code.cmd"));
            candidates.Add(LocateExecutable("code"));

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(CombineIfNotNull(localAppData, "Programs", "Microsoft VS Code", "bin", "code.cmd"));
            candidates.Add(CombineIfNotNull(localAppData, "Programs", "Microsoft VS Code Insiders", "bin", "code-insiders.cmd"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(LocateExecutable("code"));
            candidates.Add("/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code");
            candidates.Add("/Applications/Visual Studio Code - Insiders.app/Contents/Resources/app/bin/code");
        }
        else
        {
            candidates.Add(LocateExecutable("code"));
            candidates.Add("/usr/bin/code");
            candidates.Add("/var/lib/snapd/snap/bin/code");
            candidates.Add("/usr/bin/code-insiders");
        }

        return FirstExistingPath(candidates);
    }

    private static IEnumerable<string> GetVsCodeExtensionRoots()
    {
        var home = GetUserHomeDirectory();
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".vscode", "extensions");
            yield return Path.Combine(home, ".vscode-insiders", "extensions");
            yield return Path.Combine(home, ".vscode-oss", "extensions");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            yield return Path.Combine(appData, "Code", "extensions");
            yield return Path.Combine(appData, "Code - Insiders", "extensions");
        }
    }

    private static string? GetDefaultCodexConfigPath()
    {
        var home = GetUserHomeDirectory();
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".codex", "config.toml");
    }

    private static string? GetDefaultVsCodeCopilotConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return CombineIfNotNull(appData, "Code", "User", "mcp.json");
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = GetUserHomeDirectory();
            return string.IsNullOrEmpty(home)
                ? null
                : Path.Combine(home, "Library", "Application Support", "Code", "User", "mcp.json");
        }

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdgConfig))
        {
            var home = GetUserHomeDirectory();
            xdgConfig = string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config");
        }

        return CombineIfNotNull(xdgConfig, "Code", "User", "mcp.json");
    }

    private static IEnumerable<ScopeCandidate> GetVisualStudioCopilotConfigCandidates()
    {
        var candidates = new List<ScopeCandidate>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            candidates.Add(new ScopeCandidate("User profile", Path.Combine(userProfile, ".mcp.json")));
        }

        var solutionDir = FindVisualStudioSolutionDirectory();
        if (!string.IsNullOrEmpty(solutionDir))
        {
            candidates.Add(new ScopeCandidate("Solution .vs directory", Path.Combine(solutionDir, ".vs", "mcp.json")));
            candidates.Add(new ScopeCandidate("Solution root (.mcp.json)", Path.Combine(solutionDir, ".mcp.json")));
            candidates.Add(new ScopeCandidate("Solution .vscode directory", Path.Combine(solutionDir, ".vscode", "mcp.json")));
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private static string? FindVisualStudioSolutionDirectory()
    {
        try
        {
            var current = Environment.CurrentDirectory;
            var sln = Directory.EnumerateFiles(current, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            return sln is null ? null : Path.GetDirectoryName(Path.GetFullPath(sln));
        }
        catch
        {
            return null;
        }
    }

    private static bool HasVisualStudioCopilotHints()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            if (Directory.Exists(Path.Combine(localAppData, "Microsoft", "VisualStudio", "Copilot")) ||
                Directory.Exists(Path.Combine(localAppData, "github-copilot")))
            {
                return true;
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86) &&
            Directory.Exists(Path.Combine(programFilesX86, "Microsoft Visual Studio")))
        {
            return true;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles) &&
            Directory.Exists(Path.Combine(programFiles, "Microsoft Visual Studio")))
        {
            return true;
        }

        return false;
    }

    private static string? GetJetBrainsCopilotConfigPath()
    {
        string? root;
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
                return null;
            root = Path.Combine(localAppData, "github-copilot", "intellij");
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(xdg))
            {
                var home = GetUserHomeDirectory();
                if (string.IsNullOrEmpty(home))
                    return null;
                xdg = Path.Combine(home, ".config");
            }

            root = Path.Combine(xdg, "github-copilot", "intellij");
        }

        return Path.Combine(root, "mcp.json");
    }

    private static bool HasJetBrainsCopilotHints(string configPath)
    {
        var intellijDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(intellijDir) && Directory.Exists(intellijDir))
            return true;

        var copilotDir = string.IsNullOrEmpty(intellijDir) ? null : Path.GetDirectoryName(intellijDir);
        if (!string.IsNullOrEmpty(copilotDir) && Directory.Exists(copilotDir))
            return true;

        return false;
    }

    private static string? FirstExistingPath(IEnumerable<string?> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string FormatScopeLabel(string baseLabel, string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return baseLabel;

        var status = GetConfigStatus(configPath) switch
        {
            ConfigStatus.Configured => "(installed)",
            ConfigStatus.Exists => "(existing)",
            _ => "(new)"
        };

        return $"{baseLabel} {status}";
    }

    private static bool IsCopilotType(AgentType type) =>
        type is AgentType.GitHubCopilotVSCode or AgentType.GitHubCopilotVisualStudio or AgentType.GitHubCopilotJetBrains;

    private static string GetCopilotDisplayName(AgentInfo agent)
    {
        const string prefix = "GitHub Copilot";
        var name = agent.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? agent.Name.Substring(prefix.Length)
            : agent.Name;

        var trimmed = name.Trim();
        if (trimmed.StartsWith("(") && trimmed.EndsWith(")") && trimmed.Length > 2)
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
        }

        trimmed = trimmed.TrimStart('-', '–', ':').Trim();
        return string.IsNullOrEmpty(trimmed) ? agent.Name : trimmed;
    }

    private static string EscapeMarkup(string? value) =>
        value is null ? string.Empty : Markup.Escape(value);

    private static async Task<JsonObject> LoadOrCreateConfigAsync(string path, CancellationToken cancel)
    {
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path, cancel).ConfigureAwait(false);

            var nodeOptions = new JsonNodeOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var documentOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            if (JsonNode.Parse(json, nodeOptions, documentOptions) is JsonObject existing)
            {
                return existing;
            }
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new JsonObject();
    }

    private static IReadOnlyList<string> GetServerPropertyPriority(AgentType type) =>
        type is AgentType.GitHubCopilotVSCode or AgentType.GitHubCopilotVisualStudio
            ? CopilotServerPropertyPriority
            : DefaultServerPropertyPriority;

    private static IEnumerable<string> GetServerPropertySearchOrder(AgentType type) =>
        GetServerPropertyPriority(type)
            .Concat(KnownServerPropertyNames)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static JsonObject GetOrCreateServerCollection(JsonObject config, string propertyName)
    {
        if (config.TryGetPropertyValue(propertyName, out var node) && node is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        config[propertyName] = created;
        return created;
    }

    private static RepoqlLocation? FindRepoqlLocation(JsonObject config, IEnumerable<string> propertyNames)
    {
        foreach (var property in propertyNames)
        {
            if (!config.TryGetPropertyValue(property, out var node) || node is not JsonObject servers)
                continue;

            if (servers.TryGetPropertyValue("repoql", out var definition) && definition is not null)
            {
                return new RepoqlLocation(property, servers);
            }
        }

        return null;
    }

    private ConfigStatus GetConfigStatus(string path)
    {
        if (configStatusCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        ConfigStatus status;
        if (!File.Exists(path))
        {
            status = ConfigStatus.Missing;
        }
        else if (IsRepoqlConfigured(path))
        {
            status = ConfigStatus.Configured;
        }
        else
        {
            status = ConfigStatus.Exists;
        }

        configStatusCache[path] = status;
        return status;
    }

    private static bool IsRepoqlConfigured(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (JsonNode.Parse(json) is JsonObject obj)
            {
                return FindRepoqlLocation(obj, KnownServerPropertyNames) is not null;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? CombineIfNotNull(string? root, params string[] segments)
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

    private static string? GetUserHomeDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? TryGetCurrentRepositoryPath()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeAgentLocation(AgentInfo agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.ConfigPath))
        {
            return agent.ConfigPath;
        }

        if (!string.IsNullOrWhiteSpace(agent.ExecutablePath))
        {
            return $"CLI: {agent.ExecutablePath}";
        }

        return "Unknown location";
    }

    private static string GetAgentDisplayName(AgentType agentType) =>
        agentType switch
        {
            AgentType.ClaudeDesktop => "Claude Desktop",
            AgentType.ClaudeCode => "Claude Code",
            AgentType.Codex => "Codex",
            AgentType.GitHubCopilotVSCode => "GitHub Copilot (VS Code)",
            AgentType.GitHubCopilotVisualStudio => "GitHub Copilot (Visual Studio)",
            AgentType.GitHubCopilotJetBrains => "GitHub Copilot (JetBrains)",
            _ => agentType.ToString()
        };

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

            foreach (var file in EnumerateFiles("mcp.json"))
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

        AgentType? agentType = null;
        var normalizedPath = configPath.Replace('\\', '/').ToLowerInvariant();
        var fileName = Path.GetFileName(configPath);
        if (IsVsCodeConfigPath(normalizedPath, fileName))
        {
            agentType = AgentType.GitHubCopilotVSCode;
        }
        else if (IsVisualStudioConfigPath(normalizedPath, fileName))
        {
            agentType = AgentType.GitHubCopilotVisualStudio;
        }
        else if (IsJetBrainsConfigPath(normalizedPath))
        {
            agentType = AgentType.GitHubCopilotJetBrains;
        }

        var extension = Path.GetExtension(configPath);
        if (agentType is null && string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase))
        {
            agentType = DetermineAgentTypeFromTomlConfig(configPath);
        }
        else if (agentType is null)
        {
            agentType = await DetermineAgentTypeFromJsonConfigAsync(configPath).ConfigureAwait(false);
        }

        if (agentType is null)
            return;

        if (agents.Any(a => string.Equals(a.ConfigPath, configPath, StringComparison.OrdinalIgnoreCase)))
            return;

        RegisterAgent(agents, agentType.Value, GetAgentDisplayName(agentType.Value), configPath);
    }

    private async Task<AgentType?> DetermineAgentTypeFromJsonConfigAsync(string configPath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);

            foreach (var server in EnumerateJsonServerDefinitions(doc.RootElement))
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

            if (TryClassifyAgent(configPath, out var agentFromPath))
                return agentFromPath;
        }
        catch
        {
        }

        return null;
    }

    private static IEnumerable<JsonProperty> EnumerateJsonServerDefinitions(JsonElement root)
    {
        foreach (var property in KnownServerPropertyNames)
        {
            if (!root.TryGetProperty(property, out var servers) || servers.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var server in servers.EnumerateObject())
            {
                yield return server;
            }
        }
    }

    private static bool IsVsCodeConfigPath(string normalizedPath, string fileName) =>
        string.Equals(fileName, "mcp.json", StringComparison.OrdinalIgnoreCase) &&
        (normalizedPath.Contains("/.vscode/") ||
         normalizedPath.Contains("/code/user/") ||
         normalizedPath.Contains("/code - insiders/user/"));

    private static bool IsVisualStudioConfigPath(string normalizedPath, string fileName) =>
        normalizedPath.Contains("/.vs/") ||
        (string.Equals(fileName, ".mcp.json", StringComparison.OrdinalIgnoreCase) &&
         !normalizedPath.Contains("/.vscode/"));

    private static bool IsJetBrainsConfigPath(string normalizedPath) =>
        normalizedPath.Contains("/github-copilot/intellij/");

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
            agentType = AgentType.ClaudeCode;
            return true;
        }

        if (Contains("codex") || Contains("you-cli", "you.com", "you_cli", "youapp", "you-app"))
        {
            agentType = AgentType.Codex;
            return true;
        }

        if (Contains("copilot"))
        {
            if (Contains("visualstudio") || Contains("vs"))
            {
                agentType = AgentType.GitHubCopilotVisualStudio;
                return true;
            }

            if (Contains("intellij") || Contains("jetbrains"))
            {
                agentType = AgentType.GitHubCopilotJetBrains;
                return true;
            }

            agentType = AgentType.GitHubCopilotVSCode;
            return true;
        }

        return false;
    }

    private AgentInstallContext? PromptForTargetScope(AgentInfo agent)
    {
        var options = BuildTargetOptions(agent);
        if (options.Count == 0)
        {
            console.MarkupLine($"[yellow]{EscapeMarkup($"No available scopes for {agent.Name}.")}[/]");
            return null;
        }

        AgentTargetOption selectedOption;
        if (options.Count == 1)
        {
            selectedOption = options[0];
            console.MarkupLine($"[dim]{EscapeMarkup($"Using {selectedOption.Label} for {agent.Name}")}[/]");
        }
        else
        {
            selectedOption = console.Prompt(
                new SelectionPrompt<AgentTargetOption>()
                    .Title($"[bold]{EscapeMarkup($"Select scope for {agent.Name}")}[/]")
                    .PageSize(10)
                    .AddChoices(options));
        }

        var context = selectedOption.Resolve();
        if (context is null)
        {
            console.MarkupLine($"[yellow]{EscapeMarkup($"Skipped scope selection for {agent.Name}.")}[/]");
        }
        return context;
    }

    private IReadOnlyList<AgentEntry> BuildAgentEntries(List<AgentInfo> agents)
    {
        var entries = new List<AgentEntry>();
        var copilotAgents = agents
            .Where(a => IsCopilotType(a.Type))
            .OrderBy(a => GetCopilotDisplayName(a))
            .ToList();

        if (copilotAgents.Count > 0)
        {
            entries.Add(new AgentEntry("GitHub Copilot", copilotAgents));
        }

        foreach (var agent in agents.Where(a => !IsCopilotType(a.Type)))
        {
            entries.Add(new AgentEntry(agent.Name, new List<AgentInfo> { agent }));
        }

        return entries;
    }

    private AgentInfo? SelectAgentFromEntry(AgentEntry entry)
    {
        if (entry.Agents.Count == 0)
            return null;

        if (entry.Agents.Count == 1)
            return entry.Agents[0];

        var prompt = new SelectionPrompt<AgentInfo>()
            .Title($"[bold]{EscapeMarkup($"Select {entry.Label} IDE")}[/]")
            .PageSize(10)
            .UseConverter(agent => $"{GetCopilotDisplayName(agent)} - {DescribeAgentLocation(agent)}");
        prompt.AddChoices(entry.Agents);
        return console.Prompt(prompt);
    }

    private IReadOnlyList<AgentTargetOption> BuildTargetOptions(AgentInfo agent) =>
        agent.Type switch
        {
            AgentType.ClaudeCode or AgentType.Codex => BuildCliTargetOptions(agent),
            AgentType.ClaudeDesktop => BuildClaudeDesktopOptions(agent),
            AgentType.GitHubCopilotVSCode => BuildVsCodeTargetOptions(agent),
            AgentType.GitHubCopilotVisualStudio => BuildVisualStudioTargetOptions(agent),
            AgentType.GitHubCopilotJetBrains => BuildJetBrainsTargetOptions(agent),
            _ => BuildDefaultTargetOptions(agent)
        };

    private AgentTargetOption CreateTargetOption(AgentInfo agent, string label, string? configPath, string? workingDir, bool preferCli) =>
        new AgentTargetOption(label, () => new AgentInstallContext(agent, label, configPath ?? agent.ConfigPath, workingDir, preferCli));

    private IReadOnlyList<AgentTargetOption> BuildDefaultTargetOptions(AgentInfo agent)
    {
        var path = agent.ConfigPath;
        if (string.IsNullOrWhiteSpace(path))
            return Array.Empty<AgentTargetOption>();

        var label = FormatScopeLabel(path, path);
        return
        [
            CreateTargetOption(
                agent,
                label,
                path,
                agent.WorkingDirectory,
                false)
        ];
    }

    private IReadOnlyList<AgentTargetOption> BuildCliTargetOptions(AgentInfo agent)
    {
        var list = new List<AgentTargetOption>();

        list.Add(CreateTargetOption(
            agent,
            FormatScopeLabel("Use agent working directory (recommended)", agent.ConfigPath),
            agent.ConfigPath,
            null,
            true));

        var repoPath = TryGetCurrentRepositoryPath();
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            list.Add(CreateTargetOption(
                agent,
                FormatScopeLabel($"Use current directory ({repoPath})", agent.ConfigPath),
                agent.ConfigPath,
                repoPath,
                true));
        }

        list.Add(new AgentTargetOption("Enter custom path...", () =>
        {
            var path = console.Ask<string>("Enter the [green]full path[/] to use as working directory:");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (!Directory.Exists(path))
            {
                console.MarkupLine("[yellow]⚠ Warning: Directory does not exist.[/]");
            }

            return new AgentInstallContext(agent, $"Custom path ({path})", agent.ConfigPath, path, true);
        }));
        return list;
    }

    private IReadOnlyList<AgentTargetOption> BuildClaudeDesktopOptions(AgentInfo agent)
    {
        var options = new List<AgentTargetOption>();
        var repoPath = TryGetCurrentRepositoryPath();
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            options.Add(CreateTargetOption(agent, FormatScopeLabel($"Current repository ({repoPath})", agent.ConfigPath), agent.ConfigPath, repoPath, false));
        }

        options.Add(new AgentTargetOption("Enter repository path...", () =>
        {
            var path = console.Ask<string>("Enter the [green]full path[/] to your repository:");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (!Directory.Exists(path))
            {
                console.MarkupLine("[yellow]⚠ Warning: Directory does not exist. Configuration will be created anyway.[/]");
            }

            return new AgentInstallContext(agent, $"Repository: {path}", agent.ConfigPath, path, false);
        }));

        return options;
    }

    private IReadOnlyList<AgentTargetOption> BuildVsCodeTargetOptions(AgentInfo agent)
    {
        var options = new List<AgentTargetOption>();
        var repoPath = TryGetCurrentRepositoryPath();
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            var workspacePath = Path.Combine(repoPath, ".vscode", "mcp.json");
            options.Add(CreateTargetOption(agent,
                FormatScopeLabel($".vscode/mcp.json ({workspacePath})", workspacePath),
                workspacePath,
                agent.WorkingDirectory,
                false));
        }

        var userPath = GetDefaultVsCodeCopilotConfigPath();
        if (!string.IsNullOrWhiteSpace(userPath))
        {
            var label = FormatScopeLabel($"User profile ({userPath})", userPath);
            options.Add(CreateTargetOption(agent, label, userPath, agent.WorkingDirectory, false));
        }

        options.Add(new AgentTargetOption("Enter custom config path...", () =>
        {
            var path = console.Ask<string>("Enter the [green]config file[/] to update:");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return new AgentInstallContext(agent, $"Custom config: {path}", path, agent.WorkingDirectory, false);
        }));

        return options;
    }

    private IReadOnlyList<AgentTargetOption> BuildVisualStudioTargetOptions(AgentInfo agent)
    {
        var candidates = GetVisualStudioCopilotConfigCandidates().ToList();
        if (candidates.Count == 0)
            return Array.Empty<AgentTargetOption>();

        return candidates
            .Select(candidate =>
                CreateTargetOption(
                    agent,
                    FormatScopeLabel($"{candidate.Label} ({candidate.Path})", candidate.Path),
                    candidate.Path,
                    agent.WorkingDirectory,
                    false))
            .ToList();
    }

    private IReadOnlyList<AgentTargetOption> BuildJetBrainsTargetOptions(AgentInfo agent)
    {
        var path = GetJetBrainsCopilotConfigPath();
        if (string.IsNullOrWhiteSpace(path))
            return Array.Empty<AgentTargetOption>();

        return
        [
            CreateTargetOption(
                agent,
                FormatScopeLabel(path, path),
                path,
                agent.WorkingDirectory,
                false)
        ];
    }

    private async Task InstallToAgentAsync(AgentInstallContext context, CancellationToken cancel)
    {
        var agent = context.Agent;
        var workingDir = context.WorkingDirectory ?? agent.WorkingDirectory ?? GetDefaultWorkingDirectory(agent.Type);
        var configPath = context.ConfigPath ?? agent.ConfigPath;

        var safeAgentName = EscapeMarkup(agent.Name);
        var safeScopeLabel = EscapeMarkup(context.ScopeLabel);

        if (context.PreferCli && TryGetCliDefinition(agent.Type, out var cliDefinition))
        {
            var cliResult = await InstallUsingCliAsync(agent, cliDefinition, workingDir, cancel).ConfigureAwait(false);
            if (cliResult is { Success: true })
            {
                console.MarkupLine($"[green]  ✓ Installed to {safeAgentName} ({safeScopeLabel})[/]");
                if (!string.IsNullOrEmpty(workingDir))
                {
                    console.MarkupLine($"[dim]    Working directory: {EscapeMarkup(workingDir)}[/]");
                }
                return;
            }

            var message = !string.IsNullOrWhiteSpace(cliResult.StandardError)
                ? cliResult.StandardError
                : (!string.IsNullOrWhiteSpace(cliResult.ErrorMessage) ? cliResult.ErrorMessage : $"{cliDefinition.ExecutableName} command failed.");

            if (agent.Type == AgentType.Codex)
            {
                console.MarkupLine($"[red]  ✗ Failed to install to {safeAgentName} ({safeScopeLabel}): {EscapeMarkup(message.Trim())}[/]");
                console.MarkupLine("[dim]  Ensure the Codex CLI is installed and on PATH, then retry.[/]");
                return;
            }

            console.MarkupLine($"[yellow]  CLI install for {safeAgentName} ({safeScopeLabel}) failed: {EscapeMarkup(message.Trim())}[/]");
            console.MarkupLine("[dim]  Falling back to updating the configuration file directly.[/]");
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            console.MarkupLine($"[yellow]  Skipping configuration update for {safeAgentName} ({safeScopeLabel}): configuration path is unknown.[/]");
            return;
        }

        await InstallViaConfigFileAsync(context, workingDir, configPath, cancel).ConfigureAwait(false);
    }

    private async Task<CliCommandResult> InstallUsingCliAsync(AgentInfo agent, AgentCliDefinition definition, string? workingDir, CancellationToken cancel)
    {
        CliCommandResult? addResult = null;

        await console.Status()
            .StartAsync($"Installing to {EscapeMarkup(agent.Name)} via {EscapeMarkup(definition.ExecutableName)}...", async _ =>
            {
                var executable = agent.ExecutablePath ?? definition.ExecutableName;
                var removeArgs = definition.BuildRemoveArguments();
                if (removeArgs.Count > 0)
                {
                    await RunCliCommandAsync(executable, removeArgs, cancel, ignoreErrors: true).ConfigureAwait(false);
                }

                var addArgs = definition.BuildAddArguments(workingDir);
                addResult = await RunCliCommandAsync(executable, addArgs, cancel).ConfigureAwait(false);
            });

        return addResult ?? new CliCommandResult(false, -1, string.Empty, string.Empty, $"Failed to execute {definition.ExecutableName} CLI.");
    }

    private async Task InstallViaConfigFileAsync(AgentInstallContext context, string? workingDir, string resolvedConfigPath, CancellationToken cancel)
    {
        var agent = context.Agent;
        var safeAgentName = EscapeMarkup(agent.Name);
        var safeScopeLabel = EscapeMarkup(context.ScopeLabel);

        JsonObject config;
        try
        {
            config = await LoadOrCreateConfigAsync(resolvedConfigPath, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]  ✗ Failed to read configuration for {safeAgentName}: {EscapeMarkup(ex.Message)}[/]");
            return;
        }

        var propertyPriority = GetServerPropertyPriority(agent.Type);
        var searchOrder = GetServerPropertySearchOrder(agent.Type);
        var targetProperty = propertyPriority[0];
        var serversNode = GetOrCreateServerCollection(config, targetProperty);
        var existingLocation = FindRepoqlLocation(config, searchOrder);

        if (existingLocation is not null)
        {
            console.MarkupLine($"[yellow]  RepoQL is already configured in {safeAgentName}[/]");

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
            ["command"] = GetRepoqlCommand(),
            ["args"] = new JsonArray("mcp")
        };

        if (!string.IsNullOrEmpty(workingDir))
        {
            repoqlConfig["env"] = new JsonObject
            {
                ["REPOQL_CWD"] = workingDir
            };
        }

        if (agent.Type is AgentType.ClaudeDesktop or AgentType.ClaudeCode)
        {
            repoqlConfig["allowedTools"] = new JsonArray("query", "xray");
        }

        if (existingLocation is not null && !ReferenceEquals(existingLocation.Parent, serversNode))
        {
            existingLocation.Parent.Remove("repoql");
        }

        serversNode["repoql"] = repoqlConfig;

        await console.Status()
            .StartAsync($"Installing to {EscapeMarkup(agent.Name)} ({safeScopeLabel})...", async _ =>
            {
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    var updatedJson = config.ToJsonString(options);
                    await File.WriteAllTextAsync(resolvedConfigPath, updatedJson, cancel).ConfigureAwait(false);

                    console.MarkupLine($"[green]  ✓ Installed to {safeAgentName} ({safeScopeLabel})[/]");
                    if (!string.IsNullOrEmpty(workingDir))
                    {
                        console.MarkupLine($"[dim]    Working directory: {EscapeMarkup(workingDir)}[/]");
                    }
                }
                catch (Exception ex)
                {
                    console.MarkupLine($"[red]  ✗ Failed to install to {safeAgentName}: {EscapeMarkup(ex.Message)}[/]");
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

                    args.Add("repoql"); // name
                    args.Add(GetRepoqlCommand()); // command
                    args.Add("mcp");    // command argument
                    return args;
                },
                BuildRemoveArguments: () => new[] { "mcp", "remove", "repoql" }),

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

                    args.Add("repoql"); // server name
                    args.Add("--");
                    args.Add(GetRepoqlCommand());
                    args.Add("mcp");
                    return args;
                },
                BuildRemoveArguments: () => new[] { "mcp", "remove", "--scope", "user", "repoql" }),

            [AgentType.GitHubCopilotVSCode] = new AgentCliDefinition(
                ExecutableName: OperatingSystem.IsWindows() ? "code.cmd" : "code",
                BuildAddArguments: workingDir =>
                    new[]
                    {
                        "--add-mcp",
                        BuildVsCodeMcpPayload(workingDir)
                    },
                BuildRemoveArguments: () => Array.Empty<string>())
        };

    private static string BuildVsCodeMcpPayload(string? workingDir)
    {
        var payload = new JsonObject
        {
            ["name"] = "repoql",
            ["command"] = GetRepoqlCommand(),
            ["args"] = new JsonArray("mcp")
        };

        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            payload["env"] = new JsonObject
            {
                ["REPOQL_CWD"] = workingDir
            };
        }

        return payload.ToJsonString();
    }

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
        builder.Append(QuoteForWindowsCommandLine(executable));
        foreach (var arg in arguments)
        {
            builder.Append(' ');
            builder.Append(QuoteForWindowsCommandLine(arg));
        }
        return builder.ToString();
    }

    private static string QuoteForWindowsCommandLine(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        var needsQuotes = value.AsSpan().IndexOfAny(' ', '\t', '"') >= 0;
        if (!needsQuotes)
            return value;

        var escaped = value.Replace("\"", "\"\"");
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

    private static string ResolveRepoqlCommand()
    {
        var processPath = Environment.ProcessPath;
        if (LooksLikeRepoqlExecutable(processPath))
            return processPath!;

        var located = LocateExecutable("repoql");
        if (!string.IsNullOrEmpty(located))
            return located;

        foreach (var candidate in EnumerateRepoqlExecutableCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "repoql";
    }

    private static bool LooksLikeRepoqlExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!File.Exists(path))
            return false;

        var name = Path.GetFileNameWithoutExtension(path);
        // Ignore test hosts that include "RepoQL" in their name (e.g., RepoQL.Cli.Tests)
        if (name.Contains("test", StringComparison.OrdinalIgnoreCase))
            return false;

        return name.Contains("repoql", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateRepoqlExecutableCandidates()
    {
        foreach (var dir in GetRepoqlSearchDirectories())
        {
            foreach (var name in GetRepoqlExecutableNames())
            {
                yield return Path.Combine(dir, name);
            }
        }
    }

    private static IEnumerable<string> GetRepoqlSearchDirectories()
    {
        var current = Environment.CurrentDirectory;
        var rawDirectories = new[]
        {
            SafeGetDirectory(Environment.ProcessPath),
            AppContext.BaseDirectory,
            SafeGetDirectory(typeof(InstallCommand).Assembly.Location),
            current,
            Path.Combine(current, "artifacts", "publish", "RepoQL.ConsoleApp"),
            Path.Combine(current, "artifacts", "publish", "RepoQL.ConsoleApp", "release"),
            Path.Combine(current, "artifacts", "publish", "RepoQL.ConsoleApp", "debug")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in rawDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(dir);
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(fullPath) && seen.Add(fullPath))
                yield return fullPath;
        }
    }

    private static string? SafeGetDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var dir = Path.GetDirectoryName(path);
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetRepoqlExecutableNames()
    {
        yield return "repoql";
        yield return "RepoQL";
        yield return "RepoQL.ConsoleApp";

        if (OperatingSystem.IsWindows())
        {
            yield return "repoql.exe";
            yield return "RepoQL.exe";
            yield return "RepoQL.ConsoleApp.exe";
            yield return "repoql.cmd";
        }
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

    private static string GetRepoqlCommand() => RepoqlCommandLazy.Value;

    private string? GetDefaultWorkingDirectory(AgentType agentType) => null;

    private sealed record RepoqlLocation(string PropertyName, JsonObject Parent);

    private record AgentInfo
    {
        public required string Name { get; init; }
        public required AgentType Type { get; init; }
        public string? ConfigPath { get; init; }
        public string? WorkingDirectory { get; set; }
        public string? ExecutablePath { get; set; }
    }

    private sealed record AgentInstallContext(AgentInfo Agent, string ScopeLabel, string? ConfigPath, string? WorkingDirectory, bool PreferCli);

    private sealed record AgentTargetOption(string Label, Func<AgentInstallContext?> Resolver)
    {
        public AgentInstallContext? Resolve() => Resolver();
        public override string ToString() => Label;
    }

    private sealed record ScopeCandidate(string Label, string Path);

    private sealed record AgentEntry(string Label, List<AgentInfo> Agents);

    private enum ConfigStatus
    {
        Missing,
        Exists,
        Configured
    }

    internal enum AgentType
    {
        ClaudeDesktop,
        ClaudeCode,
        Codex,
        GitHubCopilotVSCode,
        GitHubCopilotVisualStudio,
        GitHubCopilotJetBrains
    }
}
