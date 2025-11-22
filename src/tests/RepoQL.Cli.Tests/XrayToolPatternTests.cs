using AwesomeAssertions;
using RepoQL.ConsoleApp.Tools;

namespace RepoQL.Cli.Tests;

internal class XrayToolPatternTests
{
    [Test]
    public void SplitGlobAndFragment_AllowsGlobWithSymbolFragment()
    {
        var (container, fragment) = XrayTool.SplitGlobAndFragment("**/*Service.cs#symbol=Authenticate");

        container.Should().Be("**/*Service.cs");
        fragment.Should().Be("#symbol=Authenticate");
    }

    [Test]
    public void SplitGlobAndFragment_AllowsGlobWithLineFragment()
    {
        var (container, fragment) = XrayTool.SplitGlobAndFragment("**/*.md#line=3,5");

        container.Should().Be("**/*.md");
        fragment.Should().Be("#line=3,5");
    }

    [Test]
    public void SplitGlobAndFragment_ReturnsNullsWhenEmpty()
    {
        var (container, fragment) = XrayTool.SplitGlobAndFragment(null);

        container.Should().BeNull();
        fragment.Should().BeNull();
    }

    [Test]
    public void AppendFragment_AddsFragmentOnce()
    {
        XrayTool.AppendFragment("file:///AuthService.cs", "#symbol=Authenticate")
            .Should().Be("file:///AuthService.cs#symbol=Authenticate");

        XrayTool.AppendFragment("file:///AuthService.cs#existing", "#symbol=Authenticate")
            .Should().Be("file:///AuthService.cs#existing");
    }

    [Test]
    public void AppendFragment_IgnoresNullFragment()
    {
        XrayTool.AppendFragment("file:///AuthService.cs", null)
            .Should().Be("file:///AuthService.cs");
    }
}
