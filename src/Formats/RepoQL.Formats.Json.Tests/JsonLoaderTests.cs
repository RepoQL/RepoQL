using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.Formats.Json.Tests;

public sealed class JsonLoaderTests
{
    [Test]
    [Arguments("data.json")]
    [Arguments("data.jsonc")]
    [Arguments("events.jsonl")]
    [Arguments("events.ndjson")]
    [Arguments("DATA.JSON")]
    [Arguments("DATA.JSONC")]
    [DisplayName("CanLoadAsync accepts supported JSON extensions")]
    public async Task CanLoadAsync_AcceptsSupportedExtensions(string fileName)
    {
        var loader = new JsonLoader(new JsonStructureParser());
        var artifact = CreateFakeArtifact(fileName);

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType.Should().NotBeNull();
        artifact.MediaType!.Kind.Should().Be("json");
    }

    [Test]
    [Arguments("data.json5")]
    [Arguments("DATA.JSON5")]
    [DisplayName("CanLoadAsync rejects JSON variants not yet supported")]
    public async Task CanLoadAsync_RejectsUnsupportedVariants(string fileName)
    {
        var loader = new JsonLoader(new JsonStructureParser());
        var artifact = CreateFakeArtifact(fileName);

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeFalse();
    }

    [Test]
    [Arguments("appsettings.json")]
    [Arguments("appsettings.Development.json")]
    [Arguments("launchSettings.json")]
    [Arguments("APPSETTINGS.Production.JSON")]
    [DisplayName("CanLoadAsync rejects appsettings and launch settings files")]
    public async Task CanLoadAsync_RejectsExcludedConfigurationFiles(string fileName)
    {
        var loader = new JsonLoader(new JsonStructureParser());
        var artifact = CreateFakeArtifact(fileName);

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeFalse();
    }

    [Test]
    [DisplayName("LoadAsync parses .jsonc files to the same key tree as strict JSON")]
    public async Task LoadAsync_Jsonc_ProducesSameKeyTreeAsStrictJson()
    {
        const string strictJson = """
        {
          "name": "repoql",
          
          "settings": {
            
            
               
            "enabled": true
          }
        }
        """;

        const string jsonc = """
        {
          "name": "repoql",
          // top-level comment
          "settings": {
            /*
               block comment
            */
            "enabled": true
          }
        }
        """;

        using var strictFile = new TestFileScope(strictJson, "strict.json");
        using var jsoncFile = new TestFileScope(jsonc, "config.jsonc");
        using var strictArtifact = CreateArtifactFromFile(strictFile.FilePath);
        using var jsoncArtifact = CreateArtifactFromFile(jsoncFile.FilePath);

        var loader = new JsonLoader(new JsonStructureParser());

        (await loader.CanLoadAsync(strictArtifact.Artifact)).Should().BeTrue();
        (await loader.CanLoadAsync(jsoncArtifact.Artifact)).Should().BeTrue();

        var strictDocument = await loader.LoadAsync(strictArtifact.Artifact);
        var jsoncDocument = await loader.LoadAsync(jsoncArtifact.Artifact);

        jsoncDocument.Text.Should().Be(jsonc);

        var strictResult = strictDocument.GetMetadataOrDefault<JsonParseResult>(JsonLoader.StateMetadataKey);
        var jsoncResult = jsoncDocument.GetMetadataOrDefault<JsonParseResult>(JsonLoader.StateMetadataKey);

        strictResult.Should().NotBeNull();
        jsoncResult.Should().NotBeNull();

        AssertEquivalentKeyTree(jsoncResult!, strictResult!);
    }

    [Test]
    [DisplayName("LoadAsync recovers .json files with comments via normalization fallback")]
    public async Task LoadAsync_JsonWithComments_UsesFallbackAndMatchesStrictJsonTree()
    {
        const string strictJson = """
        {
          "name": "repoql",            
                                   
          "settings": {
            "enabled": true
          }
        }
        """;

        const string commentedJson = """
        {
          "name": "repoql", // inline comment
          /* block comment */
          "settings": {
            "enabled": true
          }
        }
        """;

        var parser = new JsonStructureParser();
        Action strictParse = () => parser.Parse(commentedJson);
        strictParse.Should().Throw<JsonException>();

        using var strictFile = new TestFileScope(strictJson, "strict-fallback.json");
        using var commentedFile = new TestFileScope(commentedJson, "settings.json");
        using var strictArtifact = CreateArtifactFromFile(strictFile.FilePath);
        using var commentedArtifact = CreateArtifactFromFile(commentedFile.FilePath);

        var loader = new JsonLoader(parser);

        (await loader.CanLoadAsync(commentedArtifact.Artifact)).Should().BeTrue();

        var strictDocument = await loader.LoadAsync(strictArtifact.Artifact);
        var recoveredDocument = await loader.LoadAsync(commentedArtifact.Artifact);

        recoveredDocument.Text.Should().Be(commentedJson);

        var strictResult = strictDocument.GetMetadataOrDefault<JsonParseResult>(JsonLoader.StateMetadataKey);
        var recoveredResult = recoveredDocument.GetMetadataOrDefault<JsonParseResult>(JsonLoader.StateMetadataKey);

        strictResult.Should().NotBeNull();
        recoveredResult.Should().NotBeNull();

        AssertEquivalentKeyTree(recoveredResult!, strictResult!);
    }

    [Test]
    [DisplayName("Materialize builds document and json_key nodes for flat objects")]
    public async Task Materialize_FlatObject_ProducesExpectedGraphAndTemplates()
    {
        const string json = """
        {
          "name": "repoql",
          "version": 1,
          "enabled": true
        }
        """;

        using var testFile = new TestFileScope(json, "flat.json");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);

        var loader = new JsonLoader(new JsonStructureParser());
        (await loader.CanLoadAsync(artifact.Artifact)).Should().BeTrue();

        var document = await loader.LoadAsync(artifact.Artifact);
        document.GetMetadataOrDefault<JsonParseResult>(JsonLoader.StateMetadataKey).Should().NotBeNull();

        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["shape"]!.GetValue<string>().Should().Be("object");
        documentNode.Props["key_count"]!.GetValue<int>().Should().Be(3);
        documentNode.Props["max_depth"]!.GetValue<int>().Should().Be(0);

        var keyNodes = records.Nodes.Where(n => n.Kind == "json_key").ToList();
        keyNodes.Should().HaveCount(3);
        keyNodes.Select(n => n.Props["path"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(["/name", "/version", "/enabled"]);

        var nameNode = keyNodes.Single(n => n.Props["path"]!.GetValue<string>() == "/name");
        nameNode.Props["name"]!.GetValue<string>().Should().Be("name");
        nameNode.Props["depth"]!.GetValue<int>().Should().Be(0);
        nameNode.Props["value_kind"]!.GetValue<string>().Should().Be("string");
        nameNode.Props["scalar_value"]!.GetValue<string>().Should().Be("repoql");
        nameNode.Props["estimated_tokens"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(0);

        records.Spans.Should().HaveCount(3);
        records.Spans.Should().OnlyContain(s => s.StartLine >= 2 && s.EndLine >= s.StartLine);

        records.Edges.Should().HaveCount(3);
        records.Edges.Should().OnlyContain(e => e.Type == "HAS_PART" && e.IsComposition);

        var outputArtifact = records.Artifacts[0];
        outputArtifact.Headline.Should().Contain("flat.json");
        outputArtifact.Headline.Should().Contain("json");
        outputArtifact.Headline.Should().Contain("object");
        outputArtifact.Structure.Should().Contain("#/name");
        outputArtifact.Structure.Should().Contain("#/version");
        outputArtifact.Summary.Should().Contain("object | 3 keys | max depth 0");
        outputArtifact.TokenCount.Should().BeGreaterThan(0);
    }

    private static void AssertEquivalentKeyTree(JsonParseResult actual, JsonParseResult expected)
    {
        actual.Shape.Should().Be(expected.Shape);
        actual.TotalKeyCount.Should().Be(expected.TotalKeyCount);
        actual.MaxDepth.Should().Be(expected.MaxDepth);
        actual.ArrayLength.Should().Be(expected.ArrayLength);

        var actualKeys = actual.Keys.Select(ToComparableKey).ToList();
        var expectedKeys = expected.Keys.Select(ToComparableKey).ToList();

        actualKeys.Should().BeEquivalentTo(expectedKeys, options => options.WithStrictOrdering());
    }

    private static object ToComparableKey(JsonKeyInfo key)
        => new
        {
            key.Path,
            key.Name,
            key.Depth,
            key.ValueKind,
            key.StartLine,
            key.EndLine,
            key.ArrayLength,
            key.ScalarValue,
            key.IsNodeEligible
        };

    private static DiscoveredArtifact CreateFakeArtifact(string fileName)
    {
        return new DiscoveredArtifact
        {
            File = new FakeFileInfo(fileName),
            RepoUri = RepoUri.Parse($"file:///{fileName}")
        };
    }

    private static ArtifactScope CreateArtifactFromFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var fileName = Path.GetFileName(filePath);
        var provider = new PhysicalFileProvider(dir);

        var artifact = new DiscoveredArtifact
        {
            File = provider.GetFileInfo(fileName),
            RepoUri = RepoUri.Parse($"file:///{fileName}")
        };

        return new ArtifactScope(artifact, provider);
    }

    private sealed class TestFileScope : IDisposable
    {
        public string FilePath { get; }
        private readonly string _tempDir;

        public TestFileScope(string content, string fileName)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"json_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            FilePath = Path.Combine(_tempDir, fileName);
            File.WriteAllText(FilePath, content);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }
    }

    private sealed class ArtifactScope : IDisposable
    {
        public ArtifactScope(DiscoveredArtifact artifact, PhysicalFileProvider provider)
        {
            Artifact = artifact;
            Provider = provider;
        }

        public DiscoveredArtifact Artifact { get; }

        private PhysicalFileProvider Provider { get; }

        public void Dispose()
        {
            Provider.Dispose();
        }
    }

    private sealed class FakeFileInfo : IFileInfo
    {
        public FakeFileInfo(string name) => Name = name;

        public bool Exists => true;
        public long Length => 0;
        public string? PhysicalPath => null;
        public string Name { get; }
        public DateTimeOffset LastModified => DateTimeOffset.Now;
        public bool IsDirectory => false;

        public Stream CreateReadStream()
            => throw new NotSupportedException("Fake file info does not support reading");
    }
}

