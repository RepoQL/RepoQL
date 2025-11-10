using System;
using System.Linq;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Testing.Formats;

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

    /// <summary>Returns a fluent assertion context for this result.</summary>
    public FormatTestResultAssertions Should() => new(this);
}

/// <summary>Fluent assertions tailored to format test results.</summary>
public sealed class FormatTestResultAssertions
{
    private readonly FormatTestResult _result;

    internal FormatTestResultAssertions(FormatTestResult result)
    {
        _result = result;
    }

    public FormatTestResultAssertions HaveSucceeded(string because = "pipeline should complete successfully")
    {
        _result.PipelineResult.Should().Be(PipelineResult.Success, because);
        return this;
    }

    public FormatTestResultAssertions HaveFailed(string because = "pipeline should have failed")
    {
        _result.PipelineResult.Should().Be(PipelineResult.Error, because);
        return this;
    }

    public FormatTestResultAssertions HaveBeenFiltered(string because = "pipeline should have filtered the item")
    {
        _result.PipelineResult.Should().Be(PipelineResult.Filtered, because);
        return this;
    }

    public FormatTestResultAssertions WithMediaType(string expectedKind, string because = "media type should be classified correctly")
    {
        _result.MediaType.Should().NotBeNull(because);
        _result.MediaType!.Kind.Should().Be(expectedKind, because);
        return this;
    }

    public FormatTestResultAssertions WithRecords(string because = "records should be produced")
    {
        _result.Records.Should().NotBeNull(because);
        return this;
    }

    public FormatTestResultAssertions WithNodeCount(int expectedCount, string because = "")
    {
        _result.Records.Should().NotBeNull("records must exist to check node count");
        _result.Records!.Nodes.Length.Should().Be(expectedCount, because);
        return this;
    }

    public FormatTestResultAssertions WithNodes(string kind, int expectedCount, string? because = null)
    {
        _result.Records.Should().NotBeNull("records must exist to check nodes");
        var actualCount = _result.Records!.Nodes.Count(n => n.Kind == kind);
        actualCount.Should().Be(expectedCount, because ?? $"should have {expectedCount} {kind} nodes");
        return this;
    }

    public FormatTestResultAssertions WithNodesOfKind(string kind, string? because = null)
    {
        _result.Records.Should().NotBeNull("records must exist to check nodes");
        var hasNodes = _result.Records!.Nodes.Any(n => n.Kind == kind);
        hasNodes.Should().BeTrue(because ?? $"should have at least one {kind} node");
        return this;
    }

    public FormatTestResultAssertions WithAnnotations(string because = "annotations should be produced")
    {
        _result.Annotations.Length.Should().BeGreaterThan(0, because);
        return this;
    }

    public FormatTestResultAssertions WithAnnotationCount(int expectedCount, string? because = null)
    {
        _result.Annotations.Length.Should().Be(expectedCount, because ?? $"should have {expectedCount} annotations");
        return this;
    }

    public FormatTestResultAssertions WithAnnotationContaining(string messagePattern, string? because = null)
    {
        var hasAnnotation = _result.Annotations.Any(a =>
            a.Message != null && a.Message.Contains(messagePattern, StringComparison.OrdinalIgnoreCase));
        hasAnnotation.Should().BeTrue(because ?? $"should have annotation containing '{messagePattern}'");
        return this;
    }

    public FormatTestResult And => _result;

    public FormatTestResultAssertions MatchingShape(object expectedShape, string? because = null)
    {
        var actual = new
        {
            _result.PipelineResult,
            MediaTypeKind = _result.MediaType?.Kind,
            HasRecords = _result.Records is not null,
            HasNodes = _result.Records?.Nodes.Length > 0,
            HasAnnotations = _result.Annotations.Length > 0
        };

        actual.Should().BeEquivalentTo(expectedShape, because ?? "result should match expected shape");
        return this;
    }
}
