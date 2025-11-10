using RepoQL.Contracts;
using RepoQL.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Testing.Indexing;

public static class IndexingTestItemFactory
{
    public static IndexItem CreateIndexItem(string uri = "file:///repo/test.txt")
        => Builder().WithUri(uri).Build();

    public static RawArtifact CreateRawArtifact(string uri)
        => Builder().WithUri(uri).BuildRawArtifact();

    public static RepoUri CreateUri(string value)
        => RepoUri.TryParse(value, out var parsed)
            ? parsed!
            : throw new InvalidOperationException($"Unable to parse URI '{value}'.");

    public static IndexingTestItemBuilder Builder() => new();
}
