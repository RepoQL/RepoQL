using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Data.DuckDB;
using RepoQL.Formats.Ruby;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Formats.Ruby.Tests;

public sealed class RubyMixinTests
{
    [Test]
    public async Task Materialize_CreatesDeferredMixinAndInheritanceEdges()
    {
        using var loader = new RubyLoader();
        using var artifactScope = CreateArtifact("mixin_graph.rb", ReadFixture("mixin_graph.rb"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var customer = records.Nodes.Single(n =>
            n.Kind == "rb.type" && n.Props["qualified_name"]!.ToString() == "Customer");
        var formatting = records.Nodes.Single(n =>
            n.Kind == "rb.type" && n.Props["qualified_name"]!.ToString() == "Formatting");

        var customerExtends = records.Edges.Single(e => e.SrcId == customer.Id && e.Type == "EXTENDS");
        customerExtends.IsComposition.Should().BeFalse();
        customerExtends.DstId.Should().BeNull();
        customerExtends.Props!["target"]!.ToString().Should().Be("ApplicationRecord");

        var includes = records.Edges
            .Where(e => e.SrcId == customer.Id && e.Type == "INCLUDES")
            .OrderBy(e => e.Props!["ordinal"]!.GetValue<int>())
            .ToArray();
        includes.Should().HaveCount(2);
        includes[0].Props!["target"]!.ToString().Should().Be("Alpha");
        includes[0].Props!["ordinal"]!.GetValue<int>().Should().Be(0);
        includes[1].Props!["target"]!.ToString().Should().Be("Beta");
        includes[1].Props!["ordinal"]!.GetValue<int>().Should().Be(1);
        includes.All(e => !e.IsComposition && e.DstId == null).Should().BeTrue();

        var prepends = records.Edges
            .Where(e => e.SrcId == customer.Id && e.Type == "PREPENDS")
            .ToArray();
        prepends.Should().ContainSingle();
        prepends[0].Props!["target"]!.ToString().Should().Be("Gatekeeper");
        prepends[0].Props!["ordinal"]!.GetValue<int>().Should().Be(2);
        prepends[0].IsComposition.Should().BeFalse();
        prepends[0].DstId.Should().BeNull();

        var extendsModule = records.Edges
            .Where(e => e.SrcId == customer.Id && e.Type == "EXTENDS_MODULE")
            .Single();
        extendsModule.Props!["target"]!.ToString().Should().Be("TypeMethods");
        extendsModule.Props!["ordinal"]!.GetValue<int>().Should().Be(3);
        extendsModule.IsComposition.Should().BeFalse();
        extendsModule.DstId.Should().BeNull();

        var extendSelf = records.Edges
            .Single(e => e.SrcId == formatting.Id && e.Type == "EXTENDS_MODULE" && e.Props is not null);
        extendSelf.Props!["target"].Should().BeNull();
        extendSelf.Props!["ordinal"]!.GetValue<int>().Should().Be(0);
        extendSelf.IsComposition.Should().BeFalse();
        extendSelf.DstId.Should().BeNull();
    }

    [Test]
    public async Task Views_ProjectMixinOrderAndMroTier()
    {
        await using var repo = await CreateRubyRepoAsync();
        repo.AddOrUpdateText("mixin_graph.rb", ReadFixture("mixin_graph.rb"));
        await repo.IndexAsync();

        var mixins = repo.Store.RawQuery(
            """
            SELECT mechanism, module_name, mixin_order
            FROM ruby_mixins
            WHERE type_name = 'Customer'
            ORDER BY mixin_order
            """).ToArray();

        mixins.Should().HaveCount(4);
        mixins[0]["mechanism"]!.ToString().Should().Be("INCLUDES");
        mixins[0]["module_name"]!.ToString().Should().Be("Alpha");
        mixins[0]["mixin_order"]!.ToString().Should().Be("0");
        mixins[1]["module_name"]!.ToString().Should().Be("Beta");
        mixins[1]["mixin_order"]!.ToString().Should().Be("1");
        mixins[2]["mechanism"]!.ToString().Should().Be("PREPENDS");
        mixins[2]["mixin_order"]!.ToString().Should().Be("2");
        mixins[3]["mechanism"]!.ToString().Should().Be("EXTENDS_MODULE");
        mixins[3]["mixin_order"]!.ToString().Should().Be("3");

        var mro = repo.Store.RawQuery(
            """
            SELECT mechanism, module_name, mro_tier, mixin_order
            FROM ruby_mro
            WHERE type_name = 'Customer'
            ORDER BY mro_tier, mixin_order
            """).ToArray();

        mro.Should().HaveCount(4);
        mro[0]["mechanism"]!.ToString().Should().Be("PREPENDS");
        mro[0]["module_name"]!.ToString().Should().Be("Gatekeeper");
        mro[0]["mro_tier"]!.ToString().Should().Be("0");
        mro[1]["mechanism"]!.ToString().Should().Be("INCLUDES");
        mro[1]["module_name"]!.ToString().Should().Be("Alpha");
        mro[1]["mro_tier"]!.ToString().Should().Be("1");
        mro[2]["mechanism"]!.ToString().Should().Be("INCLUDES");
        mro[2]["module_name"]!.ToString().Should().Be("Beta");
        mro[2]["mro_tier"]!.ToString().Should().Be("1");
        mro[3]["mechanism"]!.ToString().Should().Be("EXTENDS_MODULE");
        mro[3]["module_name"]!.ToString().Should().Be("TypeMethods");
        mro[3]["mro_tier"]!.ToString().Should().Be("2");
    }

    private static async Task<IndexedRepoBuilder> CreateRubyRepoAsync()
    {
        return await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            options.DeleteDatabaseOnDispose = true;

            var loader = new RubyLoader();
            options.AddFormat(new FormatDescriptor(
                RubyMediaTypes.Ruby,
                loader,
                NoOpFormatAnalyzer.Instance,
                loader,
                ["rb"]));
            options.AddSchemaProvider(loader);
        });
    }

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_ruby_mixins_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        File.WriteAllText(filePath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);
        return new ArtifactScope(
            new DiscoveredArtifact
            {
                File = provider.GetFileInfo(fileName),
                RepoUri = RepoUri.Parse($"file:///{fileName}")
            },
            tempDir,
            provider);
    }

    private sealed class ArtifactScope(DiscoveredArtifact artifact, string tempDir, IFileProvider provider) : IDisposable
    {
        public DiscoveredArtifact Artifact { get; } = artifact;
        private readonly string _tempDir = tempDir;
        private readonly IFileProvider _provider = provider;

        public void Dispose()
        {
            (_provider as IDisposable)?.Dispose();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class NoOpFormatAnalyzer : IFormatAnalyzer
    {
        public static NoOpFormatAnalyzer Instance { get; } = new();

        public bool Supports(SemanticMediaType mediaType) => true;

        public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(
            DocumentModel document,
            AnalyzerContext context,
            CancellationToken cancellationToken = default)
        {
            _ = document;
            _ = context;
            _ = cancellationToken;
            await Task.CompletedTask;
            yield break;
        }
    }
}
