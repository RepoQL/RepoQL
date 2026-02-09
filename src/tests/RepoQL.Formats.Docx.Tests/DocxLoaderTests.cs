using AwesomeAssertions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using A = DocumentFormat.OpenXml.Drawing;
using DocProps = DocumentFormat.OpenXml.CustomProperties;
using ExtendedProps = DocumentFormat.OpenXml.ExtendedProperties;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using VT = DocumentFormat.OpenXml.VariantTypes;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace RepoQL.Formats.Docx.Tests;

public sealed class DocxLoaderTests
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Test]
    [DisplayName("CanLoadAsync recognizes .docx extension")]
    public async Task CanLoadAsync_RecognizesDocxExtension()
    {
        var loader = new DocxLoader();
        var artifact = CreateFakeArtifact("sample.docx");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType!.Kind.Should().Be("docx.document");
    }

    [Test]
    [DisplayName("CanLoadAsync recognizes .docm extension")]
    public async Task CanLoadAsync_RecognizesDocmExtension()
    {
        var loader = new DocxLoader();
        var artifact = CreateFakeArtifact("sample.docm");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType!.Kind.Should().Be("docx.document");
    }

    [Test]
    [DisplayName("CanLoadAsync recognizes .dotx extension")]
    public async Task CanLoadAsync_RecognizesDotxExtension()
    {
        var loader = new DocxLoader();
        var artifact = CreateFakeArtifact("sample.dotx");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType!.Kind.Should().Be("docx.template");
    }

    [Test]
    [DisplayName("CanLoadAsync rejects non-docx files")]
    public async Task CanLoadAsync_RejectsNonDocxFiles()
    {
        var loader = new DocxLoader();
        var artifact = CreateFakeArtifact("sample.txt");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeFalse();
    }

    [Test]
    [DisplayName("Loads headings at multiple levels with section spans")]
    public async Task LoadAsync_ExtractsHeadingTreeAndSectionSpans()
    {
        using var testFile = CreateDocument("headings.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                CreateHeadingParagraph("Executive Summary", 1),
                new Paragraph(new Run(new Text("Overview paragraph."))),
                CreateHeadingParagraph("Scope", 2),
                new Paragraph(new Run(new Text("Scope details."))),
                CreateHeadingParagraph("Out of Scope", 3),
                new Paragraph(new Run(new Text("Exclusions.")))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        (await loader.CanLoadAsync(artifact.Artifact)).Should().BeTrue();
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().HaveCount(1);
        records.Nodes.Should().ContainSingle(n => n.Kind == "document");
        records.Nodes.Count(n => n.Kind == "docx_heading").Should().Be(3);

        var artifactRecord = records.Artifacts[0];
        artifactRecord.Text.Should().Contain("# Executive Summary");
        artifactRecord.Text.Should().Contain("## Scope");
        artifactRecord.Text.Should().Contain("### Out of Scope");

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        var headingEdges = records.Edges
            .Where(e => e.Type == "HAS_PART" && e.SrcId == documentNode.Id)
            .OrderBy(e => e.Ordinal)
            .ToList();

        headingEdges.Should().HaveCount(3);

        var spansById = records.Spans.ToDictionary(s => s.Id);
        var orderedHeadingNodes = headingEdges.Select(edge => records.Nodes.Single(n => n.Id == edge.DstId)).ToList();
        var levels = orderedHeadingNodes.Select(node => node.Props["level"]!.GetValue<int>()).ToList();
        levels.Should().Equal([1, 2, 3]);

        var firstSpan = spansById[orderedHeadingNodes[0].SpanId!.Value];
        var secondSpan = spansById[orderedHeadingNodes[1].SpanId!.Value];
        var thirdSpan = spansById[orderedHeadingNodes[2].SpanId!.Value];

        firstSpan.StartLine.Should().Be(1);
        firstSpan.EndLine.Should().Be(2);
        secondSpan.StartLine.Should().Be(3);
        secondSpan.EndLine.Should().Be(4);
        thirdSpan.StartLine.Should().Be(5);
        thirdSpan.EndLine.Should().Be(6);

        orderedHeadingNodes[0].Uri!.AbsoluteUri.Should().Contain("symbol=ExecutiveSummary");
    }

    [Test]
    [DisplayName("Handles document with no headings")]
    public async Task LoadAsync_DocumentWithNoHeadings()
    {
        using var testFile = CreateDocument("no_headings.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Line one"))),
                new Paragraph(new Run(new Text("Line two")))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Count(n => n.Kind == "docx_heading").Should().Be(0);
        records.Edges.Count(e => e.Type == "HAS_PART").Should().Be(0);
        records.Spans.Should().BeEmpty();
        records.Artifacts[0].Structure.Should().Contain("(no headings)");
    }

    [Test]
    [DisplayName("Detects bold paragraphs as heuristic headings when no heading styles exist")]
    public async Task LoadAsync_HeuristicHeadingsFromBoldFormatting()
    {
        static Paragraph BoldParagraph(string text, int halfPointSize = 24)
        {
            return new Paragraph(
                new Run(
                    new RunProperties(
                        new Bold(),
                        new FontSize { Val = halfPointSize.ToString() }),
                    new Text(text)));
        }

        using var testFile = CreateDocument("heuristic.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                BoldParagraph("Introduction", 32),
                new Paragraph(new Run(new Text("Body paragraph one."))),
                BoldParagraph("Background", 28),
                new Paragraph(new Run(new Text("Body paragraph two."))),
                BoldParagraph("Details", 28),
                new Paragraph(new Run(new Text("Body paragraph three.")))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Count(n => n.Kind == "docx_heading").Should().Be(3);

        var artifactRecord = records.Artifacts[0];
        artifactRecord.Text.Should().Contain("# Introduction");
        artifactRecord.Text.Should().Contain("## Background");
        artifactRecord.Text.Should().Contain("## Details");

        artifactRecord.Structure.Should().NotContain("(no headings)");
        artifactRecord.Structure.Should().Contain("Introduction");
        artifactRecord.Structure.Should().Contain("Background");

        var headingNodes = records.Nodes
            .Where(n => n.Kind == "docx_heading")
            .OrderBy(n => n.Props["paragraph_index"]!.GetValue<int>())
            .ToList();

        headingNodes[0].Props["level"]!.GetValue<int>().Should().Be(1);
        headingNodes[1].Props["level"]!.GetValue<int>().Should().Be(2);
        headingNodes[2].Props["level"]!.GetValue<int>().Should().Be(2);
    }

    [Test]
    [DisplayName("Handles tracked changes by including insertions and skipping deletions")]
    public async Task LoadAsync_TrackedChangesFinalState()
    {
        using var testFile = CreateDocument("tracked.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("Keep ")),
                    new InsertedRun(new Run(new Text("added")))
                    {
                        Id = "1",
                        Author = "Alice"
                    },
                    new DeletedRun(new Run(new DeletedText("removed")))
                    {
                        Id = "2",
                        Author = "Bob"
                    },
                    new Run(new Text(" text")))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var text = records.Artifacts[0].Text;
        text.Should().Contain("Keep added text");
        text.Should().NotContain("removed");

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["has_tracked_changes"]!.GetValue<bool>().Should().BeTrue();
        documentNode.Props["tracked_change_count"]!.GetValue<int>().Should().Be(2);

        var authors = documentNode.Props["tracked_change_authors"]!
            .AsArray()
            .Select(a => a!.GetValue<string>())
            .ToList();
        authors.Should().Contain("Alice");
        authors.Should().Contain("Bob");
    }

    [Test]
    [DisplayName("Resolves custom heading styles through BasedOn chain")]
    public async Task LoadAsync_ResolvesCustomHeadingStyleInheritance()
    {
        using var testFile = CreateDocument("custom_style.docx", mainPart =>
        {
            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(
                new Style
                {
                    Type = StyleValues.Paragraph,
                    StyleId = "CustomSection",
                    CustomStyle = true,
                    StyleName = new StyleName { Val = "Custom Section" },
                    BasedOn = new BasedOn { Val = "Heading2" }
                });

            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "CustomSection" }),
                    new Run(new Text("Custom Section Heading"))),
                new Paragraph(new Run(new Text("Body content")))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var headingNode = records.Nodes.Single(n => n.Kind == "docx_heading");
        headingNode.Props["level"]!.GetValue<int>().Should().Be(2);
        records.Artifacts[0].Text.Should().Contain("## Custom Section Heading");
    }

    [Test]
    [DisplayName("Handles empty document")]
    public async Task LoadAsync_EmptyDocument()
    {
        using var testFile = CreateDocument("empty.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body());
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Should().ContainSingle(n => n.Kind == "document");
        records.Nodes.Count(n => n.Kind == "docx_heading").Should().Be(0);
        records.Artifacts[0].Text.Should().Be(string.Empty);
        records.Edges.Should().BeEmpty();
        records.Spans.Should().BeEmpty();

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["paragraph_count"]!.GetValue<int>().Should().Be(0);
    }

    [Test]
    [DisplayName("Extracts simple table with styled header row")]
    public async Task LoadAsync_ExtractsSimpleTable()
    {
        using var testFile = CreateDocument("simple_table.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                CreateHeadingParagraph("Overview", 1),
                CreateTable(
                    [
                        CreateTableRow([CreateTextCell("Name"), CreateTextCell("Role"), CreateTextCell("Team")], isHeader: true),
                        CreateTableRow([CreateTextCell("Ari"), CreateTextCell("Author"), CreateTextCell("Docs")]),
                        CreateTableRow([CreateTextCell("Sam"), CreateTextCell("Reviewer"), CreateTextCell("API")])
                    ])));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var tableNode = records.Nodes.Single(n => n.Kind == "docx_table");
        tableNode.Props["row_count"]!.GetValue<int>().Should().Be(3);
        tableNode.Props["col_count"]!.GetValue<int>().Should().Be(3);
        tableNode.Props["has_header"]!.GetValue<bool>().Should().BeTrue();
        tableNode.Props["column_names"]!.AsArray().Select(v => v!.GetValue<string>())
            .Should().Equal(["Name", "Role", "Team"]);
        records.Artifacts[0].Text.Should().Contain("[Table: Name, Role, Team (3 cols x 3 rows)]");
    }

    [Test]
    [DisplayName("Tracks merged cell spans in table surface model")]
    public async Task LoadAsync_ExtractsMergedCells()
    {
        using var testFile = CreateDocument("merged_table.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                CreateTable(
                    [
                        CreateTableRow([CreateTextCell("Merged Header", gridSpan: 2), CreateTextCell("Right")], isHeader: true),
                        CreateTableRow([CreateTextCell("Vertical", vMerge: MergedCellValues.Restart), CreateTextCell("R2C2"), CreateTextCell("R2C3")]),
                        CreateTableRow([CreateTextCell(string.Empty, vMerge: MergedCellValues.Continue), CreateTextCell("R3C2"), CreateTextCell("R3C3")])
                    ])));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var tableNode = records.Nodes.Single(n => n.Kind == "docx_table");
        tableNode.Props["row_count"]!.GetValue<int>().Should().Be(3);
        tableNode.Props["col_count"]!.GetValue<int>().Should().Be(3);

        var surfaceTable = GetSurfaceTables(document).Single();
        var horizontalAnchor = GetSurfaceCell(surfaceTable, 0, 0)!;
        GetSurfaceProperty<int>(horizontalAnchor, "ColSpan").Should().Be(2);

        var verticalAnchor = GetSurfaceCell(surfaceTable, 1, 0)!;
        GetSurfaceProperty<int>(verticalAnchor, "RowSpan").Should().Be(2);
    }

    [Test]
    [DisplayName("Excludes single-column layout tables")]
    public async Task LoadAsync_ExcludesLayoutTables()
    {
        using var testFile = CreateDocument("layout_table.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Before"))),
                CreateTable(
                    [CreateTableRow([CreateTextCell("Layout only")])],
                    showBorders: false),
                new Paragraph(new Run(new Text("After")))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Count(n => n.Kind == "docx_table").Should().Be(0);
        records.Artifacts[0].Text.Should().NotContain("[Table:");
    }

    [Test]
    [DisplayName("Handles table with no detected header row")]
    public async Task LoadAsync_TableWithoutHeader()
    {
        using var testFile = CreateDocument("table_no_header.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                CreateTable(
                    [
                        CreateTableRow([CreateTextCell("R1C1"), CreateTextCell("R1C2")]),
                        CreateTableRow([CreateTextCell("R2C1"), CreateTextCell("R2C2")])
                    ])));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var tableNode = records.Nodes.Single(n => n.Kind == "docx_table");
        tableNode.Props["has_header"]!.GetValue<bool>().Should().BeFalse();
        tableNode.Props["column_names"]!.AsArray().Should().BeEmpty();
        records.Artifacts[0].Text.Should().Contain("[Table: (2 cols x 2 rows)]");
    }

    [Test]
    [DisplayName("Renders multiple tables under their headings in structure")]
    public async Task LoadAsync_StructureTemplatePositionsTables()
    {
        using var testFile = CreateDocument("tables_structure.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                CreateHeadingParagraph("First Section", 1),
                CreateTable(
                    [
                        CreateTableRow([CreateTextCell("ColA"), CreateTextCell("ColB")], isHeader: true),
                        CreateTableRow([CreateTextCell("1"), CreateTextCell("2")])
                    ]),
                CreateHeadingParagraph("Second Section", 1),
                CreateTable(
                    [
                        CreateTableRow([CreateTextCell("X"), CreateTextCell("Y")], isHeader: true),
                        CreateTableRow([CreateTextCell("9"), CreateTextCell("8")])
                    ])));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var structure = records.Artifacts[0].Structure!;
        var firstHeadingIndex = structure.IndexOf("# First Section", StringComparison.Ordinal);
        var firstTableIndex = structure.IndexOf("Table: ColA, ColB (2 cols x 2 rows)", StringComparison.Ordinal);
        var secondHeadingIndex = structure.IndexOf("# Second Section", StringComparison.Ordinal);
        var secondTableIndex = structure.IndexOf("Table: X, Y (2 cols x 2 rows)", StringComparison.Ordinal);

        firstHeadingIndex.Should().BeGreaterThan(-1);
        firstTableIndex.Should().BeGreaterThan(firstHeadingIndex);
        secondHeadingIndex.Should().BeGreaterThan(firstTableIndex);
        secondTableIndex.Should().BeGreaterThan(secondHeadingIndex);
    }

    [Test]
    [DisplayName("Extracts nested table text inline without creating extra table nodes")]
    public async Task LoadAsync_NestedTableExtractsInlineText()
    {
        using var testFile = CreateDocument("nested_table.docx", mainPart =>
        {
            var nested = CreateTable(
                [CreateTableRow([CreateTextCell("Inner Value")])],
                showBorders: true);

            var outerRow = CreateTableRow([CreateTextCell("Before", nestedTable: nested, trailingText: "After")]);

            mainPart.Document = new Document(new Body(
                CreateTable(
                    [
                        CreateTableRow([CreateTextCell("Section")], isHeader: true),
                        outerRow
                    ],
                    showBorders: true)));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Count(n => n.Kind == "docx_table").Should().Be(1);

        var surfaceTable = GetSurfaceTables(document).Single();
        var nestedTextCell = GetSurfaceCell(surfaceTable, 1, 0)!;
        var cellText = GetSurfaceProperty<string>(nestedTextCell, "Text");
        cellText.Should().Contain("Before");
        cellText.Should().Contain("Inner Value");
        cellText.Should().Contain("After");
    }

    [Test]
    [DisplayName("Skips malformed table and continues parsing subsequent content")]
    public async Task LoadAsync_MalformedTableSkipsAndContinues()
    {
        using var testFile = CreateDocument("malformed_table.docx", mainPart =>
        {
            var malformed = CreateTable(
                [CreateTableRow([CreateTextCell("Bad", gridSpan: 0)])],
                showBorders: true);

            var valid = CreateTable(
                [
                    CreateTableRow([CreateTextCell("Ok1"), CreateTextCell("Ok2")], isHeader: true),
                    CreateTableRow([CreateTextCell("A"), CreateTextCell("B")])
                ],
                showBorders: true);

            mainPart.Document = new Document(new Body(
                CreateHeadingParagraph("Before", 1),
                malformed,
                CreateHeadingParagraph("After", 1),
                valid));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Count(n => n.Kind == "docx_heading").Should().Be(2);
        records.Nodes.Count(n => n.Kind == "docx_table").Should().Be(1);
        records.Artifacts[0].Text.Should().Contain("# Before");
        records.Artifacts[0].Text.Should().Contain("# After");
        records.Artifacts[0].Text.Should().Contain("[Table: Ok1, Ok2 (2 cols x 2 rows)]");
    }

    [Test]
    [DisplayName("Throws for corrupted DOCX package")]
    public async Task LoadAsync_CorruptedFileThrows()
    {
        using var testFile = CreateRawFile("corrupted.docx", "not a zip file");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);

        var action = async () => await loader.LoadAsync(artifact.Artifact);
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    [DisplayName("Throws for encrypted/password-protected Office container")]
    public async Task LoadAsync_EncryptedContainerThrows()
    {
        var encryptedHeader = new byte[]
        {
            0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1,
            0x00, 0x01, 0x02, 0x03
        };

        using var testFile = CreateRawFile("encrypted.docx", encryptedHeader);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);

        var action = async () => await loader.LoadAsync(artifact.Artifact);
        var exception = await action.Should().ThrowAsync<InvalidDataException>();
        exception.Which.Message.Should().Contain("password-protected");
    }

    [Test]
    [DisplayName("Extracts image metadata and markers including caption and missing-part states")]
    public async Task LoadAsync_ExtractsImagesMarkersAndMissingState()
    {
        using var testFile = CreateDocument("images.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Architecture")), CreateImageRun(mainPart, "rImg1", "System Diagram")),
                new Paragraph(new Run(new Text("Figure body")), CreateImageRun(mainPart, "rImg2", null)),
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Caption" }),
                    new Run(new Text("Figure 1: Integration Flow"))),
                new Paragraph(new Run(new Text("No metadata")), CreateImageRun(mainPart, "rImg3", null)),
                new Paragraph(new Run(new Text("Missing part")), CreateImageRun(mainPart, "rMissing", "Missing Image", includeImagePart: false))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Count(n => n.Kind == "docx_image").Should().Be(4);
        records.Artifacts[0].Text.Should().Contain("[Image: System Diagram]");
        records.Artifacts[0].Text.Should().Contain("[Image]");

        var imageNodes = records.Nodes.Where(n => n.Kind == "docx_image").ToList();
        imageNodes.Should().ContainSingle(node =>
            GetJsonString(node.Props["alt_text"]) == "System Diagram"
            && GetJsonString(node.Props["content_type"]) == "image/png");
        imageNodes.Should().ContainSingle(node =>
            GetJsonString(node.Props["caption"]) == "Figure 1: Integration Flow");
        imageNodes.Should().ContainSingle(node =>
            node.Props["missing"]!.GetValue<bool>());
    }

    [Test]
    [DisplayName("Emits image accessibility lint when image has no alt text and no caption")]
    public async Task LoadAsync_EmitsImageAccessibilityLint()
    {
        using var testFile = CreateDocument("image_lint.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Needs accessibility")), CreateImageRun(mainPart, "rImgNoAlt", null))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Annotations.Should().ContainSingle(annotation =>
            annotation.Kind == "lint"
            && annotation.Severity == "warning"
            && annotation.RuleId == "docx.image-no-alt");
    }

    [Test]
    [DisplayName("Extracts comments with anchor paragraph ranges across multiple paragraphs")]
    public async Task LoadAsync_ExtractsCommentsWithAnchors()
    {
        using var testFile = CreateDocument("comments.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                CreateParagraphWithComment("Intro", "0"),
                new Paragraph(new Run(new Text("Middle paragraph"))),
                CreateParagraphWithComment("Conclusion", "1")));

            AddCommentsPart(mainPart,
            [
                CreateComment("0", "Alice", "First comment", new DateTime(2024, 01, 01, 0, 0, 0, DateTimeKind.Utc)),
                CreateComment("1", "Bob", "Second comment", new DateTime(2024, 01, 02, 0, 0, 0, DateTimeKind.Utc))
            ]);
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var commentNodes = records.Nodes.Where(n => n.Kind == "docx_comment").ToList();
        commentNodes.Should().HaveCount(2);
        commentNodes.Should().Contain(node =>
            node.Props["author"]!.GetValue<string>() == "Alice"
            && node.Props["anchor_start_paragraph"]!.GetValue<int>() == 1
            && node.Props["anchor_end_paragraph"]!.GetValue<int>() == 1);
        commentNodes.Should().Contain(node =>
            node.Props["author"]!.GetValue<string>() == "Bob"
            && node.Props["anchor_start_paragraph"]!.GetValue<int>() == 3
            && node.Props["anchor_end_paragraph"]!.GetValue<int>() == 3);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["open_comment_count"]!.GetValue<int>().Should().Be(2);
    }

    [Test]
    [DisplayName("Uses CommentsEx resolved state when available")]
    public async Task LoadAsync_UsesCommentsExResolvedState()
    {
        using var testFile = CreateDocument("comments_resolved.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                CreateParagraphWithComment("Resolved paragraph", "0"),
                CreateParagraphWithComment("Open paragraph", "1")));

            AddCommentsPart(mainPart,
            [
                CreateComment("0", "Alice", "Resolved", DateTime.UtcNow, paraId: "AAA111"),
                CreateComment("1", "Bob", "Open", DateTime.UtcNow, paraId: "BBB222")
            ]);

            var commentsExPart = mainPart.AddNewPart<WordprocessingCommentsExPart>();
            using var writer = new StreamWriter(commentsExPart.GetStream(FileMode.Create, FileAccess.Write));
            writer.Write("""
                <w15:commentsEx xmlns:w15="http://schemas.microsoft.com/office/word/2012/wordml">
                  <w15:commentEx w15:paraId="AAA111" w15:done="1" />
                  <w15:commentEx w15:paraId="BBB222" w15:done="0" />
                </w15:commentsEx>
                """);
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var commentNodes = records.Nodes.Where(n => n.Kind == "docx_comment").ToList();
        commentNodes.Should().Contain(node =>
            node.Props["text"]!.GetValue<string>() == "Resolved"
            && node.Props["resolved"]!.GetValue<bool>());
        commentNodes.Should().Contain(node =>
            node.Props["text"]!.GetValue<string>() == "Open"
            && !node.Props["resolved"]!.GetValue<bool>());
    }

    [Test]
    [DisplayName("Treats all comments as unresolved when CommentsEx part is absent")]
    public async Task LoadAsync_CommentsWithoutCommentsExAreUnresolved()
    {
        using var testFile = CreateDocument("comments_unresolved.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                CreateParagraphWithComment("Body", "0")));

            AddCommentsPart(mainPart,
            [
                CreateComment("0", "Alice", "Needs review", DateTime.UtcNow, paraId: "AAA111")
            ]);
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var commentNode = records.Nodes.Single(n => n.Kind == "docx_comment");
        commentNode.Props["resolved"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    [DisplayName("Extracts core, extended, and custom document properties")]
    public async Task LoadAsync_ExtractsCoreExtendedAndCustomProperties()
    {
        using var testFile = CreateDocument("properties.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("Body")))));

            var packageProperties = mainPart.OpenXmlPackage.PackageProperties;
            packageProperties.Title = "Requirements";
            packageProperties.Creator = "Dana";
            packageProperties.LastModifiedBy = "Lee";
            packageProperties.Description = "Billing requirements";
            packageProperties.Subject = "Billing";
            packageProperties.Keywords = "billing;requirements";
            packageProperties.Created = new DateTime(2024, 01, 01, 0, 0, 0, DateTimeKind.Utc);
            packageProperties.Modified = new DateTime(2024, 01, 02, 0, 0, 0, DateTimeKind.Utc);

            var package = (WordprocessingDocument)mainPart.OpenXmlPackage;

            var extendedPart = package.AddExtendedFilePropertiesPart();
            extendedPart.Properties = new ExtendedProps.Properties(
                new ExtendedProps.Application("Microsoft Word"),
                new ExtendedProps.Pages("5"),
                new ExtendedProps.Words("250"),
                new ExtendedProps.Paragraphs("9"));
            extendedPart.Properties.Save();

            var customPart = package.AddCustomFilePropertiesPart();
            customPart.Properties = new DocProps.Properties(
                new DocProps.CustomDocumentProperty(new VT.VTLPWSTR("Contoso"))
                {
                    Name = "Client",
                    FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}",
                    PropertyId = 2
                },
                new DocProps.CustomDocumentProperty(new VT.VTLPWSTR("true"))
                {
                    Name = "Approved",
                    FormatId = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}",
                    PropertyId = 3
                });
            customPart.Properties.Save();
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["title"]!.GetValue<string>().Should().Be("Requirements");
        documentNode.Props["author"]!.GetValue<string>().Should().Be("Dana");
        documentNode.Props["last_modified_by"]!.GetValue<string>().Should().Be("Lee");
        documentNode.Props["description"]!.GetValue<string>().Should().Be("Billing requirements");
        documentNode.Props["subject"]!.GetValue<string>().Should().Be("Billing");
        documentNode.Props["keywords"]!.GetValue<string>().Should().Be("billing;requirements");
        documentNode.Props["application"]!.GetValue<string>().Should().Be("Microsoft Word");
        documentNode.Props["page_count"]!.GetValue<int>().Should().Be(5);
        documentNode.Props["word_count"]!.GetValue<int>().Should().Be(250);
        documentNode.Props["paragraph_count"]!.GetValue<int>().Should().Be(9);

        var customProperties = documentNode.Props["custom_properties"]!.AsObject();
        customProperties["Client"]!.GetValue<string>().Should().Be("Contoso");
        customProperties["Approved"]!.GetValue<string>().Should().Be("true");
    }

    [Test]
    [DisplayName("Falls back to filename when document properties are missing")]
    public async Task LoadAsync_NoPropertiesFallsBackToFilename()
    {
        using var testFile = CreateDocument("no_properties.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("Body")))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["title"]!.GetValue<string>().Should().Be("no_properties.docx");
        documentNode.Props["custom_properties"]!.AsObject().Should().BeEmpty();
    }

    [Test]
    [DisplayName("Skips malformed comments part and continues materialization")]
    public async Task LoadAsync_MalformedCommentsPartSkipsAndContinues()
    {
        using var testFile = CreateDocument("malformed_comments.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Before comments"))),
                new Paragraph(new Run(new Text("After comments")))));

            var commentsPart = mainPart.AddNewPart<WordprocessingCommentsPart>();
            using var writer = new StreamWriter(commentsPart.GetStream(FileMode.Create, FileAccess.Write));
            writer.Write("<w:comments xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:comment");
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Nodes.Count(n => n.Kind == "docx_comment").Should().Be(0);
        records.Artifacts[0].Text.Should().Contain("Before comments");
        records.Artifacts[0].Text.Should().Contain("After comments");
    }

    [Test]
    [DisplayName("Headline includes open comments, tracked changes, and form field count")]
    public async Task LoadAsync_HeadlineIncludesReviewAndFormSignals()
    {
        using var testFile = CreateDocument("headline_signals.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("Track ")),
                    new InsertedRun(new Run(new Text("change"))) { Id = "1", Author = "Alice" },
                    new Run(new Text(" and comment ")),
                    new CommentRangeStart { Id = "0" },
                    new Run(new Text("here")),
                    new CommentRangeEnd { Id = "0" },
                    new Run(new CommentReference { Id = "0" })),
                new Paragraph(
                    new SdtRun(
                        new SdtProperties(new Tag { Val = "Field1" }),
                        new SdtContentRun(new Run(new Text("Value")))))));

            AddCommentsPart(mainPart,
            [
                CreateComment("0", "Reviewer", "Please revise", DateTime.UtcNow)
            ]);
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var headline = records.Artifacts[0].Headline!;
        headline.Should().Contain("1 open comment");
        headline.Should().Contain("tracked changes");
        headline.Should().Contain("1 form field");
    }

    [Test]
    [DisplayName("Extracts footnotes with inline markers and appended section")]
    public async Task LoadAsync_FootnotesMarkersAndSection()
    {
        using var testFile = CreateDocument("footnotes.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("Body footnote")),
                    new Run(new FootnoteReference { Id = 1 })),
                new Paragraph(new Run(new Text("After note")))));

            AddFootnotesPart(mainPart,
            [
                CreateFootnote("-1", string.Empty, "separator"),
                CreateFootnote("0", string.Empty, "continuationSeparator"),
                CreateFootnote("1", "Footnote one text")
            ]);
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var text = records.Artifacts[0].Text;
        text.Should().Contain("Body footnote[^1]");
        text.Should().Contain("Footnotes:");
        text.Should().Contain("[1] Footnote one text");
        text.Should().NotContain("Endnotes:");
    }

    [Test]
    [DisplayName("Extracts endnotes with inline markers and appended section")]
    public async Task LoadAsync_EndnotesMarkersAndSection()
    {
        using var testFile = CreateDocument("endnotes.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("Body endnote")),
                    new Run(new EndnoteReference { Id = 2 }))));

            AddEndnotesPart(mainPart,
            [
                CreateEndnote("-1", string.Empty, "separator"),
                CreateEndnote("0", string.Empty, "continuationSeparator"),
                CreateEndnote("2", "Endnote two text")
            ]);
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var text = records.Artifacts[0].Text;
        text.Should().Contain("Body endnote[*2]");
        text.Should().Contain("Endnotes:");
        text.Should().Contain("[*2] Endnote two text");
        text.Should().NotContain("Footnotes:");
    }

    [Test]
    [DisplayName("Appends footnotes before endnotes when both exist")]
    public async Task LoadAsync_FootnotesBeforeEndnotes()
    {
        using var testFile = CreateDocument("both_notes.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("Body ")),
                    new Run(new FootnoteReference { Id = 1 }),
                    new Run(new Text(" and ")),
                    new Run(new EndnoteReference { Id = 3 }))));

            AddFootnotesPart(mainPart,
            [
                CreateFootnote("1", "Footnote text")
            ]);

            AddEndnotesPart(mainPart,
            [
                CreateEndnote("3", "Endnote text")
            ]);
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var text = records.Artifacts[0].Text ?? string.Empty;
        var footnotesIndex = text.IndexOf("Footnotes:", StringComparison.Ordinal);
        var endnotesIndex = text.IndexOf("Endnotes:", StringComparison.Ordinal);

        footnotesIndex.Should().BeGreaterThan(-1);
        endnotesIndex.Should().BeGreaterThan(footnotesIndex);
    }

    [Test]
    [DisplayName("Skips system-generated footnote separators")]
    public async Task LoadAsync_ExcludesSystemGeneratedFootnotes()
    {
        using var testFile = CreateDocument("footnote_separators.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("Body footnote")),
                    new Run(new FootnoteReference { Id = 1 }))));

            AddFootnotesPart(mainPart,
            [
                CreateFootnote("-1", "separator should skip", "separator"),
                CreateFootnote("0", "continuation should skip", "continuationSeparator"),
                CreateFootnote("1", "Author footnote")
            ]);
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var text = records.Artifacts[0].Text;
        text.Should().Contain("[1] Author footnote");
        text.Should().NotContain("separator should skip");
        text.Should().NotContain("continuation should skip");
    }

    [Test]
    [DisplayName("Creates REFERS_TO edges for external hyperlinks")]
    public async Task LoadAsync_ExternalHyperlinksCreateRefersToEdges()
    {
        using var testFile = CreateDocument("external_links.docx", mainPart =>
        {
            mainPart.AddHyperlinkRelationship(new Uri("https://openai.com/", UriKind.Absolute), true, "rIdHyperOpenAi");

            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("Visit ")),
                    new Hyperlink(new Run(new Text("OpenAI")))
                    {
                        Id = "rIdHyperOpenAi"
                    })));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        var references = records.Edges.Where(edge => edge.Type == "REFERS_TO").ToList();

        references.Should().ContainSingle();
        references[0].SrcId.Should().Be(documentNode.Id);
        var targetUri = references[0].DstUri;
        targetUri.Should().NotBeNull();
        targetUri?.AbsoluteUri.Should().Contain("https://openai.com/");
        references[0].Props["display_text"]!.GetValue<string>().Should().Be("OpenAI");
    }

    [Test]
    [DisplayName("Captures internal bookmark hyperlinks without creating external REFERS_TO edges")]
    public async Task LoadAsync_InternalBookmarkHyperlinksHandled()
    {
        using var testFile = CreateDocument("bookmark_links.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new BookmarkStart { Id = "10", Name = "TargetBookmark" },
                    new Run(new Text("Target")),
                    new BookmarkEnd { Id = "10" }),
                new Paragraph(
                    new Hyperlink(new Run(new Text("Jump to target")))
                    {
                        Anchor = "TargetBookmark"
                    })));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Edges.Count(edge => edge.Type == "REFERS_TO").Should().Be(0);

        var hyperlinks = GetSurfaceHyperlinks(document);
        hyperlinks.Should().ContainSingle();
        GetSurfaceProperty<bool>(hyperlinks[0], "IsExternal").Should().BeFalse();
        GetSurfaceProperty<string>(hyperlinks[0], "DisplayText").Should().Be("Jump to target");
        GetSurfaceProperty<string>(hyperlinks[0], "BookmarkName").Should().Be("TargetBookmark");
        GetSurfacePropertyOrDefault<string>(hyperlinks[0], "TargetUrl").Should().BeNull();
    }

    [Test]
    [DisplayName("Extracts header and footer metadata without adding them to body text")]
    public async Task LoadAsync_HeaderFooterMetadataOnly()
    {
        using var testFile = CreateDocument("header_footer.docx", mainPart =>
        {
            var headerPart = mainPart.AddNewPart<HeaderPart>("rIdHeader");
            headerPart.Header = new Header(new Paragraph(new Run(new Text("Confidential Header"))));
            headerPart.Header.Save();

            var footerPart = mainPart.AddNewPart<FooterPart>("rIdFooter");
            footerPart.Footer = new Footer(new Paragraph(new Run(new Text("Internal Footer"))));
            footerPart.Footer.Save();

            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Body text only"))),
                new Paragraph(
                    new ParagraphProperties(
                        new SectionProperties(
                            new HeaderReference
                            {
                                Type = HeaderFooterValues.Default,
                                Id = "rIdHeader"
                            },
                            new FooterReference
                            {
                                Type = HeaderFooterValues.Default,
                                Id = "rIdFooter"
                            })))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var text = records.Artifacts[0].Text;
        text.Should().Contain("Body text only");
        text.Should().NotContain("Confidential Header");
        text.Should().NotContain("Internal Footer");

        var documentNode = records.Nodes.Single(n => n.Kind == "document");
        documentNode.Props["header_text"]!.GetValue<string>().Should().Be("Confidential Header");
        documentNode.Props["footer_text"]!.GetValue<string>().Should().Be("Internal Footer");
    }

    [Test]
    [DisplayName("Does not append empty footnote or endnote sections when absent")]
    public async Task LoadAsync_NoFootnotesEndnotesHyperlinks_NoEmptySections()
    {
        using var testFile = CreateDocument("no_notes_links.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("No notes or links")))));
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var text = records.Artifacts[0].Text;
        text.Should().Be("No notes or links");
        text.Should().NotContain("Footnotes:");
        text.Should().NotContain("Endnotes:");
        records.Edges.Count(edge => edge.Type == "REFERS_TO").Should().Be(0);
    }

    [Test]
    [DisplayName("Skips malformed footnotes part and continues extraction")]
    public async Task LoadAsync_MalformedFootnotesPartSkipsAndContinues()
    {
        using var testFile = CreateDocument("malformed_footnotes.docx", mainPart =>
        {
            mainPart.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Body survives malformed footnotes")))));

            var footnotesPart = mainPart.AddNewPart<FootnotesPart>();
            using var writer = new StreamWriter(footnotesPart.GetStream(FileMode.Create, FileAccess.Write));
            writer.Write("<w:footnotes xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:footnote");
        });

        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new DocxLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts[0].Text.Should().Contain("Body survives malformed footnotes");
        records.Artifacts[0].Text.Should().NotContain("Footnotes:");
    }

    private static readonly byte[] TinyPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8Xw8AAusB9Y5T2rQAAAAASUVORK5CYII=");

    private static Run CreateImageRun(
        MainDocumentPart mainPart,
        string relationshipId,
        string? altText,
        bool includeImagePart = true)
    {
        if (includeImagePart)
        {
            var imagePart = mainPart.AddImagePart(ImagePartType.Png, relationshipId);
            using var stream = imagePart.GetStream(FileMode.Create, FileAccess.Write);
            stream.Write(TinyPngBytes, 0, TinyPngBytes.Length);
        }

        var docProperties = new DW.DocProperties
        {
            Id = 1U,
            Name = $"Picture {relationshipId}",
            Description = altText,
            Title = altText
        };

        return new Run(
            new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = 990000L, Cy = 792000L },
                    new DW.EffectExtent
                    {
                        LeftEdge = 0L,
                        TopEdge = 0L,
                        RightEdge = 0L,
                        BottomEdge = 0L
                    },
                    docProperties,
                    new DW.NonVisualGraphicFrameDrawingProperties(
                        new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties
                                    {
                                        Id = 0U,
                                        Name = $"Image {relationshipId}",
                                        Description = altText,
                                        Title = altText
                                    },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = relationshipId },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = 990000L, Cy = 792000L }),
                                    new A.PresetGeometry(new A.AdjustValueList())
                                    {
                                        Preset = A.ShapeTypeValues.Rectangle
                                    })))
                        {
                            Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
                        }))));
    }

    private static Paragraph CreateParagraphWithComment(string text, string commentId)
    {
        return new Paragraph(
            new Run(new Text($"{text} ")),
            new CommentRangeStart { Id = commentId },
            new Run(new Text("target")),
            new CommentRangeEnd { Id = commentId },
            new Run(new CommentReference { Id = commentId }));
    }

    private static Comment CreateComment(
        string id,
        string author,
        string text,
        DateTime date,
        string? paraId = null)
    {
        var comment = new Comment
        {
            Id = id,
            Author = author,
            Date = date
        };

        if (!string.IsNullOrWhiteSpace(paraId))
        {
            comment.SetAttribute(new OpenXmlAttribute(
                "w15",
                "paraId",
                "http://schemas.microsoft.com/office/word/2012/wordml",
                paraId));
        }

        comment.Append(new Paragraph(new Run(new Text(text))));
        return comment;
    }

    private static void AddCommentsPart(MainDocumentPart mainPart, IEnumerable<Comment> comments)
    {
        var commentsPart = mainPart.AddNewPart<WordprocessingCommentsPart>();
        commentsPart.Comments = new Comments(comments);
        commentsPart.Comments.Save();
    }

    private static void AddFootnotesPart(MainDocumentPart mainPart, IEnumerable<Footnote> footnotes)
    {
        var footnotesPart = mainPart.AddNewPart<FootnotesPart>();
        footnotesPart.Footnotes = new Footnotes(footnotes);
        footnotesPart.Footnotes.Save();
    }

    private static void AddEndnotesPart(MainDocumentPart mainPart, IEnumerable<Endnote> endnotes)
    {
        var endnotesPart = mainPart.AddNewPart<EndnotesPart>();
        endnotesPart.Endnotes = new Endnotes(endnotes);
        endnotesPart.Endnotes.Save();
    }

    private static Footnote CreateFootnote(string id, string text, string? type = null)
    {
        var footnote = new Footnote();
        footnote.SetAttribute(new OpenXmlAttribute("w", "id", WordNamespace, id));
        if (!string.IsNullOrWhiteSpace(type))
            footnote.SetAttribute(new OpenXmlAttribute("w", "type", WordNamespace, type));

        if (!string.IsNullOrWhiteSpace(text))
            footnote.Append(new Paragraph(new Run(new Text(text))));

        return footnote;
    }

    private static Endnote CreateEndnote(string id, string text, string? type = null)
    {
        var endnote = new Endnote();
        endnote.SetAttribute(new OpenXmlAttribute("w", "id", WordNamespace, id));
        if (!string.IsNullOrWhiteSpace(type))
            endnote.SetAttribute(new OpenXmlAttribute("w", "type", WordNamespace, type));

        if (!string.IsNullOrWhiteSpace(text))
            endnote.Append(new Paragraph(new Run(new Text(text))));

        return endnote;
    }

    private static string? GetJsonString(JsonNode? node)
    {
        if (node is null)
            return null;

        return node.GetValue<string>();
    }

    private static Paragraph CreateHeadingParagraph(string text, int level)
    {
        return new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{level}" }),
            new Run(new Text(text)));
    }

    private static Table CreateTable(IEnumerable<TableRow> rows, bool showBorders = true)
    {
        var table = new Table();
        table.AppendChild(CreateTableProperties(showBorders));
        foreach (var row in rows)
            table.Append(row);
        return table;
    }

    private static TableRow CreateTableRow(IEnumerable<TableCell> cells, bool isHeader = false)
    {
        var row = new TableRow();
        if (isHeader)
            row.AppendChild(new TableRowProperties(new TableHeader()));

        foreach (var cell in cells)
            row.Append(cell);

        return row;
    }

    private static TableCell CreateTextCell(
        string text,
        int? gridSpan = null,
        MergedCellValues? hMerge = null,
        MergedCellValues? vMerge = null,
        Table? nestedTable = null,
        string? trailingText = null)
    {
        var cell = new TableCell();
        var cellProperties = new TableCellProperties();

        if (gridSpan.HasValue)
            cellProperties.Append(new GridSpan { Val = gridSpan.Value });

        if (hMerge.HasValue)
            cellProperties.Append(new HorizontalMerge { Val = hMerge.Value });

        if (vMerge.HasValue)
            cellProperties.Append(new VerticalMerge { Val = vMerge.Value });

        if (cellProperties.ChildElements.Count > 0)
            cell.Append(cellProperties);

        if (!string.IsNullOrWhiteSpace(text))
            cell.Append(new Paragraph(new Run(new Text(text))));

        if (nestedTable is not null)
            cell.Append(nestedTable.CloneNode(true));

        if (!string.IsNullOrWhiteSpace(trailingText))
            cell.Append(new Paragraph(new Run(new Text(trailingText))));

        if (cell.ChildElements.Count == 0)
            cell.Append(new Paragraph(new Run(new Text(string.Empty))));

        return cell;
    }

    private static TableProperties CreateTableProperties(bool showBorders)
    {
        if (showBorders)
        {
            return new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 8 },
                    new BottomBorder { Val = BorderValues.Single, Size = 8 },
                    new LeftBorder { Val = BorderValues.Single, Size = 8 },
                    new RightBorder { Val = BorderValues.Single, Size = 8 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 8 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 8 }));
        }

        return new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.None },
                new BottomBorder { Val = BorderValues.None },
                new LeftBorder { Val = BorderValues.None },
                new RightBorder { Val = BorderValues.None },
                new InsideHorizontalBorder { Val = BorderValues.None },
                new InsideVerticalBorder { Val = BorderValues.None }));
    }

    private static List<object> GetSurfaceTables(DocumentModel document)
    {
#pragma warning disable IL2075
        document.Metadata.TryGetValue("docx.state", out var state).Should().BeTrue();
        state.Should().NotBeNull();

        var surface = state!.GetType()
            .GetProperty("Surface", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(state);
        surface.Should().NotBeNull();

        var tables = (IEnumerable)surface!.GetType()
            .GetProperty("Tables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(surface)!;
#pragma warning restore IL2075

        return tables.Cast<object>().ToList();
    }

    private static List<object> GetSurfaceHyperlinks(DocumentModel document)
    {
#pragma warning disable IL2075
        document.Metadata.TryGetValue("docx.state", out var state).Should().BeTrue();
        state.Should().NotBeNull();

        var surface = state!.GetType()
            .GetProperty("Surface", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(state);
        surface.Should().NotBeNull();

        var hyperlinks = (IEnumerable)surface!.GetType()
            .GetProperty("Hyperlinks", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(surface)!;
#pragma warning restore IL2075

        return hyperlinks.Cast<object>().ToList();
    }

    private static object? GetSurfaceCell(object table, int row, int col)
    {
        var rows = (IEnumerable)GetSurfaceProperty<object>(table, "Cells");
        var rowList = rows.Cast<object>().ToList();
        var cells = (IEnumerable)rowList[row];
        return cells.Cast<object?>().ElementAt(col);
    }

    private static T GetSurfaceProperty<T>(object source, string name)
    {
#pragma warning disable IL2075
        var property = source.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
#pragma warning restore IL2075
        property.Should().NotBeNull($"expected property {name} on {source.GetType().Name}");
        var value = property!.GetValue(source);
        value.Should().NotBeNull();
        return (T)value!;
    }

    private static T? GetSurfacePropertyOrDefault<T>(object source, string name)
    {
#pragma warning disable IL2075
        var property = source.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
#pragma warning restore IL2075
        property.Should().NotBeNull($"expected property {name} on {source.GetType().Name}");
        var value = property!.GetValue(source);
        return (T?)value;
    }

    private static TestFileScope CreateDocument(string fileName, Action<MainDocumentPart> configureDocument)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"docx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);

        using (var document = WordprocessingDocument.Create(filePath, ResolveDocumentType(fileName)))
        {
            var mainPart = document.AddMainDocumentPart();
            configureDocument(mainPart);
            mainPart.Document ??= new Document(new Body());
            mainPart.Document.Save();
        }

        return new TestFileScope(filePath, tempDir);
    }

    private static TestFileScope CreateRawFile(string fileName, string content)
        => CreateRawFile(fileName, System.Text.Encoding.UTF8.GetBytes(content));

    private static TestFileScope CreateRawFile(string fileName, byte[] bytes)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"docx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        File.WriteAllBytes(filePath, bytes);
        return new TestFileScope(filePath, tempDir);
    }

    private static WordprocessingDocumentType ResolveDocumentType(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.Equals(extension, ".docm", StringComparison.OrdinalIgnoreCase))
            return WordprocessingDocumentType.MacroEnabledDocument;
        if (string.Equals(extension, ".dotx", StringComparison.OrdinalIgnoreCase))
            return WordprocessingDocumentType.Template;
        return WordprocessingDocumentType.Document;
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
        var directory = Path.GetDirectoryName(filePath)!;
        var fileName = Path.GetFileName(filePath);
        var provider = new PhysicalFileProvider(directory);

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

        public void Dispose() => Provider.Dispose();
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
            => throw new NotSupportedException("Fake file info does not support reading.");
    }
}
