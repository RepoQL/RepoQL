using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.DotNet;
using RepoQL.Templating;
using TUnit.Core;

namespace RepoQL.Formats.DotNet.Tests;

internal class AppSettingsLoaderTests
{
    [Test]
    public async Task CanLoadAsync_DetectsAppsettingsJson()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var (artifact, tempPath) = CreateArtifact("appsettings.json", "{}");

        try
        {
            // Act
            var canLoad = await loader.CanLoadAsync(artifact);

            // Assert
            canLoad.Should().BeTrue();
            artifact.MediaType?.Kind.Should().Be("config.appsettings");
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task CanLoadAsync_DetectsAppsettingsDevelopmentJson()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var (artifact, tempPath) = CreateArtifact("appsettings.Development.json", "{}");

        try
        {
            // Act
            var canLoad = await loader.CanLoadAsync(artifact);

            // Assert
            canLoad.Should().BeTrue();
            artifact.MediaType?.Kind.Should().Be("config.appsettings");
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task CanLoadAsync_RejectsOtherJson()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var (artifact, tempPath) = CreateArtifact("package.json", "{}");

        try
        {
            // Act
            var canLoad = await loader.CanLoadAsync(artifact);

            // Assert
            canLoad.Should().BeFalse();
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task Materialize_CreatesHeadlineWithConnectionStrings()
    {
        // Arrange
        var loader = new AppSettingsLoader(new LiquidTemplateRenderer(
            typeof(AppSettingsLoader).Assembly,
            "RepoQL.Formats.DotNet.Templates"));

        var json = """
        {
          "ConnectionStrings": {
            "Default": "Server=localhost;Database=MyDb"
          },
          "Logging": {}
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.Production.json", json);

        try
        {
            var document = await loader.LoadAsync(artifact);

            // Act
            var records = loader.Materialize(document);

            // Assert
            var artifactRecord = records.Artifacts.First();
            artifactRecord.Headline.Should().NotBeNull();
            artifactRecord.Headline!.Should().Contain("Production");
            artifactRecord.Headline!.Should().Contain("config.appsettings");
            artifactRecord.Headline!.Should().Contain("ConnectionStrings");
            artifactRecord.Headline!.Should().Contain("cs:Default");
            artifactRecord.Headline!.Should().Contain("SqlServer");
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task Materialize_CreatesSummaryWithServices()
    {
        // Arrange
        var loader = new AppSettingsLoader(new LiquidTemplateRenderer(
            typeof(AppSettingsLoader).Assembly,
            "RepoQL.Formats.DotNet.Templates"));

        var json = """
        {
          "ConnectionStrings": {
            "Default": "Server=localhost;Database=MyDb",
            "Cache": "localhost:6379"
          },
          "ApplicationInsights": {
            "InstrumentationKey": "key"
          }
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.json", json);

        try
        {
            var document = await loader.LoadAsync(artifact);

            // Act
            var records = loader.Materialize(document);

            // Assert
            var artifactRecord = records.Artifacts.First();
            artifactRecord.Summary.Should().NotBeNull();
            artifactRecord.Summary!.Should().Contain("Connection strings: Default, Cache");
            artifactRecord.Summary!.Should().Contain("Detected services: SqlServer, Redis, AppInsights");
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task Materialize_CreatesDocumentNodeWithProperties()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var json = """
        {
          "ConnectionStrings": {
            "Default": "Server=localhost"
          },
          "Logging": {}
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.Development.json", json);

        try
        {
            var document = await loader.LoadAsync(artifact);

            // Act
            var records = loader.Materialize(document);

            // Assert
            records.Nodes.Should().HaveCount(1);
            var docNode = records.Nodes.First();
            docNode.Kind.Should().Be("document");
            docNode.Props["environment"]?.ToString().Should().Be("Development");
            docNode.Props["top_level_keys"].Should().NotBeNull();
            docNode.Props["connection_strings"].Should().NotBeNull();
            docNode.Props["services"].Should().NotBeNull();
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task LoadAsync_HandlesInvalidJson()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var json = """
        {
          "ConnectionStrings": {
            "Default": "broken
          }
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.json", json);

        try
        {
            // Act - should not throw
            var document = await loader.LoadAsync(artifact);
            var records = loader.Materialize(document);

            // Assert - should create valid records even with invalid JSON
            records.Artifacts.Should().HaveCount(1);
            records.Nodes.Should().HaveCount(1);
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    private static (DiscoveredArtifact artifact, string tempPath) CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);
        File.WriteAllText(tempPath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);

        return (new DiscoveredArtifact
        {
            File = provider.GetFileInfo(fileName),
            RepoUri = RepoUri.Parse($"file:///{fileName}")
        }, tempPath);
    }

    private static void Cleanup(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
