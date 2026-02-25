using System.Text;
using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Go.Tests;

public sealed class GoLoaderTests
{
    [Test]
    public async Task Classifier_MapsGoKind()
    {
        var classifier = new GoClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns("main.go");

        var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be("code.go");
    }

    [Test]
    public async Task Classifier_PassesThroughNonGoFiles()
    {
        var classifier = new GoClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns("README.md");
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
    public async Task LoadAndMaterialize_RoundTrip_ProducesNodesEdgesSpans()
    {
        using var loader = new GoLoader();
        var source = ReadFixture("simple_struct.go");
        using var artifactScope = CreateArtifact("simple_struct.go", source);

        (await loader.CanLoadAsync(artifactScope.Artifact)).Should().BeTrue();
        artifactScope.Artifact.MediaType!.Kind.Should().Be("code.go");

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().NotBeEmpty();
        records.Edges.Should().NotBeEmpty();
        records.Spans.Should().NotBeEmpty();
    }

    [Test]
    public async Task LoadAndMaterialize_DocumentNode_HasExpectedProperties()
    {
        var records = await LoadRecordsAsync("simple_struct.go");

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["language"]!.ToString().Should().Be("go");
        docNode.Props["package_name"]!.ToString().Should().Be("main");
        docNode.Props["line_count"]!.GetValue<int>().Should().BeGreaterThan(0);
        docNode.Props["byte_size"]!.GetValue<long>().Should().BeGreaterThan(0);
    }

    [Test]
    public async Task LoadAndMaterialize_Struct_CreatesGoTypeNode()
    {
        var records = await LoadRecordsAsync("simple_struct.go");

        var typeNode = records.Nodes.Single(n =>
            n.Kind == "go.type"
            && n.Props["name"]!.ToString() == "Server");

        typeNode.Props["kind"]!.ToString().Should().Be("struct");
        typeNode.Props["qualified_name"]!.ToString().Should().Be("main.Server");
        typeNode.Props["accessibility"]!.ToString().Should().Be("public");
        typeNode.Props["is_exported"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public async Task LoadAndMaterialize_Methods_CreateGoMemberNodesWithReceiverInfo()
    {
        var records = await LoadRecordsAsync("simple_struct.go");
        var serverType = records.Nodes.Single(n =>
            n.Kind == "go.type"
            && n.Props["name"]!.ToString() == "Server");

        var serve = records.Nodes.Single(n =>
            n.Kind == "go.member"
            && n.Props["kind"]!.ToString() == "method"
            && n.Props["name"]!.ToString() == "Serve");

        serve.Props["receiver"]!.ToString().Should().Be("s");
        serve.Props["receiver_type"]!.ToString().Should().Be("Server");
        serve.Props["is_pointer_receiver"]!.GetValue<bool>().Should().BeTrue();
        serve.Props["is_static"]!.GetValue<bool>().Should().BeFalse();
        serve.Props["declaring_type"]!.ToString().Should().Be("main.Server");
        serve.Props["signature"]!.ToString().Should().Be("func (*Server) Serve(addr string) error");
        records.Edges.Should().Contain(e =>
            e.Type == "HAS_PART"
            && e.SrcId == serverType.Id
            && e.DstId == serve.Id);
    }

    [Test]
    public async Task LoadAndMaterialize_Functions_CreateGoFunctionNodes()
    {
        var records = await LoadRecordsAsync("simple_struct.go");

        var function = records.Nodes.Single(n =>
            n.Kind == "go.function"
            && n.Props["name"]!.ToString() == "NewServer");

        function.Props["kind"]!.ToString().Should().Be("function");
        function.Props["qualified_name"]!.ToString().Should().Be("main.NewServer");
        function.Props["signature"]!.ToString().Should().Be("func NewServer(db *sql.DB) *Server");
    }

    [Test]
    public async Task LoadAndMaterialize_Imports_CreateImportEdges()
    {
        var records = await LoadRecordsAsync("simple_struct.go");

        var imports = records.Edges.Where(e => e.Type == "IMPORTS").ToList();
        imports.Should().HaveCount(3);
        imports.Should().Contain(e =>
            e.Props["target"]!.ToString() == "fmt"
            && e.Props["import_category"]!.ToString() == "stdlib");
        imports.Should().Contain(e =>
            e.Props["target"]!.ToString() == "net/http"
            && e.Props["import_category"]!.ToString() == "stdlib");
        imports.Should().Contain(e =>
            e.Props["target"]!.ToString() == "github.com/gorilla/mux"
            && e.Props["import_category"]!.ToString() == "external");
    }

    [Test]
    public async Task LoadAndMaterialize_EmbeddedFields_CreateEmbedsEdges()
    {
        var records = await LoadRecordsAsync("embedding.go");
        var userType = records.Nodes.Single(n =>
            n.Kind == "go.type"
            && n.Props["name"]!.ToString() == "User");

        var embedEdges = records.Edges.Where(e => e.Type == "EMBEDS").ToList();
        embedEdges.Should().Contain(e =>
            e.SrcId == userType.Id
            && e.Props["target"]!.ToString() == "Base");
        embedEdges.Should().Contain(e =>
            e.SrcId == userType.Id
            && e.Props["target"]!.ToString() == "sync.Mutex");
    }

    [Test]
    public async Task LoadAndMaterialize_Fields_CreateGoMemberFieldNodes()
    {
        var records = await LoadRecordsAsync("simple_struct.go");

        var fields = records.Nodes
            .Where(n => n.Kind == "go.member" && n.Props["kind"]!.ToString() == "field")
            .ToList();

        fields.Should().Contain(n => n.Props["name"]!.ToString() == "DB");
        fields.Should().Contain(n => n.Props["name"]!.ToString() == "Handler");
        fields.Should().Contain(n => n.Props["name"]!.ToString() == "port");

        var db = fields.Single(n => n.Props["name"]!.ToString() == "DB");
        db.Props["field_type"]!.ToString().Should().Be("*sql.DB");
        db.Props["tag"]!.ToString().Should().Be("json:\"db\" db:\"database\"");

        var handler = fields.Single(n => n.Props["name"]!.ToString() == "Handler");
        handler.Props["is_embedded"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    public async Task LoadAndMaterialize_XrayHeadline_ContainsPackageAndPrimaryType()
    {
        var records = await LoadRecordsAsync("simple_struct.go");

        var artifact = records.Artifacts[0];
        artifact.Headline.Should().Contain("code.go");
        artifact.Headline.Should().Contain("pkg:main");
        artifact.Headline.Should().Contain("Server");
    }

    [Test]
    public async Task LoadAndMaterialize_XrayStructure_ContainsVisibilitySymbols()
    {
        var records = await LoadRecordsAsync("simple_struct.go");

        var structure = records.Artifacts[0].Structure;
        structure.Should().Contain("+ struct Server");
        structure.Should().Contain("  - field port int    #symbol=port");
        structure.Should().Contain("  + func (*Server) Serve(addr string) error    #symbol=Serve");
        structure.Should().Contain("+ func NewServer(db *sql.DB) *Server    #symbol=NewServer");
    }

    private static async Task<Records> LoadRecordsAsync(string fixtureName)
    {
        using var loader = new GoLoader();
        var source = ReadFixture(fixtureName);
        using var artifactScope = CreateArtifact(fixtureName, source);
        (await loader.CanLoadAsync(artifactScope.Artifact)).Should().BeTrue();
        var document = await loader.LoadAsync(artifactScope.Artifact);
        return loader.Materialize(document);
    }

    private static Task<(SemanticMediaType?, PipelineResult)> Next(IDiscoveredArtifact _)
        => Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_go_{Guid.NewGuid():N}");
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
