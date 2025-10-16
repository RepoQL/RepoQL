using AwesomeAssertions;
using RepoQL.Core.Analysis;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;
using RepoQL.Formats.DotNet;

namespace RepoQL.Tests;

internal class SlnVariantsTests
{
    private sealed class StubClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
            => SemanticMediaType.Create("text", "plain");
    }

    [Test]
    public async Task Sln_MinimalFormat_Parses()
    {
        // Minimal solution with no projects
        var sln = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        	EndGlobalSection
        EndGlobal
        """;

        var fs = new MemoryFileSystem("repo");
        fs.AddOrUpdateText("Test.sln", sln);
        var uri = RepoUri.Parse("mem://repo/Test.sln");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var registry = new FormatRegistry(
        [
            new FormatDescriptor(
                    SemanticMediaType.Create("text","plain").WithKind("dotnet.sln"),
                    new SlnLoader(),
                    new NullAnalyzer(SemanticMediaType.Create("text","plain").WithKind("dotnet.sln")),
                    new SlnLoader(),
                    ["sln"])
        ]);
        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        await using var indexer = new RepositoryIndexer(new Core.Metrics.IndexingMetrics(), new System.Diagnostics.Metrics.Meter("RepoQL.Tests.SlnVar"), hub, store, classifier, registry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);
        await indexer.WaitForIdle(CancellationToken.None);

        var doc = store.GetDocumentByUri(uri)!;
        var artifact = store.GetArtifact(doc.ArtifactId!.Value)!;
        artifact.Headline.Should().Contain("dotnet.sln");
        artifact.Headline.Should().Contain("projects:0");

        await indexer.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Sln_WithSolutionFolders_ParsesFolders()
    {
        var sln = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder1", "Folder1", "{AAA-BBB}"
        EndProject
        Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder2", "Folder2", "{CCC-DDD}"
        EndProject
        Global
        EndGlobal
        """;

        var fs = new MemoryFileSystem("repo");
        fs.AddOrUpdateText("Test.sln", sln);
        var uri = RepoUri.Parse("mem://repo/Test.sln");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var registry = new FormatRegistry(
        [
            new FormatDescriptor(
                    SemanticMediaType.Create("text","plain").WithKind("dotnet.sln"),
                    new SlnLoader(),
                    new NullAnalyzer(SemanticMediaType.Create("text","plain").WithKind("dotnet.sln")),
                    new SlnLoader(),
                    ["sln"])
        ]);
        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        await using var indexer = new RepositoryIndexer(new Core.Metrics.IndexingMetrics(), new System.Diagnostics.Metrics.Meter("RepoQL.Tests.SlnVar"), hub, store, classifier, registry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);
        await indexer.WaitForIdle(CancellationToken.None);

        var doc = store.GetDocumentByUri(uri)!;
        var artifact = store.GetArtifact(doc.ArtifactId!.Value)!;
        artifact.Headline.Should().Contain("folders:2");
        artifact.Structure.Should().Contain("Folder1");
        artifact.Structure.Should().Contain("Folder2");

        await indexer.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Sln_WithProjects_ParsesProjects()
    {
        var sln = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "ProjectA", "src\ProjectA\ProjectA.csproj", "{111-222}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "ProjectB", "src\ProjectB\ProjectB.csproj", "{333-444}"
        EndProject
        Global
        EndGlobal
        """;

        var fs = new MemoryFileSystem("repo");
        fs.AddOrUpdateText("Test.sln", sln);
        var uri = RepoUri.Parse("mem://repo/Test.sln");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var registry = new FormatRegistry(
        [
            new FormatDescriptor(
                    SemanticMediaType.Create("text","plain").WithKind("dotnet.sln"),
                    new SlnLoader(),
                    new NullAnalyzer(SemanticMediaType.Create("text","plain").WithKind("dotnet.sln")),
                    new SlnLoader(),
                    ["sln"])
        ]);
        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        await using var indexer = new RepositoryIndexer(new Core.Metrics.IndexingMetrics(), new System.Diagnostics.Metrics.Meter("RepoQL.Tests.SlnVar"), hub, store, classifier, registry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);
        await indexer.WaitForIdle(CancellationToken.None);

        var doc = store.GetDocumentByUri(uri)!;
        var artifact = store.GetArtifact(doc.ArtifactId!.Value)!;
        artifact.Headline.Should().Contain("projects:2");
        artifact.Structure.Should().Contain("ProjectA");
        artifact.Structure.Should().Contain("ProjectB");

        await indexer.StopAsync(CancellationToken.None);
    }
}
