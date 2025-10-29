using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Formats.DotNet;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class SlnVariantsTests
{
    [Test]
    public async Task Sln_MinimalFormat_Parses()
    {
        await using var repo = await CreateSlnRepoAsync();
        var sln = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        	EndGlobalSection
        EndGlobal
        """;
        var uri = repo.AddOrUpdateText("Test.sln", sln);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;
        artifact.Headline.Should().Contain("dotnet.sln");
        artifact.Headline.Should().Contain("projects:0");
    }

    [Test]
    public async Task Sln_WithSolutionFolders_ParsesFolders()
    {
        await using var repo = await CreateSlnRepoAsync();
        var sln = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder1", "Folder1", "{AAA-BBB}"
        EndProject
        Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Folder2", "Folder2", "{CCC-DDD}"
        EndProject
        Global
        EndGlobal
        """;
        var uri = repo.AddOrUpdateText("Test.sln", sln);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;
        artifact.Headline.Should().Contain("folders:2");
        artifact.Structure.Should().Contain("Folder1");
        artifact.Structure.Should().Contain("Folder2");
    }

    [Test]
    public async Task Sln_WithProjects_ParsesProjects()
    {
        await using var repo = await CreateSlnRepoAsync();
        var sln = """

        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "ProjectA", "src\ProjectA\ProjectA.csproj", "{111-222}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "ProjectB", "src\ProjectB\ProjectB.csproj", "{333-444}"
        EndProject
        Global
        EndGlobal
        """;
        var uri = repo.AddOrUpdateText("Test.sln", sln);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;
        artifact.Headline.Should().Contain("projects:2");
        artifact.Structure.Should().Contain("ProjectA");
        artifact.Structure.Should().Contain("ProjectB");
    }

    private static Task<IndexedRepoBuilder> CreateSlnRepoAsync()
        => IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.SlnVar";
            options.AddFormat(new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln"),
                new SlnLoader(),
                new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln")),
                new SlnLoader(),
                ["sln"]));
        });
}
