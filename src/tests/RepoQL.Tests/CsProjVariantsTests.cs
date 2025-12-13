using RepoQL.Data.DuckDB;
using AwesomeAssertions;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class CsProjVariantsTests
{
    [Test]
    public async Task CsProj_Library_NotPackable_Surfaces_Fields()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.CsProjVar";
            options.AddCsProjFormat();
        });

        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
            <OutputType>Library</OutputType>
            <IsPackable>false</IsPackable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
          </ItemGroup>
        </Project>
        """;
        var uri = repo.AddOrUpdateText("src/Lib/Lib.csproj", csproj);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;
        artifact.Summary.Should().Contain("OutputType: Library");
        artifact.Summary.Should().Contain("Pack: No");
        artifact.Structure.Should().Contain("TargetFrameworks:");
        artifact.Structure.Should().Contain("net8.0");
        artifact.Structure.Should().Contain("net10.0");
    }
}
