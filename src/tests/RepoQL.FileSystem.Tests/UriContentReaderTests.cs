using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;

namespace RepoQL.FileSystem.Tests;

public class UriContentReaderTests
{
    [Test]
    public void Open_FragmentUri_ReadsContainerContentAndReportsSize()
    {
        var mem = new MemoryFileSystem();
        mem.AddOrUpdateText("docs/test.md", "alpha\nbeta\ngamma");

        var stores = new IVirtualFileSystem[] { mem };
        var registry = new FileSystemRegistry(stores);
        var multi = new MultiFileSystem(registry, stores);
        var reader = new UriContentReader(multi);

        using var content = reader.Open(RepoUri.Parse("mem://repo/docs/test.md#line=2,3"));
        var text = content.Reader.ReadToEnd();

        content.SourceUri.AbsoluteUri.Should().Be("mem://repo/docs/test.md");
        content.RequestedUri.AbsoluteUri.Should().Be("mem://repo/docs/test.md#line=2,3");
        content.SizeBytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount("alpha\nbeta\ngamma"));
        content.Stream.CanWrite.Should().BeFalse();
        text.Should().Be("alpha\nbeta\ngamma");
    }

    [Test]
    public void TryOpen_MissingUri_ReturnsFalse()
    {
        var mem = new MemoryFileSystem();
        var stores = new IVirtualFileSystem[] { mem };
        var registry = new FileSystemRegistry(stores);
        var multi = new MultiFileSystem(registry, stores);
        var reader = new UriContentReader(multi);

        var ok = reader.TryOpen(RepoUri.Parse("mem://repo/missing.md"), out var content);

        ok.Should().BeFalse();
        content.Should().BeNull();
    }

    [Test]
    public void Open_MissingUri_ThrowsFileNotFoundException()
    {
        var mem = new MemoryFileSystem();
        var stores = new IVirtualFileSystem[] { mem };
        var registry = new FileSystemRegistry(stores);
        var multi = new MultiFileSystem(registry, stores);
        var reader = new UriContentReader(multi);

        Action act = () => reader.Open(RepoUri.Parse("mem://repo/missing.md"));
        act.Should().Throw<FileNotFoundException>();
    }
}
