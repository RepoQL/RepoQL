using Microsoft.Extensions.FileProviders;
using RepoQL.Core;

namespace RepoQL.Tests;

public sealed class TestObserver(Action<Exception> onError, Action<IndexerEvent> onNext) : IObserver<IndexerEvent>
{
    public void OnCompleted() { }
    public void OnError(Exception error) => onError(error);
    public void OnNext(IndexerEvent value) => onNext(value);
}


internal sealed class StringFileInfo(string name, string content) : IFileInfo
{
    private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(content);

    public bool Exists => true;
    public long Length => _bytes.Length;
    public string PhysicalPath => string.Empty;
    public string Name => name;
    public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
    public bool IsDirectory => false;
    public Stream CreateReadStream() => new MemoryStream(_bytes, writable: false);
}
