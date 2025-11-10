using System.Diagnostics;
using AwesomeAssertions;
using RepoQL.Formats.Markdown;
using RepoQL.Testing.Formats;

namespace RepoQL.Indexing.Tests.Formats;

public class MarkdownIntegrationTests : FormatIntegrationTestBase
{
    private const string SampleMarkdown = @"# Test Document

This is a test markdown document.

## Section 1

Some content here.

## Section 2

More content.

[Link to Section 1](#section-1)
[Broken Link](#nonexistent)
";

    [Test]
    [DisplayName("Successfully processes markdown file through classification and parsing")]
    public async Task Given_MarkdownFile_When_ProcessedThroughPipeline_Then_ProducesRecords()
    {
        // Arrange
        var markdownLoader = new MarkdownLoader(CreateLogger<MarkdownLoader>());
        var harness = CreateHarness()
            .WithClassifier(new MarkdownClassifier(CreateLogger<MarkdownClassifier>()))
            .WithParser(new MarkdownParser(markdownLoader, CreateLogger<MarkdownParser>()))
            .Build();

        // Act
        var result = await harness.ProcessFileAsync("test.md", SampleMarkdown);

        // Assert
        result.Should()
            .HaveSucceeded()
            .WithMediaType("markdown.doc")
            .WithRecords()
            .WithNodes("md_heading", 3)
            .WithNodes("md_link", 2);

        // Verify DocumentModel was stored in metadata
        result.Item.TryGetValue("document_model", out var docModel).Should().BeTrue();
        docModel.Should().NotBeNull();
    }

    /*
    [Test]
    [DisplayName("End-to-end: Processes markdown through full IndexingEngine pipeline")]
    public async Task Given_MarkdownFile_When_ProcessedThroughEngine_Then_SuccessfullyIndexed()
    {
        // Arrange
        var markdownLoader = new MarkdownLoader(CreateLogger<MarkdownLoader>());
        var harness = CreateHarness()
            .WithClassifier(new MarkdownClassifier(CreateLogger<MarkdownClassifier>()))
            .WithParser(new MarkdownParser(markdownLoader, CreateLogger<MarkdownParser>()))
            .WithAnalyzer(new MarkdownAnalysisProcessor(
                new MarkdownAnalyzer(),
                CreateAnalyzerContextFactory(),
                CreateLogger<MarkdownAnalysisProcessor>()))
            .Build();

        // Act
        var result = await harness.ProcessFileAsync("test.md", SampleMarkdown);

        // Assert
        result.Should()
            .HaveSucceeded()
            .WithMediaType("markdown.doc")
            .WithRecords()
            .WithAnnotations()
            .WithAnnotationContaining("nonexistent");
    }

    [Test]
    [DisplayName("Demonstrates ergonomics: Setting up markdown support")]
    public void Given_NoSetup_When_WiringUpMarkdown_Then_ErgonomicsAreClear()
    {
        // Arrange - Create harness with markdown processors
        var markdownLoader = new MarkdownLoader(CreateLogger<MarkdownLoader>());
        var harness = CreateHarness()
            .WithClassifier(new MarkdownClassifier(CreateLogger<MarkdownClassifier>()))
            .WithParser(new MarkdownParser(markdownLoader, CreateLogger<MarkdownParser>()))
            .WithAnalyzer(new MarkdownAnalysisProcessor(
                new MarkdownAnalyzer(),
                CreateLogger<MarkdownAnalysisProcessor>()))
            .Build();

        // Assert - Harness should be configured
        harness.Should().NotBeNull("harness should be created successfully");

        // Note: Test infrastructure makes format support setup clear and concise
        // All common boilerplate is handled by FormatTestHarness and base class
    }*/
}
