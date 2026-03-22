using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Classification;
using RepoQL.Testing.FileSystem;

namespace RepoQL.FileSystem.Tests;

public class FileClassifierTests
{
    private readonly FileClassifier _classifier = new();

    [Test]
    public void MapsRepresentativeExtensions()
    {
        var expectations = new (string Name, string Type, string Subtype, string? Kind)[]
        {
            ("Program.cs", "text", "plain", "code.csharp"),
            ("Module.cpp", "text", "plain", "code.cpp"),
            ("Header.hpp", "text", "plain", "code.cpp-header"),
            ("project.csproj", "application", "xml", "dotnet.csproj"),
            ("settings.json", "application", "json", null),
            ("README.md", "text", "markdown", "markdown.doc"),
            ("diagram.drawio", "application", "xml", "diagram.drawio"),
            ("schema.proto", "text", "plain", "schema.protobuf"),
            ("build.gradle", "text", "plain", "build.gradle"),
            ("Solution.sln", "text", "plain", "dotnet.sln"),
            ("icon.png", "image", "png", null),
            ("scan.tif", "image", "tiff", null),
            ("audio.mp3", "audio", "mpeg", null)
        };

        foreach (var (name, type, subtype, kind) in expectations)
        {
            var file = new StringFileInfo(name, "content");
            var mediaType = _classifier.GetMediaType(file);
            Console.WriteLine($"{name}: {mediaType}");
            mediaType.Type.Should().Be(type);
            mediaType.Subtype.Should().Be(subtype);
            mediaType.Kind.Should().Be(kind);
        }
    }

    [Test]
    public void DetectsPlainTextWhenNoExtension()
    {
        var file = new StringFileInfo("README", "hello world\nline two");

        var mediaType = _classifier.GetMediaType(file);

        mediaType.Type.Should().Be("text");
        mediaType.Subtype.Should().Be("plain");
    }

    [Test]
    public void FallsBackToBinaryForUnrecognizedBinaryContent()
    {
        var payload = new byte[] { 0x00, 0x01, 0xFF, 0xEE };
        var file = new BinaryFileInfo("payload", payload);

        var mediaType = _classifier.GetMediaType(file);

        mediaType.Type.Should().Be("application");
        mediaType.Subtype.Should().Be("octet-stream");
    }

    [Test]
    public void UnknownLargeBinary_IsClassifiedFromBoundedPrefixRead()
    {
        var payload = new byte[256 * 1024];
        payload[0] = 0x00;
        payload[1] = 0xFF;
        payload[2] = 0xEE;
        payload[3] = 0xDD;

        var file = new CountingBinaryFileInfo("payload.unknown", payload);

        var mediaType = _classifier.GetMediaType(file);

        mediaType.Type.Should().Be("application");
        mediaType.Subtype.Should().Be("octet-stream");
        file.TotalBytesRead.Should().BeLessThanOrEqualTo(64 * 1024);
    }

    [Test]
    public void MapsCompoundExtensions()
    {
        var expectations = new (string Name, string Type, string Subtype, string? Kind)[]
        {
            ("settings.gradle.kts", "text", "plain", "build.gradle-kotlin"),
            ("package-lock.json", "application", "json", "config.npm-lock"),
            ("yarn.lock", "text", "plain", "config.yarn-lock"),
            ("requirements.txt", "text", "plain", "config.requirements"),
            ("setup.py", "text", "plain", "config.python-setup"),
            ("setup.cfg", "text", "plain", "config.python-setupcfg"),
            ("isort.cfg", "text", "plain", "config.isort")
        };

        SemanticMediaType.Parse("application/json;kind=config.npm-lock").Kind.Should().Be("config.npm-lock");

        foreach (var (name, type, subtype, kind) in expectations)
        {
            var file = new StringFileInfo(name, "content");
            var direct = file.GuessMediaTypeFromNamingConvention();
            direct.Should().NotBeNull($"{name} should have explicit mapping");
            direct!.Kind.Should().Be(kind);
            var mediaType = _classifier.GetMediaType(file);
            mediaType.Type.Should().Be(type);
            mediaType.Subtype.Should().Be(subtype);
            mediaType.Kind.Should().Be(kind);
        }
    }

    [Test]
    public void MapsSpecialFilenames()
    {
        var expectations = new (string Name, string Type, string Subtype, string? Kind)[]
        {
            ("Makefile", "text", "plain", "build.make"),
            ("CMakeLists.txt", "text", "plain", "build.cmake"),
            ("Package.swift", "text", "plain", "code.swift-package-manifest")
        };

        foreach (var (name, type, subtype, kind) in expectations)
        {
            var file = new StringFileInfo(name, "content");
            var mediaType = _classifier.GetMediaType(file);
            mediaType.Type.Should().Be(type);
            mediaType.Subtype.Should().Be(subtype);
            mediaType.Kind.Should().Be(kind);
        }
    }

    [Test]
    public void MapsTifWithoutInspectingContent()
    {
        var file = new ThrowingFileInfo("scan.tif");

        var direct = file.GuessMediaTypeFromNamingConvention();
        direct.Should().NotBeNull();
        direct!.Type.Should().Be("image");
        direct.Subtype.Should().Be("tiff");

        var mediaType = _classifier.GetMediaType(file);
        mediaType.Type.Should().Be("image");
        mediaType.Subtype.Should().Be("tiff");
    }

    private sealed class BinaryFileInfo(string name, byte[] content) : IFileInfo
    {
        public bool Exists => true;
        public long Length => content.Length;
        public string PhysicalPath => string.Empty;
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(content, writable: false);
    }

    private sealed class CountingBinaryFileInfo(string name, byte[] content) : IFileInfo
    {
        public bool Exists => true;
        public long Length => content.Length;
        public string PhysicalPath => string.Empty;
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public bool IsDirectory => false;
        public int TotalBytesRead { get; private set; }

        public Stream CreateReadStream() => new CountingStream(this, content);

        private sealed class CountingStream(CountingBinaryFileInfo owner, byte[] bytes) : MemoryStream(bytes, writable: false)
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = base.Read(buffer, offset, count);
                owner.TotalBytesRead += read;
                return read;
            }
        }
    }

    private sealed class ThrowingFileInfo(string name) : IFileInfo
    {
        public bool Exists => true;
        public long Length => 32L * 1024 * 1024;
        public string PhysicalPath => string.Empty;
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public bool IsDirectory => false;

        public Stream CreateReadStream()
            => throw new InvalidOperationException("Stream inspection should not be required for mapped extensions.");
    }
}
