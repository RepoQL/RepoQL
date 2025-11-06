using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Tests.TestHelpers;

/// <summary>
/// Represents the result of processing a file through a format test harness.
/// Provides fluent assertions for common validation scenarios.
/// </summary>
public sealed class FormatTestResult
{
    public IndexItem Item { get; }
    public PipelineResult PipelineResult { get; }
    public SemanticMediaType? MediaType { get; }
    public Records? Records { get; }
    public Annotation[] Annotations { get; }

    internal FormatTestResult(
        IndexItem item,
        PipelineResult pipelineResult,
        SemanticMediaType? mediaType,
        Records? records,
        Annotation[] annotations)
    {
        Item = item;
        PipelineResult = pipelineResult;
        MediaType = mediaType;
        Records = records;
        Annotations = annotations;
    }

    /// <summary>
    /// Returns a fluent assertion context for this result.
    /// </summary>
    public FormatTestResultAssertions Should() => new(this);
}

/// <summary>
/// Provides fluent assertions for FormatTestResult.
/// Common assertions that work across all format types.
/// </summary>
public sealed class FormatTestResultAssertions
{
    private readonly FormatTestResult _result;

    internal FormatTestResultAssertions(FormatTestResult result)
    {
        _result = result;
    }

    /// <summary>
    /// Asserts that the pipeline completed successfully.
    /// </summary>
    public FormatTestResultAssertions HaveSucceeded(string because = "pipeline should complete successfully")
    {
        _result.PipelineResult.Should().Be(PipelineResult.Success, because);
        return this;
    }

    /// <summary>
    /// Asserts that the pipeline failed.
    /// </summary>
    public FormatTestResultAssertions HaveFailed(string because = "pipeline should have failed")
    {
        _result.PipelineResult.Should().Be(PipelineResult.Error, because);
        return this;
    }

    /// <summary>
    /// Asserts that the pipeline was filtered.
    /// </summary>
    public FormatTestResultAssertions HaveBeenFiltered(string because = "pipeline should have filtered the item")
    {
        _result.PipelineResult.Should().Be(PipelineResult.Filtered, because);
        return this;
    }

    /// <summary>
    /// Asserts the media type kind.
    /// </summary>
    public FormatTestResultAssertions WithMediaType(string expectedKind, string because = "media type should be classified correctly")
    {
        _result.MediaType.Should().NotBeNull(because);
        _result.MediaType!.Kind.Should().Be(expectedKind, because);
        return this;
    }

    /// <summary>
    /// Asserts that records were produced.
    /// </summary>
    public FormatTestResultAssertions WithRecords(string because = "records should be produced")
    {
        _result.Records.Should().NotBeNull(because);
        return this;
    }

    /// <summary>
    /// Asserts the number of nodes parsed.
    /// </summary>
    public FormatTestResultAssertions WithNodeCount(int expectedCount, string because = "")
    {
        _result.Records.Should().NotBeNull("records must exist to check node count");
        _result.Records!.Nodes.Length.Should().Be(expectedCount, because);
        return this;
    }

    /// <summary>
    /// Asserts the number of nodes of a specific kind.
    /// </summary>
    public FormatTestResultAssertions WithNodes(string kind, int expectedCount, string? because = null)
    {
        _result.Records.Should().NotBeNull("records must exist to check nodes");
        var actualCount = _result.Records!.Nodes.Count(n => n.Kind == kind);
        actualCount.Should().Be(expectedCount, because ?? $"should have {expectedCount} {kind} nodes");
        return this;
    }

    /// <summary>
    /// Asserts that at least one node of a specific kind exists.
    /// </summary>
    public FormatTestResultAssertions WithNodesOfKind(string kind, string? because = null)
    {
        _result.Records.Should().NotBeNull("records must exist to check nodes");
        var hasNodes = _result.Records!.Nodes.Any(n => n.Kind == kind);
        hasNodes.Should().BeTrue(because ?? $"should have at least one {kind} node");
        return this;
    }

    /// <summary>
    /// Asserts that annotations were produced.
    /// </summary>
    public FormatTestResultAssertions WithAnnotations(string because = "annotations should be produced")
    {
        _result.Annotations.Length.Should().BeGreaterThan(0, because);
        return this;
    }

    /// <summary>
    /// Asserts the number of annotations.
    /// </summary>
    public FormatTestResultAssertions WithAnnotationCount(int expectedCount, string? because = null)
    {
        _result.Annotations.Length.Should().Be(expectedCount, because ?? $"should have {expectedCount} annotations");
        return this;
    }

    /// <summary>
    /// Asserts that an annotation with a specific message pattern exists.
    /// </summary>
    public FormatTestResultAssertions WithAnnotationContaining(string messagePattern, string? because = null)
    {
        var hasAnnotation = _result.Annotations.Any(a => a.Message != null && a.Message.Contains(messagePattern));
        hasAnnotation.Should().BeTrue(because ?? $"should have annotation containing '{messagePattern}'");
        return this;
    }

    /// <summary>
    /// Provides access to the underlying result for custom assertions.
    /// </summary>
    public FormatTestResult And => _result;

    /// <summary>
    /// Allows asserting on a projection of the result using BeEquivalentTo.
    /// </summary>
    public FormatTestResultAssertions MatchingShape(object expectedShape, string? because = null)
    {
        // This allows for backwards compatibility with existing BeEquivalentTo tests
        var actual = new
        {
            PipelineResult = _result.PipelineResult,
            MediaTypeKind = _result.MediaType?.Kind,
            HasRecords = _result.Records != null,
            HasNodes = _result.Records?.Nodes.Length > 0,
            HasAnnotations = _result.Annotations.Length > 0
        };

        actual.Should().BeEquivalentTo(expectedShape, because ?? "result should match expected shape");
        return this;
    }
}
