using AwesomeAssertions;
using RepoQL.Formats.Cpp.TreeSitter;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppTreeSitterClientTests
{
    [Test]
    public void Parse_Null_Throws()
    {
        using var client = new CppTreeSitterClient();
        var action = () => client.Parse(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Parse_SimpleSource_ReturnsTree()
    {
        using var client = new CppTreeSitterClient();
        using var result = client.Parse("int add(int a, int b) { return a + b; }");

        result.GrammarAvailable.Should().BeTrue();
        result.HasTree.Should().BeTrue();
        result.Diagnostic.Should().BeNull();
        result.RootNodeType.Should().Be("translation_unit");
    }

    [Test]
    public async Task Parse_ConcurrentRequests_IsThreadSafe()
    {
        using var client = new CppTreeSitterClient();

        var source = "int add(int a, int b) { return a + b; }";
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                using var result = client.Parse(source);
                return result.HasTree && result.GrammarAvailable;
            }))
            .ToArray();

        var outcomes = await Task.WhenAll(tasks);
        outcomes.Should().OnlyContain(v => v);
    }
}
