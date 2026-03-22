using System.Text;
using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Cpp.Tests;

public sealed class CppClassifierTests
{
    [Test]
    [Arguments("main.c", "code.c")]
    [Arguments("main.cpp", "code.cpp")]
    [Arguments("main.cc", "code.cpp")]
    [Arguments("main.cxx", "code.cpp")]
    [Arguments("main.hpp", "code.cpp-header")]
    [Arguments("main.hh", "code.cpp-header")]
    [Arguments("main.hxx", "code.cpp-header")]
    [Arguments("main.ipp", "code.cpp-inline")]
    [Arguments("main.tpp", "code.cpp-inline")]
    [Arguments("main.inl", "code.cpp-inline")]
    public async Task Classifier_MapsKnownExtensions(string fileName, string expectedKind)
    {
        var classifier = new CppClassifier();
        var item = CreateFakeArtifact(fileName, "int add(int a, int b);");

        var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be(expectedKind);
    }

    [Test]
    [Arguments("class Widget {};")]
    [Arguments("namespace net { int value = 0; }")]
    [Arguments("template <typename T> class Box {};")]
    [Arguments("using namespace std;")]
    [Arguments("#include <iostream>")]
    public async Task Classifier_DotH_ContentSniffing_PromotesToCppHeader(string content)
    {
        var classifier = new CppClassifier();
        var item = CreateFakeArtifact("widget.h", content);

        var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be("code.cpp-header");
    }

    [Test]
    public async Task Classifier_DotH_WithCppSibling_PromotesToCppHeader()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_cpp_classifier_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var headerPath = Path.Combine(tempDir, "service.h");
            var siblingPath = Path.Combine(tempDir, "service.cpp");
            File.WriteAllText(headerPath, "int add(int a, int b);", Encoding.UTF8);
            File.WriteAllText(siblingPath, "int add(int a, int b) { return a + b; }", Encoding.UTF8);

            var classifier = new CppClassifier();
            var item = CreateFakeArtifact("service.h", "int add(int a, int b);", physicalPath: headerPath);

            var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

            status.Should().Be(PipelineResult.Success);
            result.Should().NotBeNull();
            result!.Kind.Should().Be("code.cpp-header");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Classifier_DotH_WithoutIndicators_DefaultsToC()
    {
        var classifier = new CppClassifier();
        var item = CreateFakeArtifact("legacy.h", "int add(int a, int b);");

        var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be("code.c");
    }

    [Test]
    public async Task Classifier_DotH_ContentSniffIoError_FallsBackToExtensionClassification()
    {
        var classifier = new CppClassifier();
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns("broken.h");
        A.CallTo(() => item.PhysicalPath).Returns(null);
        A.CallTo(() => item.CreateReadStream()).Throws<IOException>();

        var (result, status) = await classifier.ProcessAsync(item, Next, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be("code.c");
    }

    [Test]
    public async Task Classifier_PassesUnrecognizedExtensionsToNext()
    {
        var classifier = new CppClassifier();
        var item = CreateFakeArtifact("README.md", "# docs");
        var nextCalled = false;

        var (result, status) = await classifier.ProcessAsync(
            item,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().BeNull();
        status.Should().Be(PipelineResult.Success);
    }

    private static IDiscoveredArtifact CreateFakeArtifact(string fileName, string content, string? physicalPath = null)
    {
        var item = A.Fake<IDiscoveredArtifact>();
        A.CallTo(() => item.Name).Returns(fileName);
        A.CallTo(() => item.PhysicalPath).Returns(physicalPath);
        A.CallTo(() => item.CreateReadStream()).ReturnsLazily(() => new MemoryStream(Encoding.UTF8.GetBytes(content)));
        return item;
    }

    private static Task<(SemanticMediaType?, PipelineResult)> Next(IDiscoveredArtifact _)
        => Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
}
