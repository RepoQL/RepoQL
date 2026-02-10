using System.Text;
using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Formats.Ruby;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Ruby.Tests;

public sealed class RubyLoaderTests
{
    [Test]
    [Arguments("app.rb", "code.ruby")]
    [Arguments("tasks.rake", "code.ruby.rake")]
    [Arguments("demo.gemspec", "code.ruby.gemspec")]
    [Arguments("Gemfile", "code.ruby.gemfile")]
    [Arguments("Rakefile", "code.ruby.rake")]
    [Arguments("Guardfile", "code.ruby")]
    [Arguments("Dangerfile", "code.ruby")]
    public async Task Classifier_MapsRubyKinds(string fileName, string expectedKind)
    {
        var classifier = new RubyClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns(fileName);

        var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be(expectedKind);
    }

    [Test]
    public async Task Classifier_PassesThroughErb()
    {
        var classifier = new RubyClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns("template.erb");
        var nextCalled = false;

        var (result, status) = await classifier.ProcessAsync(
            item,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().BeNull();
        status.Should().Be(PipelineResult.Success);
    }

    [Test]
    public async Task LoadAndMaterialize_EmitsRubyTypesMembersAndFunctions()
    {
        using var loader = new RubyLoader();
        var source = ReadFixture("simple_class.rb");
        using var artifactScope = CreateArtifact("user.rb", source);

        (await loader.CanLoadAsync(artifactScope.Artifact)).Should().BeTrue();
        artifactScope.Artifact.MediaType!.Kind.Should().Be("code.ruby");

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().NotBeEmpty();
        records.Edges.Should().NotBeEmpty();
        records.Spans.Should().NotBeEmpty();

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["language"]!.ToString().Should().Be("ruby");

        var typeNodes = records.Nodes.Where(n => n.Kind == "rb.type").ToList();
        typeNodes.Should().ContainSingle(n => n.Props["qualified_name"]!.ToString() == "App::User");
        typeNodes.Should().ContainSingle(n => n.Props["qualified_name"]!.ToString() == "App");

        var userType = typeNodes.Single(n => n.Props["qualified_name"]!.ToString() == "App::User");
        userType.Props["extends"]!.ToString().Should().Be("BaseUser");

        var memberNodes = records.Nodes.Where(n => n.Kind == "rb.member").ToList();
        memberNodes.Should().NotBeEmpty();
        memberNodes.Should().Contain(n => n.Props["name"]!.ToString() == "greet");
        memberNodes.Should().Contain(n => n.Props["name"]!.ToString() == "secret");
        memberNodes.Should().Contain(n => n.Props["name"]!.ToString() == "build");
        memberNodes.Should().Contain(n => n.Props["name"]!.ToString() == "from_json");

        memberNodes.Single(n => n.Props["name"]!.ToString() == "secret")
            .Props["accessibility"]!.ToString()
            .Should()
            .Be("private");
        memberNodes.Single(n => n.Props["name"]!.ToString() == "build")
            .Props["is_static"]!.GetValue<bool>()
            .Should()
            .BeTrue();

        var functionNodes = records.Nodes.Where(n => n.Kind == "rb.function").ToList();
        functionNodes.Should().ContainSingle(f => f.Props["name"]!.ToString() == "top_level");

        records.Edges.Where(e => e.Type == "HAS_PART").Should().NotBeEmpty();
        records.Edges.Where(e => e.Type == "HAS_PART").All(e => e.IsComposition).Should().BeTrue();
        records.Spans.All(s => s.StartLine >= 1).Should().BeTrue();
        records.Spans.All(s => s.EndByte >= s.StartByte).Should().BeTrue();
        records.Spans.All(s => s.DocumentId == docNode.Id).Should().BeTrue();

        var artifact = records.Artifacts[0];
        artifact.Headline.Should().Contain("user.rb | 3 declarations");
        artifact.Headline.Should().Contain("ln, ~");
        artifact.Structure.Should().Contain("#symbol=greet");
        artifact.Structure.Should().Contain("#symbol=secret");
    }

    [Test]
    public async Task LoadAndMaterialize_CapturesSingletonAndBlockAcceptingMethods()
    {
        using var loader = new RubyLoader();
        var source = ReadFixture("singleton_methods.rb") + Environment.NewLine + ReadFixture("block_accepting_methods.rb");
        using var artifactScope = CreateArtifact("combined.rb", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var singleton = records.Nodes.Single(n => n.Kind == "rb.member" && n.Props["name"]!.ToString() == "currency");
        singleton.Props["kind"]!.ToString().Should().Be("singleton_method");
        singleton.Props["receiver"]!.ToString().Should().Be("client");
        singleton.Props["is_static"]!.GetValue<bool>().Should().BeTrue();

        var around = records.Nodes.Single(n => n.Kind == "rb.member" && n.Props["name"]!.ToString() == "around");
        around.Props["accepts_block"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public async Task LoadAndMaterialize_CreatesConstantNodesWithQualifiedNames()
    {
        using var loader = new RubyLoader();
        var source = ReadFixture("constants_and_namespaces.rb");
        using var artifactScope = CreateArtifact("constants_and_namespaces.rb", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var constant = records.Nodes.Single(n => n.Kind == "rb.constant");
        constant.Props["name"]!.ToString().Should().Be("VALUE");
        constant.Props["qualified_name"]!.ToString().Should().Be("Outer::Inner::VALUE");

        var innerType = records.Nodes.Single(n =>
            n.Kind == "rb.type" && n.Props["qualified_name"]!.ToString() == "Outer::Inner");
        records.Edges.Should().Contain(e =>
            e.Type == "HAS_PART" &&
            e.IsComposition &&
            e.SrcId == innerType.Id &&
            e.DstId == constant.Id);
    }

    [Test]
    public async Task LoadAndMaterialize_CreatesRequireEdgesWithPathAndRelativeFlag()
    {
        using var loader = new RubyLoader();
        var source = ReadFixture("require_dependencies.rb");
        using var artifactScope = CreateArtifact("require_dependencies.rb", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);
        var documentNode = records.Nodes.Single(n => n.Kind == "document");

        var requires = records.Edges.Where(e => e.Type == "REQUIRES").ToArray();
        requires.Should().HaveCount(2);
        requires.Should().Contain(e =>
            e.SrcId == documentNode.Id &&
            e.DstId == null &&
            !e.IsComposition &&
            e.Props!["path"]!.ToString() == "json" &&
            e.Props["is_relative"]!.ToString() == "false");
        requires.Should().Contain(e =>
            e.SrcId == documentNode.Id &&
            e.DstId == null &&
            !e.IsComposition &&
            e.Props!["path"]!.ToString() == "../lib/support" &&
            e.Props["is_relative"]!.ToString() == "true");
    }

    [Test]
    public async Task LoadAndMaterialize_CreatesAliasEdgesWithAliasTypeAndMemberLinkWhenResolvable()
    {
        using var loader = new RubyLoader();
        var source = ReadFixture("alias_edges.rb");
        using var artifactScope = CreateArtifact("alias_edges.rb", source);

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var aliasEdges = records.Edges.Where(e => e.Type == "ALIASES").ToArray();
        aliasEdges.Should().HaveCount(2);
        aliasEdges.All(e => !e.IsComposition).Should().BeTrue();
        aliasEdges.Should().Contain(e => e.Props!["alias_type"]!.ToString() == "alias");
        aliasEdges.Should().Contain(e => e.Props!["alias_type"]!.ToString() == "alias_method");

        var memberById = records.Nodes
            .Where(n => n.Kind == "rb.member")
            .ToDictionary(n => n.Id);

        var resolvableEdge = aliasEdges.Single(e => e.Props!["alias_type"]!.ToString() == "alias");
        resolvableEdge.DstId.Should().NotBeNull();
        memberById[resolvableEdge.SrcId].Props["name"]!.ToString().Should().Be("copy");
        memberById[resolvableEdge.DstId!.Value].Props["name"]!.ToString().Should().Be("original");
    }

    [Test]
    public void SchemaScripts_ExposesRubyViews()
    {
        using var loader = new RubyLoader();

        var scripts = loader.GetSchemaScripts().ToList();

        scripts.Should().ContainSingle(s => s.Identifier == "ruby_views");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_types");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_methods");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_mixins");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_mro");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_inheritance");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_constants");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_requires");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_aliases");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_associations");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_validations");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_callbacks");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW ruby_metaprogramming");
    }

    private static Task<(SemanticMediaType?, PipelineResult)> Next(IDiscoveredArtifact _)
        => Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_ruby_{Guid.NewGuid():N}");
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
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }
}
