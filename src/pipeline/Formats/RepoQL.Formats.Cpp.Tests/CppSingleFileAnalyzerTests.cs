using System.Text.Json.Nodes;
using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Formats.Cpp.Analysis;
using RepoQL.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppSingleFileAnalyzerTests
{
    [Test]
    public async Task Analyzer_CreatesIncludeEdges_AndMarksResolution()
    {
        using var materializer = new CppMaterializer();

        var source = CppTestHelpers.ReadFixture("plan02_preprocessor_nodes.hpp");
        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_preprocessor_nodes.hpp");
        var item = BuildIndexItem(
            uri: "file:///plan02_preprocessor_nodes.hpp",
            content: source,
            records: records);

        var registry = new UriRegistry();
        registry.TryRegisterDiscovered(RepoUri.Parse("file:///pool.h"));

        var analyzer = new CppSingleFileAnalyzer(registry);
        var (result, status) = await analyzer.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();

        var includeEdges = item.Records!.Edges.Where(e => e.Type == "REFERS_TO").ToArray();
        includeEdges.Should().NotBeEmpty();
        includeEdges.Should().Contain(e =>
            e.Props["target"]!.ToString() == "pool.h"
            && e.Props["is_resolved"]!.ToString() == "true");
        includeEdges.Should().Contain(e =>
            e.Props["target"]!.ToString() == "vector"
            && e.Props["is_resolved"]!.ToString() == "false");
    }

    [Test]
    public async Task Analyzer_ExtractsDocComments_AndAttributes()
    {
        using var materializer = new CppMaterializer();

        var source = CppTestHelpers.ReadFixture("plan02_doc_attributes_tests.cpp");
        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_doc_attributes_tests.cpp");
        var item = BuildIndexItem(
            uri: "file:///plan02_doc_attributes_tests.cpp",
            content: source,
            records: records);

        var analyzer = new CppSingleFileAnalyzer();
        var (result, status) = await analyzer.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();

        var compute = item.Records!.Nodes.Single(n => Prop(n, "name") == "compute");
        Prop(compute, "doc_comment").Should().Contain("@brief Computes a value.");
        compute.Props["doc_tags"].Should().NotBeNull();

        var computeAttrs = compute.Props["attributes"]!.AsArray();
        computeAttrs.Select(v => v!.ToString()).Should().Contain("nodiscard");

        var deprecated = item.Records.Nodes.Single(n => Prop(n, "name") == "old_api");
        var deprecatedAttrs = deprecated.Props["attributes"]!.AsArray();
        var hasDeprecatedReason = deprecatedAttrs.Any(v =>
            v is JsonObject obj
            && obj["name"]!.ToString() == "deprecated"
            && obj["reason"]!.ToString() == "use newer_api");
        hasDeprecatedReason.Should().BeTrue();
    }

    [Test]
    public async Task Analyzer_DetectsTestFramework_MarksNodeAsTest()
    {
        using var materializer = new CppMaterializer();

        var source = CppTestHelpers.ReadFixture("plan02_doc_attributes_tests.cpp");
        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_doc_attributes_tests.cpp");
        var item = BuildIndexItem(
            uri: "file:///plan02_doc_attributes_tests.cpp",
            content: source,
            records: records);

        var analyzer = new CppSingleFileAnalyzer();
        var (result, status) = await analyzer.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Should().Contain(a =>
            a.RuleId == "cpp/test_framework"
            && a.Data["test_name"]!.ToString() == "HandlesCase");
        var hasTestNode = item.Records!.Nodes.Any(n =>
            n.Props["is_test"] is not null
            && n.Props["is_test"]!.ToString() == "true");
        hasTestNode.Should().BeTrue();
    }

    [Test]
    public async Task Analyzer_FailureIsolation_ContinuesAfterStepError()
    {
        using var materializer = new CppMaterializer();

        var records = await CppTestHelpers.LoadRecordsAsync(materializer, "plan02_preprocessor_nodes.hpp");
        var item = BuildIndexItemWithThrowingStream(
            uri: "file:///plan02_preprocessor_nodes.hpp",
            records: records);

        var analyzer = new CppSingleFileAnalyzer();
        var (result, status) = await analyzer.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Should().Contain(a => a.RuleId == "cpp/analysis_failure");
        var hasIncludeEdge = item.Records!.Edges.Any(e =>
            e.Type == "REFERS_TO"
            && e.Props["target"] is not null
            && e.Props["target"]!.ToString() == "pool.h");
        hasIncludeEdge.Should().BeTrue();
    }

    private static IndexItem BuildIndexItem(string uri, string content, Records records)
    {
        var builder = RepoQL.Testing.Indexing.IndexingTestItemFactory.Builder()
            .WithUri(uri)
            .WithContent(content);
        var item = builder.Build();
        item.MediaType = SemanticMediaType.Create("text", "plain").WithKind("code.cpp-header");
        item.Records = records;
        return item;
    }

    private static IndexItem BuildIndexItemWithThrowingStream(string uri, Records records)
    {
        var file = A.Fake<IFileInfo>();
        A.CallTo(() => file.Name).Returns("broken.hpp");
        A.CallTo(() => file.Exists).Returns(true);
        A.CallTo(() => file.Length).Returns(0);
        A.CallTo(() => file.LastModified).Returns(DateTimeOffset.UtcNow);
        A.CallTo(() => file.IsDirectory).Returns(false);
        A.CallTo(() => file.PhysicalPath).Returns("broken.hpp");
        A.CallTo(() => file.CreateReadStream()).Throws<IOException>();

        var fs = A.Fake<IVirtualFileSystem>();
        A.CallTo(() => fs.GetUri(file)).Returns(RepoUri.Parse(uri));

        var item = new IndexItem(new RawArtifact(file, fs), IndexItemOptions.Default)
        {
            MediaType = SemanticMediaType.Create("text", "plain").WithKind("code.cpp-header"),
            Records = records
        };
        return item;
    }

    private static string Prop(RepoQL.Contracts.Models.Node node, string key)
        => node.Props[key]?.ToString() ?? string.Empty;

    private static Task<(Annotation[]? Result, PipelineResult PipelineStatus)> Next(IParsedArtifact _)
        => Task.FromResult<(Annotation[]?, PipelineResult)>((Array.Empty<Annotation>(), PipelineResult.Success));
}
