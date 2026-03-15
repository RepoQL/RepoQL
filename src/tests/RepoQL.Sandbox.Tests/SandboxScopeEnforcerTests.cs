using AwesomeAssertions;

namespace RepoQL.Sandbox.Tests;

public sealed class SandboxScopeEnforcerTests
{
    [Test]
    public void EnforceRead_AllowedUri_NoException()
    {
        var enforcer = new SandboxScopeEnforcer(readScopes: ["file:///src/**"]);

        var action = () => enforcer.EnforceRead("file:///src/Foo.cs");

        action.Should().NotThrow();
    }

    [Test]
    public void EnforceRead_DeniedUri_ThrowsScopeException()
    {
        var enforcer = new SandboxScopeEnforcer(readScopes: ["file:///src/**"]);

        var action = () => enforcer.EnforceRead("file:///etc/passwd");

        action.Should().Throw<SandboxScopeException>()
            .Which.Message.Should().Contain("file:///src/**");
    }

    [Test]
    public void EnforceWrite_DefaultScope_AllowsRepoqlTmp()
    {
        var enforcer = new SandboxScopeEnforcer();

        var action = () => enforcer.EnforceWrite("file://.repoql/tmp/output.csv");

        action.Should().NotThrow();
    }

    [Test]
    public void EnforceWrite_DefaultScope_DeniesArbitraryPath()
    {
        var enforcer = new SandboxScopeEnforcer();

        var action = () => enforcer.EnforceWrite("file:///src/Foo.cs");

        action.Should().Throw<SandboxScopeException>();
    }
}
