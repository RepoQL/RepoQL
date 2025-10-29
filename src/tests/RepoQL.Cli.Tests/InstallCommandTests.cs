using System;
using System.Collections.Generic;
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
    public void ClaudeCliDefinition_ProducesExpectedArguments()
    {
        var definition = InstallCommand.CliDefinitions[InstallCommand.AgentType.ClaudeCLI];

        string.Join(' ', definition.BuildAddArguments("{workspace}")).Should().Be("mcp add --scope user --transport stdio --env REPOQL_CWD={workspace} repoql -- repoql mcp");
        string.Join(' ', definition.BuildAddArguments(null)).Should().Be("mcp add --scope user --transport stdio repoql -- repoql mcp");
        string.Join(' ', definition.BuildRemoveArguments()).Should().Be("mcp remove --scope user repoql");
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
    public void TryClassifyAgent_RecognizesClaudeCodeTokens()
    {
        InstallCommand.TryClassifyAgent("claude-code", out var agentType).Should().BeTrue();
        agentType.Should().Be(InstallCommand.AgentType.ClaudeCode);
    }
}
