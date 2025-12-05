using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.FileSystem.Embedded;
using RepoQL.Formats.Markdown;
using Assembly = System.Reflection.Assembly;

namespace RepoQL.FileSystem.Tests;

public class MarkdownFileParserTests
{
    [Test]
    public async Task ParseFile_Produces_DocumentChildrenSpansAndEdges()
    {
        // Arrange: locate embedded markdown resource
        var asm = Assembly.GetExecutingAssembly();

        var store = new EmbeddedStore(asm);
        var uriStr = $"embed:///Resources/Sample.md";
        var repoUri = RepoUri.Parse(uriStr);
        var fileInfo = store.GetFile(repoUri);

        var hasher = new XxHasher();
        var hash = await hasher.HashAsync(fileInfo, CancellationToken.None);

        var artifact = new DiscoveredArtifact
        {
            File = fileInfo,
            RepoUri = repoUri,
            Hash = hash,
            MediaType = null
        };

        var loader = new MarkdownLoader();

        // Discover capability and set media type like the indexer does
        var can = await loader.CanLoadAsync(artifact);
        can.Should().BeTrue();
        artifact.MediaType.Should().NotBeNull();
        artifact.MediaType!.Type.Should().Be("text");
        artifact.MediaType!.Subtype.Should().Be("markdown");

        // Act
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        // Assert - artifacts
        records.Artifacts.Length.Should().Be(1);
        var a = records.Artifacts[0];
        a.Digest.Should().StartWith("xxh64:");
        a.MediaType!.Type.Should().Be("text");
        a.MediaType!.Subtype.Should().Be("markdown");

        // Assert - nodes contain document, heading, link, and code block
        var kinds = records.Nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
        kinds.Keys.Should().Contain("document");
        kinds.Keys.Should().Contain("md_heading");
        kinds.Keys.Should().Contain("md_link");
        kinds.Keys.Should().Contain("md_code_block");

        var doc = records.Nodes.First(n => n.Kind == "document");

        // Assert - spans exist and are associated to nodes (via SpanId)
        records.Spans.Length.Should().BeGreaterThan(0);
        var withSpans = records.Nodes.Where(n => n.Kind != "document").Count(n => n.SpanId != null);
        withSpans.Should().BeGreaterThan(0);

        // Assert - edges: HAS_PART count matches number of children
        var hasPart = records.Edges.Where(e => e.Type == "HAS_PART").ToArray();
        hasPart.Length.Should().BeGreaterThan(0);
        hasPart.All(e => e.SrcId == doc.Id && e.IsComposition).Should().BeTrue();

        // Assert - REFERS_TO edge for self-link
        var refers = records.Edges.FirstOrDefault(e => e.Type == "REFERS_TO");
        refers.Should().NotBeNull();
        // sanity: REFERS_TO should not be composition
        refers.IsComposition.Should().BeFalse();
    }

    [Test]
    public async Task CanParseAsync_ByExtension_And_ByExistingMediaType()
    {
        // Arrange
        var asm = Assembly.GetExecutingAssembly();
        var store = new EmbeddedStore(asm);
        var hasher = new XxHasher();

        // Case 1: .md extension should be accepted
        var mdUri = RepoUri.Parse($"embed:///Resources/Sample.md");
        var mdFile = store.GetFile(mdUri);
        var mdHash = await hasher.HashAsync(mdFile, CancellationToken.None);
        var mdArtifact = new DiscoveredArtifact { File = mdFile, RepoUri = mdUri, Hash = mdHash, MediaType = null };

        var loader = new MarkdownLoader();
        var canMd = await loader.CanLoadAsync(mdArtifact);
        canMd.Should().BeTrue();
        mdArtifact.MediaType.Should().NotBeNull();
        mdArtifact.MediaType!.Type.Should().Be("text");
        mdArtifact.MediaType!.Subtype.Should().Be("markdown");

        // Case 2: .txt extension is not accepted unless pre-labeled as markdown
        var txtUri = RepoUri.Parse($"embed:///Resources/JustText.txt");
        var txtFile = store.GetFile(txtUri);
        var txtHash = await hasher.HashAsync(txtFile, CancellationToken.None);

        var txtArtifact = new DiscoveredArtifact { File = txtFile, RepoUri = txtUri, Hash = txtHash, MediaType = null };
        var canTxt = await loader.CanLoadAsync(txtArtifact);
        canTxt.Should().BeFalse();
        txtArtifact.MediaType.Should().BeNull();

        // Now pre-label as markdown; parser should accept and keep that media type
        txtArtifact.MediaType = SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");
        var canTxtLabeled = await loader.CanLoadAsync(txtArtifact);
        canTxtLabeled.Should().BeTrue();
        txtArtifact.MediaType!.Type.Should().Be("text");
        txtArtifact.MediaType!.Subtype.Should().Be("markdown");
        txtArtifact.MediaType!.Kind.Should().Be("markdown.doc");
    }

    [Test]
    public async Task ParseFile_HeadingSlug_CodeBlock_And_Ordinals()
    {
        // Arrange
        var asm = Assembly.GetExecutingAssembly();
        var store = new EmbeddedStore(asm);
        var uri = RepoUri.Parse($"embed:///Resources/Sample.md");
        var file = store.GetFile(uri);
        var hasher = new XxHasher();
        var hash = await hasher.HashAsync(file, CancellationToken.None);
        var artifact = new DiscoveredArtifact { File = file, RepoUri = uri, Hash = hash, MediaType = null };

        var loader = new MarkdownLoader();
        _ = await loader.CanLoadAsync(artifact);
        artifact.MediaType.Should().NotBeNull();

        // Act
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        // Assert document props
        var doc = records.Nodes.First(n => n.Kind == "document");
        doc.Props["media_type"]!.ToString().Should().Contain("text/markdown");
        doc.Props["byte_size"]!.GetValue<long>().Should().BeGreaterThan(0);

        // Heading slug
        var heading = records.Nodes.First(n => n.Kind == "md_heading");
        heading.Props["text"]!.ToString().Should().Be("Getting Started");
        heading.Props["slug"]!.ToString().Should().Be("getting-started");
        heading.Props["level"]!.ToString().Should().Be("1");

        // Code block props
        var code = records.Nodes.First(n => n.Kind == "md_code_block");
        code.Props["language"]!.ToString().Should().Be("js");
        code.Props["fenced"]!.GetValue<bool>().Should().BeTrue();
        int.Parse(code.Props["lines"]!.ToString()).Should().BeGreaterThan(0);

        // Link props
        var link = records.Nodes.First(n => n.Kind == "md_link");
        link.Props["href"]!.ToString().Should().Be("#getting-started");
        link.Props["text"]!.ToString().Should().Be("self link");

        // HAS_PART ordinals are unique and increasing
        var ords = records.Edges.Where(e => e.Type == "HAS_PART").Select(e => e.Ordinal!.Value).ToArray();
        ords.Length.Should().BeGreaterThan(0);
        ords.SequenceEqual(ords.OrderBy(x => x)).Should().BeTrue();
        ords.Distinct().Count().Should().Be(ords.Length);

        // Basic span sanity for heading - note: SpanId references the SectionSpan (heading to next heading/EOF)
        var headingSpan = records.Spans.First(s => s.Id == heading.SpanId);
        headingSpan.StartLine.Should().Be(1);
        headingSpan.EndLine.Should().Be(9); // Section extends to end of document
    }

    [Test]
    public async Task ParseFile_IndentedCodeBlock_FencedFalse_LinesRecorded()
    {
        // Arrange
        var asm = Assembly.GetExecutingAssembly();

        var store = new EmbeddedStore(asm);
        var uri = RepoUri.Parse($"embed:///Resources/IndentedCode.md");
        var file = store.GetFile(uri);
        var hasher = new XxHasher();
        var hash = await hasher.HashAsync(file, CancellationToken.None);
        var artifact = new DiscoveredArtifact { File = file, RepoUri = uri, Hash = hash, MediaType = null };

        var loader = new MarkdownLoader();
        _ = await loader.CanLoadAsync(artifact);
        artifact.MediaType.Should().NotBeNull();

        // Act
        var document = await loader.LoadAsync(artifact);
        var records = loader.Materialize(document);

        // Assert there is an indented code block (fenced=false, language empty)
        var code = records.Nodes.First(n => n.Kind == "md_code_block");
        code.Props["fenced"]!.GetValue<bool>().Should().BeFalse();
        code.Props["language"]!.ToString().Should().Be("");
        int.Parse(code.Props["lines"]!.ToString()).Should().BeGreaterThan(1);
    }
}
