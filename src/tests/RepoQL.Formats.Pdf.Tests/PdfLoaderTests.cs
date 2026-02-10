using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Formats.Pdf.Surface;
using RepoQL.Formats.Pdf.TextExtraction;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Writer;

namespace RepoQL.Formats.Pdf.Tests;

public sealed class PdfLoaderTests
{
    [Test]
    [DisplayName("CanLoadAsync recognizes .pdf extension")]
    public async Task CanLoadAsync_RecognizesPdfExtension()
    {
        var loader = new PdfLoader();
        var artifact = CreateFakeArtifact("sample.pdf");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeTrue();
        artifact.MediaType.Should().NotBeNull();
        artifact.MediaType!.Subtype.Should().Be("pdf");
    }

    [Test]
    [DisplayName("CanLoadAsync rejects non-pdf files")]
    public async Task CanLoadAsync_RejectsNonPdfFiles()
    {
        var loader = new PdfLoader();
        var artifact = CreateFakeArtifact("sample.txt");

        var canLoad = await loader.CanLoadAsync(artifact);

        canLoad.Should().BeFalse();
    }

    [Test]
    [DisplayName("LoadAsync and Materialize extract text for a single-page PDF")]
    public async Task LoadAsync_Materialize_ExtractsText()
    {
        using var testFile = CreatePdf("single.pdf", ["Hello PDF world"]);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        (await loader.CanLoadAsync(artifact.Artifact)).Should().BeTrue();
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts.Should().ContainSingle();
        records.Nodes.Should().ContainSingle(n => n.Kind == "document");

        var artifactRecord = records.Artifacts[0];
        artifactRecord.Text.Should().Contain("Hello PDF world");
        artifactRecord.MediaType!.Kind.Should().Be("pdf.document");

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["page_count"]!.GetValue<int>().Should().Be(1);
        docNode.Props["text_page_count"]!.GetValue<int>().Should().Be(1);
        docNode.Props["image_only_page_count"]!.GetValue<int>().Should().Be(0);
    }

    [Test]
    [DisplayName("Materialize stores page byte offsets and token counts")]
    public async Task Materialize_StoresPageAddressingProps()
    {
        using var testFile = CreatePdf("multi.pdf", ["Page one text", "Page two text"]);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var artifactText = records.Artifacts[0].Text!;
        var docNode = records.Nodes.Single(n => n.Kind == "document");
        var offsets = docNode.Props["page_byte_offsets"]!.AsArray();
        var tokenCounts = docNode.Props["page_token_counts"]!.AsArray();

        offsets.Count.Should().Be(2);
        tokenCounts.Count.Should().Be(2);

        var pageOneBytes = ReadByteRange(artifactText, offsets[0]!.AsArray());
        var pageTwoBytes = ReadByteRange(artifactText, offsets[1]!.AsArray());

        pageOneBytes.Should().Contain("Page one text");
        pageTwoBytes.Should().Contain("Page two text");
    }

    [Test]
    [DisplayName("Image-only PDF materializes as pdf.scan")]
    public async Task LoadAsync_ImageOnly_SetsScanKind()
    {
        using var testFile = CreatePdfWithoutText("scan.pdf", pageCount: 1);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts[0].MediaType!.Kind.Should().Be("pdf.scan");
        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["text_page_count"]!.GetValue<int>().Should().Be(0);
    }

    [Test]
    [DisplayName("Corrupted PDF throws InvalidDataException")]
    public async Task LoadAsync_CorruptedPdf_Throws()
    {
        using var testFile = CreateRawFile("bad.pdf", "not a valid pdf");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        await loader.CanLoadAsync(artifact.Artifact);

        var action = async () => await loader.LoadAsync(artifact.Artifact);
        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Test]
    [DisplayName("Loader supports reopen-per-page mode thresholds")]
    public async Task LoadAsync_UsesReopenPerPageWhenThresholdsExceeded()
    {
        using var testFile = CreatePdf("thresholds.pdf", ["First", "Second"]);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader(singleOpenMaxBytes: 1, singleOpenMaxPages: 1);

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        records.Artifacts[0].Text.Should().Contain("First");
        records.Artifacts[0].Text.Should().Contain("Second");
        records.Nodes.Single(n => n.Kind == "document").Props["page_count"]!.GetValue<int>().Should().Be(2);
    }

    [Test]
    [DisplayName("Materialize creates pdf_bookmark nodes, spans, and HAS_PART edges for flat bookmarks")]
    public async Task Materialize_FlatBookmarks_CreatesNodesSpansAndEdges()
    {
        var bookmarks = new BookmarkNode[]
        {
            CreateBookmark("Introduction", pageNumber: 1, level: 1),
            CreateBookmark("Authentication", pageNumber: 2, level: 1),
            CreateBookmark("Endpoints", pageNumber: 3, level: 1)
        };

        using var testFile = CreatePdf(
            "flat-bookmarks.pdf",
            ["Page 1", "Page 2", "Page 3", "Page 4"],
            bookmarks);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["has_bookmarks"]!.GetValue<bool>().Should().BeTrue();
        docNode.Props["bookmark_count"]!.GetValue<int>().Should().Be(3);

        var bookmarkNodes = records.Nodes.Where(n => n.Kind == "pdf_bookmark").ToList();
        bookmarkNodes.Should().HaveCount(3);
        bookmarkNodes.All(n => !string.IsNullOrWhiteSpace(n.Headline)).Should().BeTrue();
        bookmarkNodes.Any(n => n.Uri!.Loc.Symbol == "Introduction").Should().BeTrue();

        records.Spans.Should().HaveCount(3);

        var edges = records.Edges
            .Where(e => e.Type == "HAS_PART" && e.SrcId == docNode.Id)
            .OrderBy(e => e.Ordinal)
            .ToList();
        edges.Should().HaveCount(3);
        edges.Select(e => e.Ordinal).Should().BeEquivalentTo([0, 1, 2]);
    }

    [Test]
    [DisplayName("Nested bookmarks preserve depth-first tree order and level props")]
    public async Task Materialize_NestedBookmarks_PreservesOrderAndLevels()
    {
        var bookmarks = new BookmarkNode[]
        {
            CreateBookmark("Introduction", pageNumber: 1, level: 1, children:
            [
                CreateBookmark("Setup", pageNumber: 2, level: 2, children:
                [
                    CreateBookmark("Deep Dive", pageNumber: 3, level: 3)
                ])
            ]),
            CreateBookmark("API", pageNumber: 4, level: 1)
        };

        using var testFile = CreatePdf(
            "nested-bookmarks.pdf",
            ["Page 1", "Page 2", "Page 3", "Page 4", "Page 5"],
            bookmarks);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var bookmarkNodesById = records.Nodes
            .Where(n => n.Kind == "pdf_bookmark")
            .ToDictionary(n => n.Id);

        var orderedTitles = records.Edges
            .Where(e => e.Type == "HAS_PART")
            .OrderBy(e => e.Ordinal)
            .Select(e => bookmarkNodesById[e.DstId!.Value].Props["title"]!.GetValue<string>())
            .ToList();

        orderedTitles.Should().Equal("Introduction", "Setup", "Deep Dive", "API");

        bookmarkNodesById.Values.Single(n => n.Props["title"]!.GetValue<string>() == "Introduction").Props["level"]!.GetValue<int>().Should().Be(1);
        bookmarkNodesById.Values.Single(n => n.Props["title"]!.GetValue<string>() == "Setup").Props["level"]!.GetValue<int>().Should().Be(2);
        bookmarkNodesById.Values.Single(n => n.Props["title"]!.GetValue<string>() == "Deep Dive").Props["level"]!.GetValue<int>().Should().Be(3);
    }

    [Test]
    [DisplayName("No bookmarks sets has_bookmarks false and structure falls back to page inventory")]
    public async Task Materialize_NoBookmarks_FallsBackToPageInventory()
    {
        using var testFile = CreatePdf("no-bookmarks.pdf", ["One", "Two"]);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(n => n.Kind == "document");
        docNode.Props["has_bookmarks"]!.GetValue<bool>().Should().BeFalse();
        docNode.Props["bookmark_count"]!.GetValue<int>().Should().Be(0);

        records.Nodes.Count(n => n.Kind == "pdf_bookmark").Should().Be(0);
        records.Spans.Should().BeEmpty();
        records.Edges.Should().BeEmpty();

        records.Artifacts[0].Structure.Should().Contain("No outline detected");
        records.Artifacts[0].Structure.Should().Contain("Pages:");
    }

    [Test]
    [DisplayName("Bookmark spans use next sibling or ancestor sibling target page")]
    public async Task Materialize_BookmarkSpans_UseExpectedPageRanges()
    {
        var bookmarks = new BookmarkNode[]
        {
            CreateBookmark("Introduction", pageNumber: 1, level: 1, children:
            [
                CreateBookmark("Setup", pageNumber: 2, level: 2, children:
                [
                    CreateBookmark("Deep Dive", pageNumber: 3, level: 3)
                ])
            ]),
            CreateBookmark("API", pageNumber: 4, level: 1)
        };

        using var testFile = CreatePdf(
            "bookmark-ranges.pdf",
            ["P1", "P2", "P3", "P4", "P5", "P6"],
            bookmarks);
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var spansByTitle = records.Nodes
            .Where(n => n.Kind == "pdf_bookmark")
            .ToDictionary(
                node => node.Props["title"]!.GetValue<string>(),
                node => records.Spans.Single(span => span.Id == node.SpanId));

        spansByTitle["Introduction"].StartLine.Should().Be(1);
        spansByTitle["Introduction"].EndLine.Should().Be(3);
        spansByTitle["Setup"].StartLine.Should().Be(2);
        spansByTitle["Setup"].EndLine.Should().Be(3);
        spansByTitle["Deep Dive"].StartLine.Should().Be(3);
        spansByTitle["Deep Dive"].EndLine.Should().Be(3);
        spansByTitle["API"].StartLine.Should().Be(4);
        spansByTitle["API"].EndLine.Should().Be(6);
    }

    [Test]
    [DisplayName("Materialize creates pdf_form_field nodes and has_values metadata")]
    public void Materialize_FormFields_CreatesNodesAndHasValues()
    {
        var formFields = new[]
        {
            new FormFieldInfo
            {
                NodeId = Guid.NewGuid(),
                SpanId = Guid.NewGuid(),
                FieldName = "first_name",
                FieldType = "Text",
                Value = "Ada",
                Page = 1
            },
            new FormFieldInfo
            {
                NodeId = Guid.NewGuid(),
                SpanId = Guid.NewGuid(),
                FieldName = "agree_terms",
                FieldType = "Checkbox",
                Value = null,
                Page = 1
            }
        };

        var state = CreateSyntheticState(formFields: formFields, mediaType: PdfMediaTypes.Form);
        var document = CreateDocumentFromState(state, "form.pdf");
        var records = new PdfLoader().Materialize(document);

        records.Artifacts[0].MediaType!.Kind.Should().Be("pdf.form");
        records.Nodes.Count(node => node.Kind == PdfNodeKinds.FormField).Should().Be(2);
        records.Edges.Count(edge => edge.Type == "HAS_PART").Should().Be(2);

        var docNode = records.Nodes.Single(node => node.Kind == PdfNodeKinds.Document);
        docNode.Props["has_form"]!.GetValue<bool>().Should().BeTrue();
        docNode.Props["form_field_count"]!.GetValue<int>().Should().Be(2);
        docNode.Props["has_values"]!.GetValue<bool>().Should().BeTrue();
    }

    [Test]
    [DisplayName("Materialize distinguishes blank forms from filled forms")]
    public void Materialize_BlankForm_HasValuesFalse()
    {
        var blankField = new FormFieldInfo
        {
            NodeId = Guid.NewGuid(),
            SpanId = Guid.NewGuid(),
            FieldName = "full_name",
            FieldType = "Text",
            Value = null,
            Page = 1
        };

        var state = CreateSyntheticState(formFields: [blankField], mediaType: PdfMediaTypes.Form);
        var document = CreateDocumentFromState(state, "blank-form.pdf");
        var records = new PdfLoader().Materialize(document);

        var docNode = records.Nodes.Single(node => node.Kind == PdfNodeKinds.Document);
        docNode.Props["has_values"]!.GetValue<bool>().Should().BeFalse();
    }

    [Test]
    [DisplayName("Materialize creates PDF annotations and sets AnnotationSources")]
    public void Materialize_PdfAnnotations_CreateAnnotationRecords()
    {
        var annotations = new[]
        {
            new PdfAnnotationInfo
            {
                AnnotationType = "comment",
                Page = 1,
                Content = "Needs legal review",
                Author = "alice",
                Date = "2026-02-09"
            },
            new PdfAnnotationInfo
            {
                AnnotationType = "highlight",
                Page = 1,
                Content = null,
                Author = null,
                Date = null
            }
        };

        var state = CreateSyntheticState(pdfAnnotations: annotations);
        var document = CreateDocumentFromState(state, "annotated.pdf");
        var records = new PdfLoader().Materialize(document);

        records.Annotations.Should().HaveCount(2);
        records.AnnotationSources.Should().ContainSingle().Which.Should().Be(PdfLoader.AnnotationSource);
        records.Annotations.All(annotation => annotation.Source == PdfLoader.AnnotationSource).Should().BeTrue();
        records.Annotations.Single(a => a.Kind == "highlight").Message.Should().Contain("highlight on page");
    }

    [Test]
    [DisplayName("Materialize creates REFERS_TO edges for PDF links")]
    public void Materialize_PdfLinks_CreateRefersToEdges()
    {
        var links = new[]
        {
            new PdfLinkInfo { Page = 1, Url = "https://example.com/spec" },
            new PdfLinkInfo { Page = 2, Url = "notaurl" }
        };

        var state = CreateSyntheticState(links: links);
        var document = CreateDocumentFromState(state, "links.pdf");
        var records = new PdfLoader().Materialize(document);

        var refersTo = records.Edges.Where(edge => edge.Type == "REFERS_TO").ToList();
        refersTo.Should().ContainSingle();
        refersTo[0].DstUri!.AbsoluteUri.Should().Be("https://example.com/spec");
    }

    [Test]
    [DisplayName("LoadAsync tracks image_count and pages_with_images")]
    public async Task LoadAsync_DetectsImages()
    {
        using var testFile = CreatePdfWithImage("image.pdf");
        using var artifact = CreateArtifactFromFile(testFile.FilePath);
        var loader = new PdfLoader();

        await loader.CanLoadAsync(artifact.Artifact);
        var document = await loader.LoadAsync(artifact.Artifact);
        var records = loader.Materialize(document);

        var docNode = records.Nodes.Single(node => node.Kind == PdfNodeKinds.Document);
        docNode.Props["image_count"]!.GetValue<int>().Should().BeGreaterThan(0);
        docNode.Props["pages_with_images"]!.GetValue<int>().Should().Be(1);
    }

    [Test]
    [DisplayName("GetSchemaScripts loads embedded pdf views SQL")]
    public void GetSchemaScripts_LoadsPdfViewsSql()
    {
        var loader = new PdfLoader();

        var scripts = loader.GetSchemaScripts().ToList();

        scripts.Should().ContainSingle();
        scripts[0].Identifier.Should().Be("pdf_views");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW pdf_form_fields");
        scripts[0].Sql.Should().Contain("CREATE OR REPLACE VIEW pdf_annotations");
    }

    private static string ReadByteRange(string text, JsonArray pair)
    {
        var start = pair[0]!.GetValue<long>();
        var end = pair[1]!.GetValue<long>();
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = checked((int)(end - start));
        return Encoding.UTF8.GetString(bytes, checked((int)start), length);
    }

    private static TestFileScope CreatePdf(
        string fileName,
        IReadOnlyList<string> pageTexts,
        IReadOnlyList<BookmarkNode>? bookmarks = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pdf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);

        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var pageText in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(pageText, 12, new PdfPoint(36, 800), font);
        }

        if (bookmarks is { Count: > 0 })
        {
            builder.Bookmarks = new Bookmarks(bookmarks);
        }

        var bytes = builder.Build();
        File.WriteAllBytes(filePath, bytes);

        return new TestFileScope(filePath, tempDir);
    }

    private static TestFileScope CreatePdfWithoutText(string fileName, int pageCount)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pdf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);

        using var builder = new PdfDocumentBuilder();
        for (var index = 0; index < pageCount; index++)
        {
            _ = builder.AddPage(PageSize.A4);
        }

        var bytes = builder.Build();
        File.WriteAllBytes(filePath, bytes);

        return new TestFileScope(filePath, tempDir);
    }

    private static TestFileScope CreatePdfWithImage(string fileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pdf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);

        var pngBytes = File.ReadAllBytes(FindRepoImagePath());

        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.AddPng(pngBytes, new PdfRectangle(36, 700, 96, 760));

        var bytes = builder.Build();
        File.WriteAllBytes(filePath, bytes);

        return new TestFileScope(filePath, tempDir);
    }

    private static string FindRepoImagePath()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "RepoQL.Web", "wwwroot", "favicon.png");
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate src/RepoQL.Web/wwwroot/favicon.png from test working directory.");
    }

    private static PdfDocumentState CreateSyntheticState(
        IReadOnlyList<FormFieldInfo>? formFields = null,
        IReadOnlyList<PdfAnnotationInfo>? pdfAnnotations = null,
        IReadOnlyList<PdfLinkInfo>? links = null,
        IReadOnlyList<string>? embeddedFileNames = null,
        SemanticMediaType? mediaType = null)
    {
        var localFormFields = formFields ?? [];
        var localAnnotations = pdfAnnotations ?? [];
        var localLinks = links ?? [];
        var localEmbeddedNames = embeddedFileNames ?? [];
        var pageTexts = new[] { "Synthetic PDF text." };
        var assembled = PageTextAssembler.Assemble(pageTexts);

        var surface = new PdfDocumentSurface
        {
            DocumentId = Guid.NewGuid(),
            Metadata = new PdfDocumentMetadata
            {
                Title = "Synthetic PDF",
                Author = "RepoQL Tests",
                Version = "1.7"
            },
            Pages =
            [
                new PageInfo
                {
                    Number = 1,
                    Width = 595,
                    Height = 842,
                    Rotation = 0,
                    HasText = true,
                    IsImageOnly = false
                }
            ],
            Bookmarks = [],
            FormFields = localFormFields,
            PdfAnnotations = localAnnotations,
            Links = localLinks,
            EmbeddedFileNames = localEmbeddedNames,
            PageTexts = pageTexts,
            AssembledText = assembled,
            Stats = new PdfDocumentStats
            {
                PageCount = 1,
                TextPageCount = 1,
                ImageOnlyPageCount = 0,
                HasBookmarks = false,
                BookmarkCount = 0,
                HasForm = localFormFields.Count > 0,
                FormFieldCount = localFormFields.Count,
                HasValues = localFormFields.Any(f => !string.IsNullOrWhiteSpace(f.Value)),
                AnnotationCount = localAnnotations.Count,
                LinkCount = localLinks.Count,
                ImageCount = 0,
                PagesWithImages = 0,
                EmbeddedFileCount = localEmbeddedNames.Count
            }
        };

        return new PdfDocumentState
        {
            Surface = surface,
            Digest = "synthetic-digest",
            Size = 1234,
            MediaType = mediaType ?? (localFormFields.Count > 0 ? PdfMediaTypes.Form : PdfMediaTypes.Document),
            StoreUri = "file:///synthetic.pdf"
        };
    }

    private static DocumentModel CreateDocumentFromState(PdfDocumentState state, string fileName)
    {
        var uri = RepoUri.Parse($"file:///{fileName}");
        var metadata = new Dictionary<string, object?>
        {
            [PdfLoader.StateMetadataKey] = state
        };

        return new DocumentModel(
            uri,
            state.MediaType,
            state.Surface.AssembledText.Text,
            metadata: metadata);
    }

    private static TestFileScope CreateRawFile(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pdf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        File.WriteAllText(filePath, content);
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

    private static DocumentBookmarkNode CreateBookmark(
        string title,
        int pageNumber,
        int level,
        IReadOnlyList<BookmarkNode>? children = null)
    {
        var destination = new ExplicitDestination(
            pageNumber,
            ExplicitDestinationType.FitPage,
            new ExplicitDestinationCoordinates((double?)null));

        return new DocumentBookmarkNode(
            title,
            level,
            destination,
            children ?? []);
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
