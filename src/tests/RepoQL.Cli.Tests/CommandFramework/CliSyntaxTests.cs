using AwesomeAssertions;
using RepoQL.ConsoleApp.Commands;

namespace RepoQL.Cli.Tests.CommandFramework;

internal sealed class CliSyntaxTests
{
    [Test]
    public void BuildReadExpression_NormalizesRelativeTreeRequest()
    {
        var expression = CliSyntax.BuildReadExpression("src/**", tree: "folders");

        expression.Should().StartWith("file:///");
        expression.Should().EndWith("/src/** => tree: folders");
    }

    [Test]
    public void BuildReadExpression_AppendsSymbolFragment()
    {
        var expression = CliSyntax.BuildReadExpression(
            "src/RepoQL.Commands/CommandRegistry.cs",
            symbol: "CommandRegistry.ExecuteAsync");

        expression.Should().StartWith("file:///");
        expression.Should().EndWith("/src/RepoQL.Commands/CommandRegistry.cs#symbol=CommandRegistry.ExecuteAsync");
    }

    [Test]
    public void BuildReadExpression_RejectsLegacyModifierSyntax()
    {
        var action = () => CliSyntax.BuildReadExpression("src/** => tree: folders");

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Use read flags*");
    }

    [Test]
    public void BuildReadExpression_RejectsMultipleViews()
    {
        var action = () => CliSyntax.BuildReadExpression("src/**", tree: "folders", structure: true);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*one read view at a time*");
    }

    [Test]
    public void NormalizeCliImportUri_ConvertsRelativePathToLocalUri()
    {
        var importUri = CliSyntax.NormalizeCliImportUri("../other-repo");

        importUri.Should().StartWith("local:///");
        importUri.Should().Contain("/other-repo");
    }
}
