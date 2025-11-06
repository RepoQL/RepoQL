using System.Text;
using FakeItEasy;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Tests.TestHelpers;

/// <summary>
/// Fluent builder for creating test IndexItems with minimal boilerplate.
/// Eliminates the need for manual FakeItEasy setup in every test.
/// </summary>
public sealed class TestItemBuilder
{
    private string _uriString = "file:///test.txt";
    private string _content = string.Empty;
    private DateTimeOffset _lastModified = DateTimeOffset.UtcNow;
    private IndexItemOptions _options = IndexItemOptions.Default;

    /// <summary>
    /// Sets the file URI and infers the filename from it.
    /// </summary>
    public TestItemBuilder WithUri(string uriString)
    {
        _uriString = uriString;
        return this;
    }

    /// <summary>
    /// Sets the file content as a string.
    /// </summary>
    public TestItemBuilder WithContent(string content)
    {
        _content = content;
        return this;
    }

    /// <summary>
    /// Sets the last modified timestamp.
    /// </summary>
    public TestItemBuilder WithLastModified(DateTimeOffset lastModified)
    {
        _lastModified = lastModified;
        return this;
    }

    /// <summary>
    /// Sets the IndexItem options.
    /// </summary>
    public TestItemBuilder WithOptions(IndexItemOptions options)
    {
        _options = options;
        return this;
    }

    /// <summary>
    /// Builds the IndexItem with all configured settings.
    /// </summary>
    public IndexItem Build()
    {
        var bytes = Encoding.UTF8.GetBytes(_content);

        var fileInfo = A.Fake<IFileInfo>();
        A.CallTo(() => fileInfo.Name).Returns(Path.GetFileName(_uriString));
        A.CallTo(() => fileInfo.Exists).Returns(true);
        A.CallTo(() => fileInfo.Length).Returns(bytes.Length);
        A.CallTo(() => fileInfo.LastModified).Returns(_lastModified);
        A.CallTo(() => fileInfo.IsDirectory).Returns(false);
        A.CallTo(() => fileInfo.PhysicalPath).Returns(_uriString);
        A.CallTo(() => fileInfo.CreateReadStream()).ReturnsLazily(() => new MemoryStream(bytes));

        var fileSystem = A.Fake<IVirtualFileSystem>();
        if (!RepoUri.TryParse(_uriString, out var testUri))
            throw new InvalidOperationException($"Failed to parse test URI: {_uriString}");
        A.CallTo(() => fileSystem.GetUri(fileInfo)).Returns(testUri);

        var rawArtifact = new RawArtifact(fileInfo, fileSystem);
        return new IndexItem(rawArtifact, _options);
    }

    /// <summary>
    /// Creates a new builder for a file with the given name.
    /// </summary>
    public static TestItemBuilder ForFile(string filename)
    {
        return new TestItemBuilder().WithUri($"file:///{filename}");
    }

    /// <summary>
    /// Creates a new builder for a markdown file.
    /// </summary>
    public static TestItemBuilder ForMarkdown(string filename = "test.md")
    {
        return ForFile(filename);
    }
}
