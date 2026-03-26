using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet.Tests;

public class CSharpWorkspaceHostTests
{
    [Test]
    [DisplayName("Reuses cached compilation for repeated analysis in the same project")]
    public async Task Given_SameProject_When_AnalyzingTwice_Then_CompilationBuildsOnce()
    {
        if (!CSharpWorkspaceHost.IsSdkAvailable)
            return;

        using var workspace = new TempProjectScope("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """,
            """
            namespace Demo;

            public class Sample
            {
                public int Add(int left, int right) => left + right;
            }
            """);

        using var host = new CSharpWorkspaceHost(workspaceFactory: null, logger: NullLogger<CSharpWorkspaceHost>.Instance);
        var text = File.ReadAllText(workspace.SourceFilePath);
        var lineMap = new TextLineMap(text);
        var surface = new CSharpDocumentSurface
        {
            DocumentId = Guid.NewGuid(),
            DocumentProperties = new JsonObject(),
            Namespaces = [],
            Types = [],
            Members = [],
            Usings = []
        };

        var first = await host.TryAnalyzeAsync(workspace.SourceFilePath, surface, lineMap, CancellationToken.None);
        var second = await host.TryAnalyzeAsync(workspace.SourceFilePath, surface, lineMap, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        host.GetProjectLoadCount(workspace.ProjectFilePath).Should().Be(1);
        host.GetCompilationBuildCount(workspace.ProjectFilePath).Should().Be(1);
    }

    [Test]
    [DisplayName("Reloads the cached project when the analyzed source file changes on disk")]
    public async Task Given_SourceFileChanges_When_AnalyzingAgain_Then_ProjectAndCompilationReload()
    {
        if (!CSharpWorkspaceHost.IsSdkAvailable)
            return;

        using var workspace = new TempProjectScope("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """,
            """
            namespace Demo;

            public class Sample
            {
                public int Add(int left, int right) => left + right;
            }
            """);

        using var host = new CSharpWorkspaceHost(workspaceFactory: null, logger: NullLogger<CSharpWorkspaceHost>.Instance);
        var surface = CreateSurface();
        var firstText = File.ReadAllText(workspace.SourceFilePath);

        var first = await host.TryAnalyzeAsync(
            workspace.SourceFilePath,
            surface,
            new TextLineMap(firstText),
            CancellationToken.None);

        const string updatedSource = """
            namespace Demo;

            public class Sample
            {
                public int Add(int left, int right) => left - right;
            }
            """;
        File.WriteAllText(workspace.SourceFilePath, updatedSource);
        File.SetLastWriteTimeUtc(workspace.SourceFilePath, DateTime.UtcNow.AddSeconds(2));

        var second = await host.TryAnalyzeAsync(
            workspace.SourceFilePath,
            CreateSurface(),
            new TextLineMap(updatedSource),
            CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        host.GetProjectLoadCount(workspace.ProjectFilePath).Should().Be(2);
        host.GetCompilationBuildCount(workspace.ProjectFilePath).Should().Be(2);
    }

    private static CSharpDocumentSurface CreateSurface()
    {
        return new CSharpDocumentSurface
        {
            DocumentId = Guid.NewGuid(),
            DocumentProperties = new JsonObject(),
            Namespaces = [],
            Types = [],
            Members = [],
            Usings = []
        };
    }

    private sealed class TempProjectScope : IDisposable
    {
        public TempProjectScope(string projectText, string sourceText)
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"repoql_csharp_host_{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            ProjectFilePath = Path.Combine(DirectoryPath, "TestProject.csproj");
            SourceFilePath = Path.Combine(DirectoryPath, "Sample.cs");
            File.WriteAllText(ProjectFilePath, projectText);
            File.WriteAllText(SourceFilePath, sourceText);
        }

        public string DirectoryPath { get; }
        public string ProjectFilePath { get; }
        public string SourceFilePath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
