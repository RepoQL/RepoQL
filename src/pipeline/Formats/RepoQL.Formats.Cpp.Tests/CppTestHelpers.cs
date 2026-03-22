using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Cpp;

namespace RepoQL.Formats.Cpp.Tests;

internal static class CppTestHelpers
{
    public static string ReadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    public static async Task<Records> LoadRecordsAsync(
        CppMaterializer materializer,
        string fixtureName,
        string? artifactFileName = null)
    {
        var source = ReadFixture(fixtureName);
        using var artifactScope = CreateArtifact(artifactFileName ?? fixtureName, source);
        (await materializer.CanLoadAsync(artifactScope.Artifact).ConfigureAwait(false)).Should().BeTrue();
        var document = await materializer.LoadAsync(artifactScope.Artifact).ConfigureAwait(false);
        return materializer.Materialize(document);
    }

    public static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_cpp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        var parentDir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

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

    internal sealed class ArtifactScope(DiscoveredArtifact artifact, string tempDir, IFileProvider provider) : IDisposable
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
