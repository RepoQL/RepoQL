using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.Python.Tests;

public sealed class PythonLoaderTests
{
    [Test]
    public async Task LoadAndMaterialize_DocumentNodeHasCoreProperties()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("simple_class.py", ReadFixture("simple_class.py"));

        (await loader.CanLoadAsync(artifactScope.Artifact)).Should().BeTrue();
        artifactScope.Artifact.MediaType!.Kind.Should().Be("code.python");

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["language"]!.GetValue<string>().Should().Be("python");
        documentNode.Props["line_count"]!.GetValue<int>().Should().BeGreaterThan(0);
        documentNode.Props["byte_size"]!.GetValue<long>().Should().BeGreaterThan(0);
        documentNode.Props["constants"].Should().NotBeNull();
        documentNode.Props["type_aliases"].Should().NotBeNull();
    }

    [Test]
    public async Task LoadAndMaterialize_DataclassCreatesTypeAndGeneratedInit()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("dataclass_example.py", ReadFixture("dataclass_example.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var typeNode = records.Nodes.Single(n => n.Kind == "py.type" && GetString(n, "name") == "Point");
        GetString(typeNode, "type_kind").Should().Be("dataclass");

        var generatedInit = records.Nodes.Single(n =>
            n.Kind == "py.member"
            && GetString(n, "declaring_type") == "Point"
            && GetString(n, "name") == "__init__"
            && GetBool(n, "is_generated"));

        GetString(generatedInit, "generator").Should().Be("dataclass");
    }

    [Test]
    public async Task LoadAndMaterialize_EnumCreatesTypeAndDocumentConstants()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("enum_example.py", ReadFixture("enum_example.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var enumNode = records.Nodes.Single(n => n.Kind == "py.type" && GetString(n, "name") == "Status");
        GetString(enumNode, "type_kind").Should().Be("enum");

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        var constants = GetArray(documentNode, "constants");
        constants.Should().Contain(c => c!["name"]!.GetValue<string>() == "READY");
        constants.Should().Contain(c => c!["name"]!.GetValue<string>() == "BUSY");
    }

    [Test]
    public async Task LoadAndMaterialize_ProtocolTypeKindIsProtocol()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("protocol_example.py", ReadFixture("protocol_example.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var protocolNode = records.Nodes.Single(n => n.Kind == "py.type" && GetString(n, "name") == "Greeter");
        GetString(protocolNode, "type_kind").Should().Be("protocol");
    }

    [Test]
    public async Task LoadAndMaterialize_ImportEdgesHaveExpectedProperties()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("imports_relative.py", ReadFixture("imports_relative.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var importEdges = records.Edges.Where(e => e.Type == "IMPORTS").ToArray();
        importEdges.Should().HaveCount(3);

        importEdges.Should().Contain(e =>
            GetEdgeString(e, "specifier") == "." &&
            GetEdgeString(e, "names") == "utils" &&
            GetEdgeBool(e, "is_relative") &&
            GetEdgeInt(e, "relative_level") == 1);

        importEdges.Should().Contain(e =>
            GetEdgeString(e, "specifier") == "..core" &&
            GetEdgeString(e, "names") == "Base" &&
            GetEdgeBool(e, "is_relative") &&
            GetEdgeInt(e, "relative_level") == 2);

        importEdges.Should().Contain(e =>
            GetEdgeString(e, "specifier") == "...pkg.mod" &&
            GetEdgeString(e, "names") == "Name:AliasName" &&
            GetEdgeBool(e, "is_relative") &&
            GetEdgeInt(e, "relative_level") == 3);
    }

    [Test]
    public async Task LoadAndMaterialize_ExtendsEdgesUseOrdinals()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("simple_class.py", ReadFixture("simple_class.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var extendsEdges = records.Edges.Where(e => e.Type == "EXTENDS").ToArray();
        extendsEdges.Should().HaveCount(2);

        extendsEdges.Should().Contain(e =>
            e.DstId == null &&
            e.IsComposition == false &&
            GetEdgeString(e, "target") == "BaseUser" &&
            GetEdgeInt(e, "ordinal") == 0);

        extendsEdges.Should().Contain(e =>
            e.DstId == null &&
            e.IsComposition == false &&
            GetEdgeString(e, "target") == "Trackable" &&
            GetEdgeInt(e, "ordinal") == 1);
    }

    [Test]
    public async Task LoadAndMaterialize_VisibilityMappedByNamingConvention()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("visibility_conventions.py", ReadFixture("visibility_conventions.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var publicMember = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "public");
        var privateMember = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "_private");
        var mangledMember = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "__mangled");
        var dunderMember = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "__dunder__");

        GetString(publicMember, "accessibility").Should().Be("public");
        GetString(privateMember, "accessibility").Should().Be("private");
        GetString(mangledMember, "accessibility").Should().Be("private");
        GetString(dunderMember, "accessibility").Should().Be("public");
    }

    [Test]
    public async Task LoadAndMaterialize_DecoratorsMapToSemanticProperties()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("decorators.py", ReadFixture("decorators.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var propertyMember = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "prop");
        var staticMember = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "static");
        var classMember = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "from_value");
        var abstractMember = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "abstract");

        GetString(propertyMember, "kind").Should().Be("property");
        GetString(staticMember, "kind").Should().Be("method");
        GetBool(staticMember, "is_static").Should().BeTrue();
        GetString(classMember, "kind").Should().Be("method");
        GetBool(classMember, "is_static").Should().BeTrue();
        GetBool(classMember, "is_classmethod").Should().BeTrue();
        GetBool(abstractMember, "is_abstract").Should().BeTrue();

        var pickDecorators = records.Nodes
            .Where(n => n.Kind == "py.function" && GetString(n, "name") == "pick")
            .Select(n => GetArray(n, "decorators"))
            .First(arr => arr.Any(x => x!.GetValue<string>() == "override"));

        pickDecorators.Should().Contain(x => x!.GetValue<string>() == "custom.decorator");
    }

    [Test]
    public async Task LoadAndMaterialize_AsyncAndGeneratorPropertiesSet()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("async_functions.py", ReadFixture("async_functions.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var stream = records.Nodes.Single(n => n.Kind == "py.function" && GetString(n, "name") == "stream");
        GetBool(stream, "is_async").Should().BeTrue();
        GetBool(stream, "is_generator").Should().BeTrue();
        GetBool(stream, "uses_async_with").Should().BeTrue();
        GetBool(stream, "uses_async_for").Should().BeTrue();

        var regular = records.Nodes.Single(n => n.Kind == "py.function" && GetString(n, "name") == "regular");
        GetBool(regular, "is_async").Should().BeTrue();
        GetBool(regular, "is_generator").Should().BeFalse();
    }

    [Test]
    public async Task LoadAndMaterialize_XrayHeadlineFollowsFormat()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("simple_class.py", ReadFixture("simple_class.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().ContainSingle();
        var headline = records.Artifacts[0].Headline!;
        Regex.IsMatch(headline, @"^simple_class\.py \| .+ \| .+ \| \d+ ln, ~\d+ tok$").Should().BeTrue();
    }

    [Test]
    public async Task LoadAndMaterialize_XrayStructureIncludesVisibilityAndAnchors()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("visibility_conventions.py", ReadFixture("visibility_conventions.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var structure = records.Artifacts[0].Structure!;
        structure.Should().Contain("#symbol=Visibility.public");
        structure.Should().Contain("#symbol=Visibility.__mangled");
        structure.Should().Contain("-__mangled(");
    }

    [Test]
    public async Task LoadAndMaterialize_InitFileGetsPackageRole()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("__init__.py", "__all__ = [\"User\"]");

        (await loader.CanLoadAsync(artifactScope.Artifact)).Should().BeTrue();
        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        GetString(documentNode, "role").Should().Be("package_init");
    }

    [Test]
    public async Task LoadAndMaterialize_StubFileGetsStubRole()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("service.pyi", "class Service: ...");

        (await loader.CanLoadAsync(artifactScope.Artifact)).Should().BeTrue();
        artifactScope.Artifact.MediaType!.Kind.Should().Be("code.python.stub");

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        GetString(documentNode, "role").Should().Be("stub");
    }

    [Test]
    public async Task LoadAndMaterialize_DocumentConstantsJsonPopulated()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("constants.py", ReadFixture("constants.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        var constants = GetArray(documentNode, "constants");

        constants.Should().Contain(c =>
            c!["name"]!.GetValue<string>() == "MAX_SIZE"
            && c["is_final"]!.GetValue<bool>()
            && c["value_preview"]!.GetValue<string>() == "10");
    }

    [Test]
    public async Task LoadAndMaterialize_DocumentTypeAliasesJsonPopulated()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("type_aliases.py", ReadFixture("type_aliases.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        var aliases = GetArray(documentNode, "type_aliases");

        aliases.Should().Contain(a => a!["name"]!.GetValue<string>() == "UserId");
        aliases.Should().Contain(a => a!["name"]!.GetValue<string>() == "JsonDict");
    }

    [Test]
    public async Task LoadAndMaterialize_TypeVariablesJsonPopulated()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("simple_class.py", ReadFixture("simple_class.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var userType = records.Nodes.Single(n => n.Kind == "py.type" && GetString(n, "name") == "User");
        var variables = GetArray(userType, "variables");

        variables.Should().Contain(v =>
            v!["name"]!.GetValue<string>() == "level"
            && v["variable_kind"]!.GetValue<string>() == "class");
        variables.Should().Contain(v =>
            v!["name"]!.GetValue<string>() == "active"
            && v["variable_kind"]!.GetValue<string>() == "instance");
    }

    [Test]
    public async Task LoadAndMaterialize_TypeCheckingImportsMarked()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("imports_type_checking.py", ReadFixture("imports_type_checking.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var imports = records.Edges.Where(e => e.Type == "IMPORTS").ToArray();
        imports.Count(e => GetEdgeBool(e, "is_type_checking_only")).Should().Be(2);
        imports.Count(e => !GetEdgeBool(e, "is_type_checking_only")).Should().Be(2);
    }

    [Test]
    public async Task LoadAndMaterialize_DocstringsStoredOnNodes()
    {
        using var loader = new PythonLoader();
        using var artifactScope = CreateArtifact("docstrings.py", ReadFixture("docstrings.py"));

        var document = await loader.LoadAsync(artifactScope.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        var typeNode = records.Nodes.Single(n => n.Kind == "py.type" && GetString(n, "name") == "Documented");
        var methodNode = records.Nodes.Single(n => n.Kind == "py.member" && GetString(n, "name") == "run");

        GetString(documentNode, "docstring").Should().Be("Module docs.");
        GetString(typeNode, "docstring").Should().Be("Class docs.");
        GetString(methodNode, "docstring").Should().Be("Method docs.");
    }

    private static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    private static string GetString(Node node, string key)
        => node.Props[key]!.GetValue<string>();

    private static bool GetBool(Node node, string key)
        => node.Props[key]!.GetValue<bool>();

    private static JsonArray GetArray(Node node, string key)
        => node.Props[key] as JsonArray ?? throw new InvalidOperationException($"Missing array property '{key}'.");

    private static string GetEdgeString(Edge edge, string key)
        => edge.Props[key]!.GetValue<string>();

    private static bool GetEdgeBool(Edge edge, string key)
        => edge.Props[key]!.GetValue<bool>();

    private static int GetEdgeInt(Edge edge, string key)
        => edge.Props[key]!.GetValue<int>();

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_python_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);
        return new ArtifactScope(
            new DiscoveredArtifact
            {
                File = provider.GetFileInfo(fileName),
                RepoUri = RepoUri.Parse($"file:///{fileName.Replace('\\', '/')}")
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
