using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using RepoQL.ConsoleApp.Commands;

namespace RepoQL.Cli.Tests;
    
internal class InstallCommandTests
{
    [Test]
    public void CodexCliDefinition_ProducesExpectedArguments()
    {
        var definition = InstallCommand.CliDefinitions[InstallCommand.AgentType.Codex];
        var workingDir = "/path/to/workspace";
        var repoqlCommand = GetRepoqlCommandForTests();

        string.Join(' ', definition.BuildAddArguments(workingDir)).Should().Be($"mcp add --env REPOQL_CWD={workingDir} repoql {repoqlCommand} mcp");
        string.Join(' ', definition.BuildAddArguments(null)).Should().Be($"mcp add repoql {repoqlCommand} mcp");
        string.Join(' ', definition.BuildRemoveArguments()).Should().Be("mcp remove repoql");
    }

    [Test]
    public void ClaudeCodeDefinition_ProducesExpectedArguments()
    {
        var definition = InstallCommand.CliDefinitions[InstallCommand.AgentType.ClaudeCode];
        var workingDir = "/path/to/workspace";
        var repoqlCommand = GetRepoqlCommandForTests();

        string.Join(' ', definition.BuildAddArguments(workingDir)).Should().Be($"mcp add --scope user --transport stdio --env REPOQL_CWD={workingDir} repoql -- {repoqlCommand} mcp");
        string.Join(' ', definition.BuildAddArguments(null)).Should().Be($"mcp add --scope user --transport stdio repoql -- {repoqlCommand} mcp");
        string.Join(' ', definition.BuildRemoveArguments()).Should().Be("mcp remove --scope user repoql");
    }

    [Test]
    public void GitHubCopilotVSCodeDefinition_ProducesExpectedArguments()
    {
        var definition = InstallCommand.CliDefinitions[InstallCommand.AgentType.GitHubCopilotVSCode];
        var workingDir = "/path/to/workspace";
        var repoqlCommand = GetRepoqlCommandForTests();

        var argsWithWorkspace = definition.BuildAddArguments(workingDir);
        argsWithWorkspace.Should().HaveCount(2);
        argsWithWorkspace[0].Should().Be("--add-mcp");
        AssertVsCodePayload(argsWithWorkspace[1], repoqlCommand, workingDir);

        var argsWithoutWorkspace = definition.BuildAddArguments(null);
        AssertVsCodePayload(argsWithoutWorkspace[1], repoqlCommand, null);
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

    private static string GetRepoqlCommandForTests()
    {
        var method = typeof(InstallCommand).GetMethod("GetRepoqlCommand", BindingFlags.Static | BindingFlags.NonPublic);
        return (string)method!.Invoke(null, null)!;
    }

    private static void AssertVsCodePayload(string payload, string repoqlCommand, string? workingDir)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        root.GetProperty("name").GetString().Should().Be("repoql");
        root.GetProperty("command").GetString().Should().Be(repoqlCommand);

        var args = root.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        args.SequenceEqual(new[] { "mcp" }).Should().BeTrue();

        if (workingDir is null)
        {
            root.TryGetProperty("env", out _).Should().BeFalse();
        }
        else
        {
            root.GetProperty("env").GetProperty("REPOQL_CWD").GetString().Should().Be(workingDir);
        }
    }
}
