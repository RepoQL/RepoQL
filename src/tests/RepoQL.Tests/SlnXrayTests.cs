using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Formats.DotNet;
using RepoQL.Tests.Scaffolding;

namespace RepoQL.Tests;

internal class SlnXrayTests
{
    [Test]
    public async Task Sln_Indexer_Populates_Xray_And_Items()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.Sln";
            options.AddFormat(new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln"),
                new SlnLoader(),
                new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln")),
                new SlnLoader(),
                ["sln"]));
        });

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
        var uri = repo.AddOrUpdateText("MySolution.sln", sln);

        await repo.IndexAsync();

        var doc = repo.Store.GetDocumentByUri(uri)!;
        var artifact = repo.Store.GetArtifact(doc.ArtifactId!.Value)!;

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

        var docProps = repo.Store.RawQuery("SELECT properties->>'format_version' AS format_version, CAST(properties->>'project_count' AS INTEGER) AS project_count, CAST(properties->>'folder_count' AS INTEGER) AS folder_count FROM node WHERE kind='document' AND lower(uri)=lower(?)", uri.AbsoluteUri).First();
        docProps["format_version"].Should().NotBeNull();
        docProps["format_version"]!.ToString()!.Should().Contain("12.00");
        int.Parse(docProps["project_count"]!.ToString()!).Should().Be(2);
        int.Parse(docProps["folder_count"]!.ToString()!).Should().Be(1);
    }
}
