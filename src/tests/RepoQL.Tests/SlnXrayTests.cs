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

internal class SlnXrayTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private sealed class StubClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
            => SemanticMediaType.Create("text", "plain");
    }

    private sealed class Observer(Action onCompleted, Action<Exception> onError, Action<IndexerEvent> onNext)
        : IObserver<IndexerEvent>
    {
        public void OnCompleted() => onCompleted();
        public void OnError(Exception error) => onError(error);
        public void OnNext(IndexerEvent value) => onNext(value);
    }

    [Test]
    public async Task Sln_Indexer_Populates_Xray_And_Items()
    {
        // Arrange: simple solution file
        var fs = new MemoryFileSystem("repo");
        var sln = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        VisualStudioVersion = 17.0.31903.59
        MinimumVisualStudioVersion = 10.0.40219.1
        Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{827E0CD3-B72D-47B6-A68D-7590B98EB39B}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{44D4D51D-72A4-46C2-83A9-E6586050AEA3}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Core", "src\Core\Core.csproj", "{B1121FF9-459C-46EA-BE60-74C4695C74AD}"
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        		Release|Any CPU = Release|Any CPU
        	EndGlobalSection
        	GlobalSection(ProjectConfigurationPlatforms) = postSolution
        		{44D4D51D-72A4-46C2-83A9-E6586050AEA3}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{44D4D51D-72A4-46C2-83A9-E6586050AEA3}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{44D4D51D-72A4-46C2-83A9-E6586050AEA3}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{44D4D51D-72A4-46C2-83A9-E6586050AEA3}.Release|Any CPU.Build.0 = Release|Any CPU
        		{B1121FF9-459C-46EA-BE60-74C4695C74AD}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{B1121FF9-459C-46EA-BE60-74C4695C74AD}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{B1121FF9-459C-46EA-BE60-74C4695C74AD}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{B1121FF9-459C-46EA-BE60-74C4695C74AD}.Release|Any CPU.Build.0 = Release|Any CPU
        	EndGlobalSection
        	GlobalSection(SolutionProperties) = preSolution
        		HideSolutionNode = FALSE
        	EndGlobalSection
        	GlobalSection(NestedProjects) = preSolution
        		{44D4D51D-72A4-46C2-83A9-E6586050AEA3} = {827E0CD3-B72D-47B6-A68D-7590B98EB39B}
        		{B1121FF9-459C-46EA-BE60-74C4695C74AD} = {827E0CD3-B72D-47B6-A68D-7590B98EB39B}
        	EndGlobalSection
        EndGlobal
        """;
        fs.AddOrUpdateText("MySolution.sln", sln);
        var uri = RepoUri.Parse("mem://repo/MySolution.sln");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);

        var registry = new FormatRegistry(
        [
            new(
                    SemanticMediaType.Create("text","plain").WithKind("dotnet.sln"),
                    new SlnLoader(),
                    new NullAnalyzer(SemanticMediaType.Create("text","plain").WithKind("dotnet.sln")),
                    new SlnLoader(),
                    ["sln"])
        ]);

        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        await using var indexer = new RepositoryIndexer(new Core.Metrics.IndexingMetrics(), new System.Diagnostics.Metrics.Meter("RepoQL.Tests.Sln"), hub, store, classifier, registry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);
        var indexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = indexer.Subscribe(new Observer(() => { }, _ => { }, ev =>
        {
            if (ev is Core.IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri.AbsoluteUri)
                indexed.TrySetResult(true);
        }));
        var done = await Task.WhenAny(indexed.Task, Task.Delay(DefaultTimeout));
        if (done != indexed.Task) throw new TimeoutException("Timed out waiting for index");

        // Act
        var doc = store.GetDocumentByUri(uri)!;
        var artifact = store.GetArtifact(doc.ArtifactId!.Value)!;

        // Assert headline/summary/structure
        artifact.Headline!.Should().Contain("dotnet.sln");
        artifact.Headline!.Should().Contain("projects:2");
        artifact.Headline!.Should().Contain("folders:1");
        artifact.Headline!.Should().Contain("configs:2");
        artifact.Summary!.Should().Contain("Format: 12.00");
        artifact.Summary!.Should().Contain("Projects: 2");
        artifact.Summary!.Should().Contain("Solution Folders: 1");
        artifact.Structure!.Should().Contain("Solution Folders:");
        artifact.Structure!.Should().Contain("src");
        artifact.Structure!.Should().Contain("App");
        artifact.Structure!.Should().Contain("Core");

        // Document props include format_version, project_count, folder_count
        var docProps = store.RawQuery("SELECT properties->>'format_version' AS format_version, CAST(properties->>'project_count' AS INTEGER) AS project_count, CAST(properties->>'folder_count' AS INTEGER) AS folder_count FROM node WHERE kind='document' AND lower(uri)=lower(?)", uri.AbsoluteUri).First();
        docProps["format_version"].Should().NotBeNull();
        docProps["format_version"]!.ToString()!.Should().Contain("12.00");
        int.Parse(docProps["project_count"]!.ToString()!).Should().Be(2);
        int.Parse(docProps["folder_count"]!.ToString()!).Should().Be(1);

        await indexer.StopAsync(CancellationToken.None);
    }
}
