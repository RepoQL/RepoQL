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
    public void Parse_SimpleSource_ReturnsTreeOrGrammarUnavailableDiagnostic()
    {
        using var client = new CppTreeSitterClient();
        using var result = client.Parse("int add(int a, int b) { return a + b; }");

        if (client.IsGrammarAvailable)
        {
            result.GrammarAvailable.Should().BeTrue();
            result.HasTree.Should().BeTrue();
            result.Diagnostic.Should().BeNull();
            result.RootNodeType.Should().Be("translation_unit");
            return;
        }

        result.GrammarAvailable.Should().BeFalse();
        result.HasTree.Should().BeFalse();
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Parse_ConcurrentRequests_IsThreadSafe_WhenGrammarAvailable()
    {
        using var client = new CppTreeSitterClient();
        if (!client.IsGrammarAvailable)
        {
            Skip.Test("tree-sitter-cpp grammar is not bundled on this machine.");
            return;
        }

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

    [Test]
    public void Parse_WhenRuntimeBasePathHasNoGrammar_ReturnsUnavailable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_cpp_grammar_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var client = new CppTreeSitterClient(runtimeBasePath: tempDir);
            using var result = client.Parse("int main() { return 0; }");

            client.IsGrammarAvailable.Should().BeFalse();
            result.GrammarAvailable.Should().BeFalse();
            result.HasTree.Should().BeFalse();
            result.Diagnostic.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
