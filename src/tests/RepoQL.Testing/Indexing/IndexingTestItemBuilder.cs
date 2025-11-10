using System.Text;
using FakeItEasy;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Testing.Indexing;

/// <summary>
/// Fluent builder for creating test <see cref="IndexItem"/> instances without repeating FakeItEasy boilerplate.
/// </summary>
public sealed class IndexingTestItemBuilder
{
    private string _uriString = "file:///test.txt";
    private byte[] _content = Array.Empty<byte>();
    private DateTimeOffset _lastModified = DateTimeOffset.UtcNow;
    private IndexItemOptions _options = IndexItemOptions.Default;

    public IndexingTestItemBuilder WithUri(string uriString)
    {
        _uriString = uriString;
        return this;
    }

    public IndexingTestItemBuilder WithContent(string content)
    {
        _content = Encoding.UTF8.GetBytes(content ?? string.Empty);
        return this;
    }

    public IndexingTestItemBuilder WithContent(byte[] content)
    {
        _content = content ?? Array.Empty<byte>();
        return this;
    }

    public IndexingTestItemBuilder WithLastModified(DateTimeOffset lastModified)
    {
        _lastModified = lastModified;
        return this;
    }

    public IndexingTestItemBuilder WithOptions(IndexItemOptions options)
    {
        _options = options;
        return this;
    }

    public IndexItem Build()
    {
        var rawArtifact = BuildRawArtifact();
        return new IndexItem(rawArtifact, _options);
    }

    public RawArtifact BuildRawArtifact()
    {
        var fileInfo = A.Fake<IFileInfo>();
        A.CallTo(() => fileInfo.Name).Returns(Path.GetFileName(_uriString));
        A.CallTo(() => fileInfo.Exists).Returns(true);
        A.CallTo(() => fileInfo.Length).Returns(_content.Length);
        A.CallTo(() => fileInfo.LastModified).Returns(_lastModified);
        A.CallTo(() => fileInfo.IsDirectory).Returns(false);
        A.CallTo(() => fileInfo.PhysicalPath).Returns(_uriString);
        A.CallTo(() => fileInfo.CreateReadStream()).ReturnsLazily(() => new MemoryStream(_content));

        var fileSystem = A.Fake<IVirtualFileSystem>();
        if (!RepoUri.TryParse(_uriString, out var uri))
        {
            throw new InvalidOperationException($"Failed to parse test URI: {_uriString}");
        }
        A.CallTo(() => fileSystem.GetUri(fileInfo)).Returns(uri);

        return new RawArtifact(fileInfo, fileSystem);
    }

    public static IndexingTestItemBuilder ForFile(string filename)
    {
        return new IndexingTestItemBuilder().WithUri($"file:///{filename}");
    }

    public static IndexingTestItemBuilder ForMarkdown(string filename = "test.md")
    {
        return ForFile(filename);
    }
}
