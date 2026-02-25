using AwesomeAssertions;
using RepoQL.Formats.Python.TreeSitter;

namespace RepoQL.Formats.Python.Tests;

public sealed class PythonConcurrentParsingTests
{
    [Test]
    public async Task Parse_DifferentSources_ThreadSafeWithEightThreads()
    {
        using var client = new PythonTreeSitterClient();
        var sources = new[]
        {
            ReadFixture("simple_class.py"),
            ReadFixture("dataclass_example.py"),
            ReadFixture("imports_basic.py"),
            ReadFixture("type_aliases.py"),
            ReadFixture("async_functions.py"),
            ReadFixture("framework_django_model.py"),
            ReadFixture("malformed.py"),
            "class Inline: pass"
        };

        var tasks = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => client.Parse(sources[i])))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(8);
        results[0].Classes.Should().Contain(c => c.Name == "User");
        results[1].Classes.Should().Contain(c => c.Name == "Point");
        results[2].Imports.Should().NotBeEmpty();
        results[3].TypeAliases.Should().NotBeEmpty();
        results[4].Functions.Should().Contain(f => f.Name == "stream");
        results[5].FrameworkHints.Should().NotBeEmpty();
        results[6].ErrorNodeCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task Parse_SameSource_ThreadSafeWithEightThreads()
    {
        using var client = new PythonTreeSitterClient();
        var source = ReadFixture("simple_class.py");

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => client.Parse(source)))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var baseline = results[0];

        results.Should().AllSatisfy(r =>
        {
            r.Classes.Select(c => c.Name).Should().BeEquivalentTo(baseline.Classes.Select(c => c.Name));
            r.Functions.Select(f => f.Name).Should().BeEquivalentTo(baseline.Functions.Select(f => f.Name));
            r.Imports.Select(i => i.Module).Should().BeEquivalentTo(baseline.Imports.Select(i => i.Module));
            r.Constants.Select(c => c.Name).Should().BeEquivalentTo(baseline.Constants.Select(c => c.Name));
            r.TypeAliases.Select(a => a.Name).Should().BeEquivalentTo(baseline.TypeAliases.Select(a => a.Name));
            r.ErrorNodeCount.Should().Be(baseline.ErrorNodeCount);
        });
    }

    private static string ReadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(path);
    }
}
