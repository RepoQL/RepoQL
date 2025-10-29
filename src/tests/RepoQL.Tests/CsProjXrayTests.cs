using System.Diagnostics;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Formats.DotNet;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class CsProjXrayTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task CsProj_Indexer_Populates_Xray_And_Items()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.CsProj";
            options.AddFormat(new FormatDescriptor(
                SemanticMediaType.Create("text", "xml").WithKind("dotnet.csproj"),
                new CsProjLoader(),
                new CsProjAnalyzer(),
                new CsProjLoader(),
                ["csproj"]));
        });

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
        var uri = repo.AddOrUpdateText("src/App/App.csproj", csproj);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;

        artifact.Headline!.Should().Contain("dotnet.csproj");
        artifact.Headline!.Should().Contain("packages:2");
        artifact.Summary!.Should().Contain("Serilog");
        artifact.Structure!.Should().Contain("PackageReference");
        artifact.Summary.Should().Contain("SDK:");
        artifact.Summary.Should().Contain("OutputType: Exe");

        var docProps = repo.Store.RawQuery(
            "SELECT properties->>'sdk' AS sdk, properties->>'output_type' AS output_type, CAST(coalesce(properties->>'pack','false') AS VARCHAR) AS pack FROM node WHERE kind='document' AND lower(uri)=lower(?)",
            uri.AbsoluteUri).First();
        docProps["sdk"].Should().NotBeNull();
        docProps["output_type"]!.ToString()!.ToLowerInvariant().Should().Be("exe");
        docProps["pack"]!.ToString()!.ToLowerInvariant().Should().BeOneOf("true", "yes");

        await repo.WaitForStagesIdleAsync(PipelineStage.Analysis, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        IReadOnlyDictionary<string, object?>[] ann;
        do
        {
            ann = repo.Store.RawQuery("SELECT kind,severity,message FROM annotations_for(?, 'lint', 'hint')", uri.AbsoluteUri).ToArray();
            if (ann.Length > 0) break;
            await Task.Delay(50, CancellationToken.None);
        } while (sw.Elapsed < DefaultTimeout);
        ann.Length.Should().BeGreaterThan(0);
        ann.Any(r => r["message"]!.ToString()!.Contains("Dapper", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
    }
}
