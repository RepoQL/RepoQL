using System.Text;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.Formats.Rust.Tests;

internal static class RustTestArtifactHelper
{
    public static ArtifactScope CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_rust_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, fileName);
        File.WriteAllText(filePath, content, Encoding.UTF8);

        var fileInfo = new TempFileInfo(filePath, fileName);
        return new ArtifactScope(
            new DiscoveredArtifact
            {
                File = fileInfo,
                RepoUri = RepoUri.Parse($"file:///{fileName}")
            },
            tempDir);
    }

    internal sealed class ArtifactScope(
        DiscoveredArtifact artifact,
        string tempDir) : IDisposable
    {
        public DiscoveredArtifact Artifact { get; } = artifact;
        private readonly string _tempDir = tempDir;

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }

    private sealed class TempFileInfo(string filePath, string name) : IFileInfo
    {
        private readonly FileInfo _fileInfo = new(filePath);
        public bool Exists => _fileInfo.Exists;
        public long Length => _fileInfo.Length;
        public string? PhysicalPath => _fileInfo.FullName;
        public string Name { get; } = name;
        public DateTimeOffset LastModified => _fileInfo.LastWriteTimeUtc;
        public bool IsDirectory => false;

        public Stream CreateReadStream()
            => _fileInfo.OpenRead();
    }
}
