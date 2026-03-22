using System.Text;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.Go.Tests;

internal static class GoTestHelpers
{
    public static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    public static async Task<Records> LoadRecordsAsync(string fixtureName, string? artifactFileName = null)
    {
        using var loader = new GoLoader();
        var source = ReadFixture(fixtureName);
        using var artifactScope = CreateArtifact(artifactFileName ?? fixtureName, source);
        if (!await loader.CanLoadAsync(artifactScope.Artifact).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Fixture {fixtureName} was not recognized as a Go artifact.");
        }

        var document = await loader.LoadAsync(artifactScope.Artifact).ConfigureAwait(false);
        return loader.Materialize(document);
    }

    private static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_go_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        File.WriteAllText(filePath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);
        return new ArtifactScope(
            new DiscoveredArtifact
            {
                File = provider.GetFileInfo(fileName),
                RepoUri = RepoUri.Parse($"file:///{fileName}")
            },
            tempDir,
            provider);
    }

    private sealed class ArtifactScope(DiscoveredArtifact artifact, string tempDir, IFileProvider provider) : IDisposable
    {
        public DiscoveredArtifact Artifact { get; } = artifact;
        private readonly string _tempDir = tempDir;
        private readonly IFileProvider _provider = provider;

        public void Dispose()
        {
            (_provider as IDisposable)?.Dispose();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }
}
