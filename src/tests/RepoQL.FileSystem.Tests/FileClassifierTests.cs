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
}
