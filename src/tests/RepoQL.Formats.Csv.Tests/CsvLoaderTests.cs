using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.Formats.Csv.Tests;

public sealed class CsvLoaderTests
{
    [Test]
    [DisplayName("CanLoadAsync recognizes .csv extension")]
    public async Task CanLoadAsync_RecognizesCsvExtension()
    {
        var loader = new CsvLoader();
        var artifact = CreateFakeArtifact("test.csv");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType!.Kind.Should().Be("csv.table");
    }

    [Test]
    [DisplayName("CanLoadAsync recognizes .tsv extension")]
    public async Task CanLoadAsync_RecognizesTsvExtension()
    {
        var loader = new CsvLoader();
        var artifact = CreateFakeArtifact("test.tsv");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType!.Kind.Should().Be("tsv.table");
    }

    [Test]
    [DisplayName("CanLoadAsync recognizes .psv extension")]
    public async Task CanLoadAsync_RecognizesPsvExtension()
    {
        var loader = new CsvLoader();
        var artifact = CreateFakeArtifact("test.psv");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType!.Kind.Should().Be("data.psv");
    }

    [Test]
    [DisplayName("CanLoadAsync rejects non-CSV formats")]
    public async Task CanLoadAsync_RejectsNonCsvFiles()
    {
        var loader = new CsvLoader();
        var artifact = CreateFakeArtifact("test.xlsx");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeFalse();
    }

    [Test]
    [DisplayName("Supports returns true for csv.table kind")]
    public void Supports_ReturnsTrueForCsvKind()
    {
        var loader = new CsvLoader();
        var mediaType = SemanticMediaType.Create("text", "csv").WithKind("csv.table");

        loader.Supports(mediaType).Should().BeTrue();
    }

    [Test]
    [DisplayName("Supports returns true for tsv.table kind")]
    public void Supports_ReturnsTrueForTsvKind()
    {
        var loader = new CsvLoader();
        var mediaType = SemanticMediaType.Create("text", "tab-separated-values").WithKind("tsv.table");

        loader.Supports(mediaType).Should().BeTrue();
    }

    [Test]
    [DisplayName("Supports returns false for unrelated kind")]
    public void Supports_ReturnsFalseForUnrelatedKind()
    {
        var loader = new CsvLoader();
        var mediaType = SemanticMediaType.Create("application", "json").WithKind("json.document");

        loader.Supports(mediaType).Should().BeFalse();
    }

    [Test]
    [DisplayName("Loads CSV with headers and creates graph records")]
    public async Task LoadAsync_LoadsCsvWithHeadersAndCreatesGraph()
    {
        const string content = "name,age,city\nAlice,30,NYC\nBob,25,LA\nCharlie,35,SF";
        using var testFile = new TestFileScope(content, "people.csv");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new CsvLoader();

        (await loader.CanLoadAsync(artifact.Artifact)).Should().BeTrue();
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().ContainSingle(n => n.Kind == "document");

        var columnNodes = records.Nodes.Where(n => n.Kind == "csv_column").ToList();
        columnNodes.Should().HaveCount(3);

        var hasPartEdges = records.Edges.Where(e => e.Type == "HAS_PART").ToList();
        hasPartEdges.Should().HaveCount(3);
        hasPartEdges.All(e => e.IsComposition).Should().BeTrue();

        var outputArtifact = records.Artifacts[0];
        outputArtifact.Headline.Should().NotBeNullOrWhiteSpace();
        outputArtifact.Summary.Should().NotBeNullOrWhiteSpace();
        outputArtifact.Structure.Should().NotBeNullOrWhiteSpace();
        outputArtifact.Text.Should().Be(content);
    }

    [Test]
    [DisplayName("Loads TSV file correctly")]
    public async Task LoadAsync_LoadsTsvWithCorrectMediaKind()
    {
        const string content = "name\tage\tcity\nAlice\t30\tNYC\nBob\t25\tLA";
        using var testFile = new TestFileScope(content, "people.tsv");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new CsvLoader();

        (await loader.CanLoadAsync(artifact.Artifact)).Should().BeTrue();
        artifact.Artifact.MediaType!.Kind.Should().Be("tsv.table");

        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        document.MediaType.Kind.Should().Be("tsv.table");
        records.Nodes.Should().ContainSingle(n => n.Kind == "document");
        records.Nodes.Count(n => n.Kind == "csv_column").Should().Be(3);
    }

    [Test]
    [DisplayName("Handles CSV without headers using synthetic column names")]
    public async Task LoadAsync_UsesSyntheticColumnNamesWhenHeaderMissing()
    {
        const string content = "1,Alice,30\n2,Bob,25\n3,Charlie,35";
        using var testFile = new TestFileScope(content, "headerless.csv");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new CsvLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["has_header"]!.GetValue<bool>().Should().BeFalse();

        var columnNames = records.Nodes
            .Where(n => n.Kind == "csv_column")
            .OrderBy(n => n.Props["index"]!.GetValue<int>())
            .Select(n => n.Props["name"]!.GetValue<string>())
            .ToList();

        columnNames.Should().Equal(["column_1", "column_2", "column_3"]);
    }

    [Test]
    [DisplayName("Handles empty file gracefully")]
    public async Task LoadAsync_HandlesEmptyFileGracefully()
    {
        using var testFile = new TestFileScope(string.Empty, "empty.csv");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new CsvLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().ContainSingle(n => n.Kind == "document");
        records.Nodes.Count(n => n.Kind == "csv_column").Should().Be(0);
        records.Edges.Should().BeEmpty();
        records.Spans.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Handles header-only file with zero data rows")]
    public async Task LoadAsync_HandlesHeaderOnlyFileWithZeroRows()
    {
        const string content = "name,age,city";
        using var testFile = new TestFileScope(content, "header_only.csv");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new CsvLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["row_count"]!.GetValue<int>().Should().Be(0);
    }

    [Test]
    [DisplayName("Column nodes include expected properties and inferred types")]
    public async Task LoadAsync_ColumnNodesHaveExpectedProperties()
    {
        const string content = "id,name,score,active,start_date\n1,Alice,98.5,true,2024-01-01\n2,Bob,88.0,false,2024-02-10";
        using var testFile = new TestFileScope(content, "typed.csv");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new CsvLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var columns = records.Nodes
            .Where(n => n.Kind == "csv_column")
            .ToDictionary(
                n => n.Props["name"]!.GetValue<string>(),
                n => n);

        columns.Should().ContainKeys("id", "name", "score", "active", "start_date");

        foreach (var node in columns.Values)
        {
            node.Props.ContainsKey("name").Should().BeTrue();
            node.Props.ContainsKey("index").Should().BeTrue();
            node.Props.ContainsKey("type").Should().BeTrue();
            node.Props.ContainsKey("estimated_tokens").Should().BeTrue();
            node.Props["estimated_tokens"]!.GetValue<int>().Should().BeGreaterThan(0);
        }

        columns["id"].Props["type"]!.GetValue<string>().Should().Be("integer");
        columns["name"].Props["type"]!.GetValue<string>().Should().Be("varchar");
        columns["score"].Props["type"]!.GetValue<string>().Should().Be("float");
        columns["active"].Props["type"]!.GetValue<string>().Should().Be("boolean");
        columns["start_date"].Props["type"]!.GetValue<string>().Should().Be("date");
    }

    [Test]
    [DisplayName("Headline contains file name, row count, and column count")]
    public async Task Materialize_HeadlineContainsKeyInfo()
    {
        const string content = "name,age,city\nAlice,30,NYC\nBob,25,LA";
        using var testFile = new TestFileScope(content, "headline.csv");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new CsvLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var headline = records.Artifacts[0].Headline;
        headline.Should().NotBeNullOrWhiteSpace();
        headline.Should().Contain("headline.csv");
        headline.Should().Contain("2 rows");
        headline.Should().Contain("3 cols");
    }

    [Test]
    [DisplayName("Structure contains per-column token cost")]
    public async Task Materialize_StructureContainsColumnTokenEstimates()
    {
        const string content = "name,age,city\nAlice,30,NYC\nBob,25,LA";
        using var testFile = new TestFileScope(content, "structure.csv");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new CsvLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var structure = records.Artifacts[0].Structure;
        structure.Should().NotBeNullOrWhiteSpace();
        structure.Should().Contain("name");
        structure.Should().Contain("age");
        structure.Should().Contain("city");
        structure.Should().Contain("tok");
    }

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
            _tempDir = Path.Combine(Path.GetTempPath(), $"csv_test_{Guid.NewGuid():N}");
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
        public PhysicalFileProvider Provider { get; }

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
