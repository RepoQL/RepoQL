using AwesomeAssertions;
using RepoQL.ConsoleApp.Commands;

namespace RepoQL.Cli.Tests;
    
internal class InstallCommandTests
{
    [Test]
    public void CodexCliDefinition_ProducesExpectedArguments()
    {
        var definition = InstallCommand.CliDefinitions[InstallCommand.AgentType.Codex];

        string.Join(' ', definition.BuildAddArguments("{workspace}")).Should().Be("mcp add --env REPOQL_CWD={workspace} repoql mcp repoql");
        string.Join(' ', definition.BuildAddArguments(null)).Should().Be("mcp add repoql mcp repoql");
        string.Join(' ', definition.BuildRemoveArguments()).Should().Be("mcp remove repoql");
    }

    [Test]
    public void ClaudeCodeDefinition_ProducesExpectedArguments()
    {
        var definition = InstallCommand.CliDefinitions[InstallCommand.AgentType.ClaudeCode];

        string.Join(' ', definition.BuildAddArguments("{workspace}")).Should().Be("mcp add --scope user --transport stdio --env REPOQL_CWD={workspace} repoql -- repoql mcp");
        string.Join(' ', definition.BuildAddArguments(null)).Should().Be("mcp add --scope user --transport stdio repoql -- repoql mcp");
        string.Join(' ', definition.BuildRemoveArguments()).Should().Be("mcp remove --scope user repoql");
    }

    [Test]
    public void GitHubCopilotVSCodeDefinition_ProducesExpectedArguments()
    {
        var definition = InstallCommand.CliDefinitions[InstallCommand.AgentType.GitHubCopilotVSCode];

        var argsWithWorkspace = definition.BuildAddArguments("{workspace}");
        argsWithWorkspace.Should().HaveCount(2);
        argsWithWorkspace[0].Should().Be("--add-mcp");
        argsWithWorkspace[1].Should().Contain("\"name\":\"repoql\"");
        argsWithWorkspace[1].Should().Contain("\"command\":\"repoql\"");
        argsWithWorkspace[1].Should().Contain("\"args\":[\"mcp\"]");
        argsWithWorkspace[1].Should().Contain("\"REPOQL_CWD\":\"{workspace}\"");

        var argsWithoutWorkspace = definition.BuildAddArguments(null);
        argsWithoutWorkspace[1].Should().Contain("\"name\":\"repoql\"");
        argsWithoutWorkspace[1].Should().NotContain("REPOQL_CWD");
        definition.BuildRemoveArguments().Should().BeEmpty();
    }

    [Test]
    public void TryClassifyAgent_RecognizesClaudeCodeTokens()
    {
        InstallCommand.TryClassifyAgent("claude-code", out var agentType).Should().BeTrue();
        agentType.Should().Be(InstallCommand.AgentType.ClaudeCode);
    }

    [Test]
    public void TryClassifyAgent_RecognizesCopilotVisualStudioTokens()
    {
        InstallCommand.TryClassifyAgent("copilot visualstudio", out var agentType).Should().BeTrue();
        agentType.Should().Be(InstallCommand.AgentType.GitHubCopilotVisualStudio);
    }

    [Test]
    public void TryClassifyAgent_RecognizesCopilotJetBrainsTokens()
    {
        InstallCommand.TryClassifyAgent("copilot intellij", out var agentType).Should().BeTrue();
        agentType.Should().Be(InstallCommand.AgentType.GitHubCopilotJetBrains);
    }
}
