using System.Diagnostics.CodeAnalysis;
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

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope")]
internal class CsProjVariantsTests
{
    private sealed class StubClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
            => SemanticMediaType.Create("text", "xml");
    }

    [Test]
    public async Task CsProj_Library_NotPackable_Surfaces_Fields()
    {
        var fs = new MemoryFileSystem("repo");
        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
            <OutputType>Library</OutputType>
            <IsPackable>false</IsPackable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />
          </ItemGroup>
        </Project>
        """;
        fs.AddOrUpdateText("src/Lib/Lib.csproj", csproj);
        var uri = RepoUri.Parse("mem://repo/src/Lib/Lib.csproj");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var registry = new FormatRegistry(
        [
            new FormatDescriptor(
                    SemanticMediaType.Create("text","xml").WithKind("dotnet.csproj"),
                    new CsProjLoader(),
                    new CsProjAnalyzer(),
                    new CsProjLoader(),
                    ["csproj"])
        ]);
        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        await using var indexer = new Core.RepositoryIndexer(new Core.Metrics.IndexingMetrics(), new System.Diagnostics.Metrics.Meter("RepoQL.Tests.CsProjVar"), hub, store, classifier, registry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);
        await indexer.WaitForIdle(CancellationToken.None);

        // Verify props/xray
        var doc = store.GetDocumentByUri(uri)!;
        var artifact = store.GetArtifact(doc.ArtifactId!.Value)!;
        artifact.Summary.Should().Contain("OutputType: Library");
        artifact.Summary.Should().Contain("Pack: No");
        artifact.Structure.Should().Contain("TargetFrameworks:");
        artifact.Structure.Should().Contain("net9.0");
        foreach (var row in store.RawQuery("SELECT properties FROM node WHERE id=?", doc.Id))
        {
            Console.WriteLine($"NODE PROPS: {row["properties"]}");
        }

        await indexer.StopAsync(CancellationToken.None);
    }
}
