using System.Text;
using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.Indexing.Tests.Indexing;

public class RawArtifactDigestTests
{
    [Test]
    [DisplayName("Uses full digest for files at or below the sampling threshold")]
    public async Task Given_SmallFile_When_ComputingDigest_Then_UsesFullContentDigest()
    {
        var content = Encoding.UTF8.GetBytes("hello world");
        var artifact = CreateRawArtifact("file:///repo/small.log", content);

        var digest = await artifact.Digest.WithCancellation(CancellationToken.None);

        digest.Should().Be(ContentDigest.FromBytes(content));
    }

    [Test]
    [DisplayName("Uses sampled digest for large files and ignores middle-only changes")]
    public async Task Given_LargeFilesDifferOnlyInMiddle_When_ComputingDigest_Then_DigestsMatch()
    {
        using var tempDir = new TemporaryDirectory();
        var firstPath = CreateLargeFile(tempDir.Path, "first.bin", "HEAD-A", "middle-a", "TAIL-A", FileDigest.SampledDigestThresholdBytes + 1024);
        var secondPath = CreateLargeFile(tempDir.Path, "second.bin", "HEAD-A", "middle-b", "TAIL-A", FileDigest.SampledDigestThresholdBytes + 1024);

        var first = CreateRawArtifact("file:///repo/first.bin", firstPath, DateTimeOffset.UtcNow.AddDays(-2));
        var second = CreateRawArtifact("file:///repo/second.bin", secondPath, DateTimeOffset.UtcNow);

        var firstDigest = await first.Digest.WithCancellation(CancellationToken.None);
        var secondDigest = await second.Digest.WithCancellation(CancellationToken.None);

        firstDigest.Should().StartWith("xxh64-sampled:v1:");
        secondDigest.Should().Be(firstDigest);
    }

    [Test]
    [DisplayName("Sampled digests change when sampled regions or file size change")]
    public async Task Given_LargeFilesDifferInSampledContentOrSize_When_ComputingDigest_Then_DigestsDiffer()
    {
        using var tempDir = new TemporaryDirectory();
        var basePath = CreateLargeFile(tempDir.Path, "base.bin", "HEAD-A", "middle-a", "TAIL-A", FileDigest.SampledDigestThresholdBytes + 1024);
        var headChangedPath = CreateLargeFile(tempDir.Path, "head.bin", "HEAD-B", "middle-a", "TAIL-A", FileDigest.SampledDigestThresholdBytes + 1024);
        var tailChangedPath = CreateLargeFile(tempDir.Path, "tail.bin", "HEAD-A", "middle-a", "TAIL-B", FileDigest.SampledDigestThresholdBytes + 1024);
        var sizeChangedPath = CreateLargeFile(tempDir.Path, "size.bin", "HEAD-A", "middle-a", "TAIL-A", FileDigest.SampledDigestThresholdBytes + 2048);

        var baseDigest = await CreateRawArtifact("file:///repo/base.bin", basePath).Digest.WithCancellation(CancellationToken.None);
        var headDigest = await CreateRawArtifact("file:///repo/head.bin", headChangedPath).Digest.WithCancellation(CancellationToken.None);
        var tailDigest = await CreateRawArtifact("file:///repo/tail.bin", tailChangedPath).Digest.WithCancellation(CancellationToken.None);
        var sizeDigest = await CreateRawArtifact("file:///repo/size.bin", sizeChangedPath).Digest.WithCancellation(CancellationToken.None);

        headDigest.Should().NotBe(baseDigest);
        tailDigest.Should().NotBe(baseDigest);
        sizeDigest.Should().NotBe(baseDigest);
    }

    private static RawArtifact CreateRawArtifact(string uri, byte[] content)
    {
        var fileInfo = A.Fake<IFileInfo>();
        A.CallTo(() => fileInfo.Name).Returns(Path.GetFileName(uri));
        A.CallTo(() => fileInfo.Exists).Returns(true);
        A.CallTo(() => fileInfo.Length).Returns(content.Length);
        A.CallTo(() => fileInfo.LastModified).Returns(DateTimeOffset.UtcNow);
        A.CallTo(() => fileInfo.IsDirectory).Returns(false);
        A.CallTo(() => fileInfo.PhysicalPath).Returns(uri);
        A.CallTo(() => fileInfo.CreateReadStream()).ReturnsLazily(() => new MemoryStream(content, writable: false));

        var fileSystem = CreateFileSystem(uri, fileInfo);
        return new RawArtifact(fileInfo, fileSystem);
    }

    private static RawArtifact CreateRawArtifact(string uri, string path, DateTimeOffset? lastModified = null)
    {
        var fileInfo = A.Fake<IFileInfo>();
        var file = new FileInfo(path);

        A.CallTo(() => fileInfo.Name).Returns(file.Name);
        A.CallTo(() => fileInfo.Exists).Returns(true);
        A.CallTo(() => fileInfo.Length).Returns(file.Length);
        A.CallTo(() => fileInfo.LastModified).Returns(lastModified ?? file.LastWriteTimeUtc);
        A.CallTo(() => fileInfo.IsDirectory).Returns(false);
        A.CallTo(() => fileInfo.PhysicalPath).Returns(path);
        A.CallTo(() => fileInfo.CreateReadStream()).ReturnsLazily(() => File.OpenRead(path));

        var fileSystem = CreateFileSystem(uri, fileInfo);
        return new RawArtifact(fileInfo, fileSystem);
    }

    private static IVirtualFileSystem CreateFileSystem(string uri, IFileInfo fileInfo)
    {
        var fileSystem = A.Fake<IVirtualFileSystem>();
        RepoUri.TryParse(uri, out var repoUri).Should().BeTrue();
        A.CallTo(() => fileSystem.GetUri(fileInfo)).Returns(repoUri!);
        return fileSystem;
    }

    private static string CreateLargeFile(string directory, string name, string headMarker, string middleMarker, string tailMarker, long length)
    {
        var path = Path.Combine(directory, name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(length);

        WriteMarker(stream, 0, headMarker);
        WriteMarker(stream, FileDigest.SampleWindowBytes, middleMarker);
        WriteMarker(stream, length - FileDigest.SampleWindowBytes, tailMarker);

        return path;
    }

    private static void WriteMarker(FileStream stream, long offset, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Position = offset;
        stream.Write(bytes, 0, bytes.Length);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "repoql-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
