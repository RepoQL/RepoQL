using AwesomeAssertions;
using RepoQL.Formats.Ruby.TreeSitter;

namespace RepoQL.Formats.Ruby.Tests;

public sealed class RubyConcurrentParsingTests
{
    [Test]
    public async Task Parse_DifferentSources_ThreadSafeWithEightThreads()
    {
        using var client = new RubyTreeSitterClient();
        var sources = new[]
        {
            ReadFixture("simple_class.rb"),
            ReadFixture("module_with_methods.rb"),
            ReadFixture("visibility_modifiers.rb"),
            ReadFixture("constants_and_namespaces.rb"),
            ReadFixture("require_dependencies.rb"),
            ReadFixture("malformed.rb"),
            "class A; def one; end; end",
            "module B; def two; end; end"
        };

        var tasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => client.Parse(sources[i])))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(8);
        results[0].Classes.Should().Contain(c => c.Name == "User");
        results[1].Modules.Should().Contain(m => m.Name == "Searchable");
        results[2].Classes.Should().Contain(c => c.Name == "VisibilityExample");
        results[3].Classes.Should().Contain(c => c.QualifiedName == "Outer::Inner");
        results[4].Requires.Should().HaveCount(2);
        results[5].ErrorNodeCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Parse_SameSource_ThreadSafeWithEightThreads()
    {
        using var client = new RubyTreeSitterClient();
        var source = ReadFixture("simple_class.rb");

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => client.Parse(source)))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var baseline = results[0];

        results.Should().AllSatisfy(r =>
        {
            r.Classes.Select(c => c.Name).Should().BeEquivalentTo(baseline.Classes.Select(c => c.Name));
            r.Modules.Select(m => m.Name).Should().BeEquivalentTo(baseline.Modules.Select(m => m.Name));
            r.Functions.Select(f => f.Name).Should().BeEquivalentTo(baseline.Functions.Select(f => f.Name));
            r.Requires.Select(req => req.Path).Should().BeEquivalentTo(baseline.Requires.Select(req => req.Path));
            r.Aliases.Select(a => $"{a.AliasType}:{a.NewName}->{a.OriginalName}")
                .Should()
                .BeEquivalentTo(baseline.Aliases.Select(a => $"{a.AliasType}:{a.NewName}->{a.OriginalName}"));
            r.ErrorNodeCount.Should().Be(baseline.ErrorNodeCount);
        });
    }

    private static string ReadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }
}
