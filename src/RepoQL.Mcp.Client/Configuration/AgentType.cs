namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Identifies different AI agent applications that support MCP servers.
///
/// Purpose: Enables extensible support for loading global MCP configurations
/// from different agents. Each agent stores configs in different locations
/// and potentially different formats.
///
/// Complexity: None - simple enumeration. Agent-specific complexity is
/// isolated in AgentConfigLocator implementations.
/// </summary>
public enum AgentType
{
    /// <summary>
    /// Claude Desktop application (claude_desktop_config.json)
    /// </summary>
    ClaudeDesktop,

    /// <summary>
    /// Claude Code CLI (claude mcp list --json)
    /// </summary>
    ClaudeCode,

    /// <summary>
    /// OpenAI Codex CLI (~/.codex/config.toml)
    /// </summary>
    Codex,

    /// <summary>
    /// GitHub Copilot in VS Code (~/.config/Code/User/mcp.json)
    /// </summary>
    GitHubCopilotVSCode,

    /// <summary>
    /// GitHub Copilot in Visual Studio (.mcp.json or .vs/mcp.json)
    /// </summary>
    GitHubCopilotVisualStudio,

    /// <summary>
    /// GitHub Copilot in JetBrains IDEs
    /// </summary>
    GitHubCopilotJetBrains
}
