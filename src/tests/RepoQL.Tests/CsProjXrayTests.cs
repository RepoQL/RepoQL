using System.Diagnostics;
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

internal class CsProjXrayTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private sealed class StubClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
            => SemanticMediaType.Create("text", "xml");
    }

    private sealed class Observer(Action onCompleted, Action<Exception> onError, Action<IndexerEvent> onNext)
        : IObserver<IndexerEvent>
    {
        public void OnCompleted() => onCompleted();
        public void OnError(Exception error) => onError(error);
        public void OnNext(IndexerEvent value) => onNext(value);
    }

    [Test]
    public async Task CsProj_Indexer_Populates_Xray_And_Items()
    {
        // Arrange: simple csproj
        var fs = new MemoryFileSystem("repo");
        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net9.0</TargetFramework>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <OutputType>Exe</OutputType>
            <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="3.1.0" />
            <PackageReference Include="Dapper" />
            <ProjectReference Include="..\\Other\\Other.csproj" />
          </ItemGroup>
        </Project>
        """;
        fs.AddOrUpdateText("src/App/App.csproj", csproj);
        var uri = RepoUri.Parse("mem://repo/src/App/App.csproj");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);

        var registry = new FormatRegistry(
        [
            new(
                    SemanticMediaType.Create("text","xml").WithKind("dotnet.csproj"),
                    new CsProjLoader(),
                    new CsProjAnalyzer(),
                    new CsProjLoader(),
                    ["csproj"])
        ]);

        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        await using var indexer = new RepositoryIndexer(new Core.Metrics.IndexingMetrics(), new System.Diagnostics.Metrics.Meter("RepoQL.Tests.CsProj"), hub, store, classifier, registry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

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
        artifact.Headline!.Should().Contain("dotnet.csproj");
        artifact.Headline!.Should().Contain("packages:2");
        artifact.Summary!.Should().Contain("Serilog");
        artifact.Structure!.Should().Contain("PackageReference");
        artifact.Summary.Should().Contain("SDK:");
        artifact.Summary.Should().Contain("OutputType: Exe");

        // Document props include sdk/output_type/pack
        var docProps = store.RawQuery("SELECT properties->>'sdk' AS sdk, properties->>'output_type' AS output_type, CAST(coalesce(properties->>'pack','false') AS VARCHAR) AS pack FROM node WHERE kind='document' AND lower(uri)=lower(?)", uri.AbsoluteUri).First();
        docProps["sdk"].Should().NotBeNull();
        docProps["output_type"]!.ToString()!.ToLowerInvariant().Should().Be("exe");
        docProps["pack"]!.ToString()!.ToLowerInvariant().Should().BeOneOf("true","yes");

        await indexer.WaitForStagesIdleAsync(PipelineStage.Analysis, CancellationToken.None);

        // Analyzer: one unpinned package (Dapper)
        var sw = Stopwatch.StartNew();
        IReadOnlyDictionary<string, object?>[] ann;
        do
        {
            ann = store.RawQuery("SELECT kind,severity,message FROM annotations_for(?, 'lint', 'hint')", uri.AbsoluteUri).ToArray();
            if (ann.Length > 0) break;
            await Task.Delay(50, CancellationToken.None);
        } while (sw.Elapsed < DefaultTimeout);
        ann.Length.Should().BeGreaterThan(0);
        ann.Any(r => r["message"]!.ToString()!.Contains("Dapper", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();

        await indexer.StopAsync(CancellationToken.None);
    }
}
