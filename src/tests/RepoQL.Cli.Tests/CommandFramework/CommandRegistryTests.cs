using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Commands;

namespace RepoQL.Cli.Tests.CommandFramework;

/// <summary>
/// Purpose: Verify command discovery, dispatch, prefix listing, fuzzy match, and help generation.
/// Complexity: Uses a minimal DI container with test command classes.
/// </summary>
internal sealed class CommandRegistryTests
{
    private CommandRegistry CreateRegistry()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var registry = new CommandRegistry(provider);
        registry.DiscoverCommands();
        return registry;
    }

    [Test]
    public void DiscoverCommands_FindsTestCommands()
    {
        var registry = CreateRegistry();
        var commands = registry.GetCommands();
        commands.Should().ContainKey("test.echo");
        commands.Should().ContainKey("test.grp.sub1");
        commands.Should().ContainKey("test.grp.sub2");
    }

    [Test]
    public async Task DispatchesKnownCommand()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.echo", ["hello"]);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("echo: hello");
    }

    [Test]
    public async Task UnknownCommand_ReturnsErrorWithSuggestion()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.ech", []);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Did you mean");
    }

    [Test]
    public async Task UnknownUnrelatedCommand_DoesNotIncludeSuggestion()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("x", []);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Unknown command ::x.");
        result.Text.Should().NotContain("Did you mean");
    }

    [Test]
    public async Task EmptyRegistry_UnknownCommandReportsNoCommandsRegistered()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        var registry = new CommandRegistry(provider);

        var parsed = new ParsedCommand("anything", []);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("No commands registered");
    }

    [Test]
    public async Task WrongParamCount_ReturnsUsage()
    {
        var registry = CreateRegistry();
        // test.echo requires 1 param (message is not nullable)
        var parsed = new ParsedCommand("test.echo", ["a", "b", "c"]);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Wrong parameter count");
    }

    [Test]
    public async Task OptionalParamsCanBeOmitted()
    {
        var registry = CreateRegistry();
        // test.optional has one required + one optional param
        var parsed = new ParsedCommand("test.optional", ["required-value"]);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("required-value");
    }

    [Test]
    public async Task MultipleOptionalParams_AcceptOneTwoOrThreeParameters()
    {
        var registry = CreateRegistry();

        var one = await registry.ExecuteAsync(new ParsedCommand("test.multiopt", ["req"]), CancellationToken.None);
        one.IsError.Should().BeFalse();
        one.Text.Should().Be("req|<null>|<null>");

        var two = await registry.ExecuteAsync(new ParsedCommand("test.multiopt", ["req", "a"]), CancellationToken.None);
        two.IsError.Should().BeFalse();
        two.Text.Should().Be("req|a|<null>");

        var three = await registry.ExecuteAsync(new ParsedCommand("test.multiopt", ["req", "a", "b"]), CancellationToken.None);
        three.IsError.Should().BeFalse();
        three.Text.Should().Be("req|a|b");
    }

    [Test]
    public async Task MultipleOptionalParams_WrongCountShowsExpectedRange()
    {
        var registry = CreateRegistry();
        var result = await registry.ExecuteAsync(new ParsedCommand("test.multiopt", []), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("expected 1-3");
    }

    [Test]
    public async Task NullableAndDefaultValueParams_AreOptionalForRequiredCount()
    {
        var registry = CreateRegistry();

        var tooFew = await registry.ExecuteAsync(new ParsedCommand("test.nullability", []), CancellationToken.None);
        tooFew.IsError.Should().BeTrue();
        tooFew.Text.Should().Contain("expected 1-3");

        var enough = await registry.ExecuteAsync(new ParsedCommand("test.nullability", ["required"]), CancellationToken.None);
        enough.IsError.Should().BeFalse();
        enough.Text.Should().Be("required");
    }

    [Test]
    public async Task OptionalDefaultValue_WhenOmitted_UsesDeclaredDefault()
    {
        var registry = CreateRegistry();
        var result = await registry.ExecuteAsync(new ParsedCommand("test.defaultvalue", ["hello"]), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("hello:auto");
    }

    [Test]
    public async Task PrefixListing_ListsSubcommands()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.grp", []);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("test.grp.sub1");
        result.Text.Should().Contain("test.grp.sub2");
    }

    [Test]
    public async Task Help_ShowsUsageForExactCommand()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.echo", [], IsHelp: true);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("test.echo");
        result.Text.Should().Contain("Echo a message back");
    }

    [Test]
    public async Task Help_OnPrefixGroup_ListsSubcommands()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.grp", [], IsHelp: true);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("test.grp.sub1");
        result.Text.Should().Contain("test.grp.sub2");
    }

    [Test]
    public async Task DispatchesSyncCommandHandler()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.sync", ["hello"]);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("sync: hello");
    }

    [Test]
    public async Task DispatchesCommandWithoutCancellationTokenParameter()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.nocancel", ["hello"]);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Be("nocancel: hello");
    }

    [Test]
    public void CommandWithoutCancellationToken_HasMetadataFlagFalse()
    {
        var registry = CreateRegistry();
        var command = registry.GetCommands()["test.nocancel"];

        command.HasCancellationToken.Should().BeFalse();
    }

    [Test]
    public async Task DuplicateCommandName_IsRegisteredOnlyOnce()
    {
        var registry = CreateRegistry();
        var commands = registry.GetCommands();

        var duplicateCount = 0;
        foreach (var key in commands.Keys)
        {
            if (key == "test.dup")
                duplicateCount++;
        }

        duplicateCount.Should().Be(1);

        var result = await registry.ExecuteAsync(new ParsedCommand("test.dup", []), CancellationToken.None);
        result.IsError.Should().BeFalse();
        result.Text.Should().BeOneOf("dup:first", "dup:second");
    }

    [Test]
    public async Task HandlerException_ReturnsError()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.throws", []);
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Boom!");
    }

    [Test]
    public async Task ParseError_ReturnsError()
    {
        var registry = CreateRegistry();
        var parsed = new ParsedCommand("test.echo", [], ParseError: "Unclosed bracket");
        var result = await registry.ExecuteAsync(parsed, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("Unclosed bracket");
    }
}

// --- Test command classes used by registry tests ---

[CommandClass]
public sealed class TestEchoCommand
{
    [Command("test.echo", Description = "Echo a message back")]
    public Task<CommandResult> Execute(
        [CommandParam("The message to echo")] string message,
        CancellationToken cancel)
    {
        return Task.FromResult(CommandResult.Success($"echo: {message}"));
    }
}

[CommandClass]
public sealed class TestOptionalCommand
{
    [Command("test.optional", Description = "Command with optional param")]
    public Task<CommandResult> Execute(
        string required,
        string? optional,
        CancellationToken cancel)
    {
        var text = optional != null ? $"{required}:{optional}" : required;
        return Task.FromResult(CommandResult.Success(text));
    }
}

[CommandClass]
public sealed class TestMultiOptionalCommand
{
    [Command("test.multiopt", Description = "Command with multiple optional params")]
    public Task<CommandResult> Execute(
        string required,
        string? first,
        string? second,
        CancellationToken cancel)
    {
        return Task.FromResult(CommandResult.Success($"{required}|{first ?? "<null>"}|{second ?? "<null>"}"));
    }
}

[CommandClass]
public sealed class TestNullabilityCommand
{
    [Command("test.nullability", Description = "Mix of required, nullable, and default params")]
    public Task<CommandResult> Execute(
        string required,
        string? optionalNullable,
        CancellationToken cancel,
        string optionalDefault = "fallback")
    {
        return Task.FromResult(CommandResult.Success(required));
    }
}

[CommandClass]
public sealed class TestSyncCommand
{
    [Command("test.sync", Description = "Synchronous command handler")]
    public CommandResult Execute(
        string message,
        CancellationToken cancel) => CommandResult.Success($"sync: {message}");
}

[CommandClass]
public sealed class TestNoCancelCommand
{
    [Command("test.nocancel", Description = "Handler without CancellationToken")]
    public Task<CommandResult> Execute(string message) =>
        Task.FromResult(CommandResult.Success($"nocancel: {message}"));
}

[CommandClass]
public sealed class TestDuplicateFirstCommand
{
    [Command("test.dup", Description = "First duplicate")]
    public Task<CommandResult> Execute(CancellationToken cancel) =>
        Task.FromResult(CommandResult.Success("dup:first"));
}

[CommandClass]
public sealed class TestDuplicateSecondCommand
{
    [Command("test.dup", Description = "Second duplicate")]
    public Task<CommandResult> Execute(CancellationToken cancel) =>
        Task.FromResult(CommandResult.Success("dup:second"));
}

[CommandClass]
public sealed class TestDefaultValueCommand
{
    [Command("test.defaultvalue", Description = "Optional default value behavior")]
    public Task<CommandResult> Execute(string required, string mode = "auto") =>
        Task.FromResult(CommandResult.Success($"{required}:{mode}"));
}

[CommandClass]
public sealed class TestGroupCommands
{
    [Command("test.grp.sub1", Description = "First subcommand")]
    public Task<CommandResult> Sub1(CancellationToken cancel) =>
        Task.FromResult(CommandResult.Success("sub1"));

    [Command("test.grp.sub2", Description = "Second subcommand")]
    public Task<CommandResult> Sub2(CancellationToken cancel) =>
        Task.FromResult(CommandResult.Success("sub2"));
}

[CommandClass]
public sealed class TestThrowsCommand
{
    [Command("test.throws", Description = "Always throws")]
    public Task<CommandResult> Execute(CancellationToken cancel) =>
        throw new InvalidOperationException("Boom!");
}
