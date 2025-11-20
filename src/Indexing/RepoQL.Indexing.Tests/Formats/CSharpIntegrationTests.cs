using AwesomeAssertions;
using RepoQL.Formats.DotNet;
using RepoQL.Testing.Formats;

namespace RepoQL.Indexing.Tests.Formats;

public class CSharpIntegrationTests : FormatIntegrationTestBase
{
    private const string SampleCSharp = """
        namespace Demo.Core;

        public class Widget
        {
            private readonly int _seed;

            public Widget(int seed) => _seed = seed;

            public int Add(int value) => _seed + value;
        }
        """;

    [Test]
    [DisplayName("Processes C# files through classification and parsing")]
    public async Task Given_CSharpFile_When_Processed_Then_ProducesRecords()
    {
        var loader = new CSharpLoader();
        var harness = CreateHarness()
            .WithClassifier(new CSharpClassifier(CreateLogger<CSharpClassifier>()))
            .WithParser(new CSharpParser(loader, CreateLogger<CSharpParser>()))
            .Build();

        var result = await harness.ProcessFileAsync("Widget.cs", SampleCSharp);

        result.Should()
            .HaveSucceeded()
            .WithMediaType(CSharpLoader.MediaKind)
            .WithRecords()
            .WithNodesOfKind("csharp.namespace")
            .WithNodesOfKind("csharp.type")
            .WithNodesOfKind("csharp.member");

        result.Item.TryGetValue("document_model", out var docModel).Should().BeTrue();
        docModel.Should().NotBeNull();
    }
}
