using AwesomeAssertions;

namespace RepoQL.Sandbox.Tests;

public sealed class BuiltinModuleProviderTests
{
    [Test]
    public void Load_BareSpecifier_ReturnsSource()
    {
        var source = BuiltinModuleProvider.Load("yaml");

        source.Should().NotBeNull();
        source.Should().NotBeEmpty();
    }

    [Test]
    public void Load_WithRepoqlPrefix_ReturnsSource()
    {
        var bare = BuiltinModuleProvider.Load("yaml");
        var prefixed = BuiltinModuleProvider.Load("repoql:yaml");

        prefixed.Should().BeSameAs(bare);
    }

    [Test]
    public void Load_UnknownModule_ReturnsNull()
    {
        var source = BuiltinModuleProvider.Load("nonexistent");

        source.Should().BeNull();
    }

    [Test]
    public void Load_AgentModule_ReturnsNull()
    {
        var source = BuiltinModuleProvider.Load("repoql:@agent/foo");

        source.Should().BeNull();
    }

    [Test]
    public void AvailableModules_ContainsExpectedModules()
    {
        BuiltinModuleProvider.AvailableModules.Should().BeEquivalentTo(
        [
            "yaml",
            "toml",
            "json5",
            "xml",
            "ini",
            "semver",
            "diff",
            "microdiff",
            "ohash",
            "fuse",
            "ignore",
            "base64",
            "dayjs",
            "change-case",
            "mustache",
            "radash",
            "picomatch",
            "toposort",
            "front-matter",
            "parse-diff"
        ]);
    }

    [Test]
    public void Load_CachesResult()
    {
        var first = BuiltinModuleProvider.Load("yaml");
        var second = BuiltinModuleProvider.Load("yaml");

        second.Should().BeSameAs(first);
    }
}
