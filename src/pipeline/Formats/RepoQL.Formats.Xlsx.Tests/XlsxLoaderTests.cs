using AwesomeAssertions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.Formats.Xlsx.Tests;

public sealed class XlsxLoaderTests
{
    [Test]
    [DisplayName("Recognizes .xlsx extension")]
    public async Task CanLoadAsync_RecognizesXlsxExtension()
    {
        var loader = new XlsxLoader();
        var artifact = CreateFakeArtifact("test.xlsx");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType!.Kind.Should().Be("xlsx.workbook");
    }

    [Test]
    [DisplayName("Rejects non-xlsx files")]
    public async Task CanLoadAsync_RejectsNonXlsxFiles()
    {
        var loader = new XlsxLoader();
        var artifact = CreateFakeArtifact("test.csv");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeFalse();
    }

    [Test]
    [DisplayName("Loads XLSX file and extracts worksheets")]
    public async Task LoadAsync_ExtractsWorksheets()
    {
        using var testFile = CreateSimpleTestFile();
        var loader = new XlsxLoader();

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        await loader.CanLoadAsync(artifact.Artifact);

        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        // Verify basic structure
        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().NotBeEmpty();

        // Should have a document node
        var docNode = records.Nodes.FirstOrDefault(n => n.Kind == "document");
        docNode.Should().NotBeNull();

        // Should have at least one worksheet node
        var worksheetNodes = records.Nodes.Where(n => n.Kind == "xlsx_worksheet").ToList();
        worksheetNodes.Should().NotBeEmpty("XLSX file should have at least one worksheet");

        // Verify artifact has x-ray fields
        var artifact1 = records.Artifacts[0];
        artifact1.Headline.Should().NotBeNullOrWhiteSpace("should have headline");
        artifact1.Summary.Should().NotBeNullOrWhiteSpace("should have summary");
        artifact1.Structure.Should().NotBeNullOrWhiteSpace("should have structure");
    }

    [Test]
    [DisplayName("Worksheet nodes have correct properties")]
    public async Task LoadAsync_WorksheetNodesHaveCorrectProperties()
    {
        using var testFile = CreateSimpleTestFile();
        var loader = new XlsxLoader();

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        await loader.CanLoadAsync(artifact.Artifact);

        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var worksheetNodes = records.Nodes.Where(n => n.Kind == "xlsx_worksheet").ToList();
        worksheetNodes.Should().NotBeEmpty();

        foreach (var ws in worksheetNodes)
        {
            ws.Props.Should().NotBeNull();
            ws.Props!.ContainsKey("name").Should().BeTrue();
            ws.Props.ContainsKey("row_count").Should().BeTrue();
            ws.Props.ContainsKey("column_count").Should().BeTrue();
        }
    }

    [Test]
    [DisplayName("Edges connect document to worksheets")]
    public async Task LoadAsync_CreatesHasPartEdges()
    {
        using var testFile = CreateSimpleTestFile();
        var loader = new XlsxLoader();

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        await loader.CanLoadAsync(artifact.Artifact);

        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.First(n => n.Kind == "document");
        var worksheetNodes = records.Nodes.Where(n => n.Kind == "xlsx_worksheet").ToList();

        // Each worksheet should have a HAS_PART edge from the document
        foreach (var ws in worksheetNodes)
        {
            var hasPartEdge = records.Edges.FirstOrDefault(e =>
                e.SrcId == docNode.Id && e.DstId == ws.Id && e.Type == "HAS_PART");

            hasPartEdge.Should().NotBeNull($"worksheet {ws.Id} should have HAS_PART edge from document");
            hasPartEdge!.IsComposition.Should().BeTrue();
        }
    }

    [Test]
    [DisplayName("Detects header row in worksheet")]
    public async Task LoadAsync_DetectsHeaderRow()
    {
        using var testFile = CreateFileWithHeaders();
        var loader = new XlsxLoader();

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        await loader.CanLoadAsync(artifact.Artifact);

        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var worksheetNode = records.Nodes.First(n => n.Kind == "xlsx_worksheet");

        worksheetNode.Props!["has_header_row"]?.GetValue<bool>().Should().BeTrue();
    }

    private static TestFileScope CreateSimpleTestFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"xlsx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "test.xlsx");

        using (var spreadsheet = SpreadsheetDocument.Create(filePath, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = spreadsheet.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData());

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Sheet1"
            });

            // Add some data
            var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>()!;
            var row = new Row { RowIndex = 1 };
            row.Append(new Cell { CellReference = "A1", CellValue = new CellValue("Hello"), DataType = CellValues.String });
            row.Append(new Cell { CellReference = "B1", CellValue = new CellValue("World"), DataType = CellValues.String });
            sheetData.Append(row);

            var row2 = new Row { RowIndex = 2 };
            row2.Append(new Cell { CellReference = "A2", CellValue = new CellValue("100") });
            row2.Append(new Cell { CellReference = "B2", CellValue = new CellValue("200") });
            sheetData.Append(row2);

            workbookPart.Workbook.Save();
        }

        return new TestFileScope(filePath, tempDir);
    }

    private static TestFileScope CreateFileWithHeaders()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"xlsx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "headers.xlsx");

        using (var spreadsheet = SpreadsheetDocument.Create(filePath, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = spreadsheet.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData());

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Expenses"
            });

            // Add header row with text values
            var sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>()!;

            var headerRow = new Row { RowIndex = 1 };
            headerRow.Append(new Cell { CellReference = "A1", CellValue = new CellValue("Date"), DataType = CellValues.String });
            headerRow.Append(new Cell { CellReference = "B1", CellValue = new CellValue("Description"), DataType = CellValues.String });
            headerRow.Append(new Cell { CellReference = "C1", CellValue = new CellValue("Amount"), DataType = CellValues.String });
            sheetData.Append(headerRow);

            // Add data rows with numbers
            for (int i = 2; i <= 5; i++)
            {
                var dataRow = new Row { RowIndex = (uint)i };
                dataRow.Append(new Cell { CellReference = $"A{i}", CellValue = new CellValue($"2024-01-0{i}"), DataType = CellValues.String });
                dataRow.Append(new Cell { CellReference = $"B{i}", CellValue = new CellValue($"Expense {i}"), DataType = CellValues.String });
                dataRow.Append(new Cell { CellReference = $"C{i}", CellValue = new CellValue((i * 100).ToString()) });
                sheetData.Append(dataRow);
            }

            workbookPart.Workbook.Save();
        }

        return new TestFileScope(filePath, tempDir);
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
        public TestFileScope(string filePath, string tempDir)
        {
            FilePath = filePath;
            TempDir = tempDir;
        }

        public string FilePath { get; }
        public string TempDir { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, true);
            }
            catch
            {
                // Ignore cleanup failures
            }
        }
    }

    private sealed class ArtifactScope : IDisposable
    {
        public ArtifactScope(DiscoveredArtifact artifact, IFileProvider provider)
        {
            Artifact = artifact;
            Provider = provider;
        }

        public DiscoveredArtifact Artifact { get; }
        public IFileProvider Provider { get; }

        public void Dispose()
        {
            if (Provider is IDisposable disposable)
                disposable.Dispose();
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

        public Stream CreateReadStream() =>
            throw new NotSupportedException("Fake file info does not support reading");
    }
}
