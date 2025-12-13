using AwesomeAssertions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Testing.Indexing;

/// <summary>Convenience wrapper for asserting graph database state in tests.</summary>
public sealed class GraphAssertionHarness
{
    private readonly DuckDbTestStore _store;

    public GraphAssertionHarness(DuckDbTestStore store)
    {
        _store = store;
    }

    public NodeAssertions Nodes => new(_store.DataStore);
    public EmbeddingAssertions Embeddings => new(_store.DataStore);
    public RepoIndexAssertions RepoIndex => new(_store.DataStore);

    public sealed class NodeAssertions
    {
        private readonly DuckDbDataStore _store;

        internal NodeAssertions(DuckDbDataStore store) => _store = store;

        public void ShouldContainDocument(string uri)
        {
            var exists = _store.ReadScalar<long>(
                $"SELECT count(*) FROM node WHERE kind = 'document' AND uri = '{uri}'");
            exists.Should().BeGreaterThan(0, $"document {uri} should exist");
        }
    }

    public sealed class EmbeddingAssertions
    {
        private readonly DuckDbDataStore _store;

        internal EmbeddingAssertions(DuckDbDataStore store) => _store = store;

        public void ShouldHaveEmbeddingsFor(params string[] uris)
        {
            var actual = _store.Read(
                "SELECT uri FROM document_embedding",
                r => r.GetString(0))
                .ToHashSet();
            foreach (var uri in uris)
            {
                actual.Should().Contain(uri, $"embedding for {uri} should exist");
            }
        }

        public void ShouldHaveScope(string uri, string scope)
        {
            var exists = _store.ReadScalar<long>(
                $"SELECT count(*) FROM document_embedding WHERE uri = '{uri}' AND scope = '{scope}'");
            exists.Should().BeGreaterThan(0, $"embedding for {uri} in scope {scope}");
        }
    }

    public sealed class RepoIndexAssertions
    {
        private readonly DuckDbDataStore _store;

        internal RepoIndexAssertions(DuckDbDataStore store) => _store = store;

        public void ShouldContainEntry(string uri)
        {
            var exists = _store.ReadScalar<long>(
                $"SELECT count(*) FROM repo_index WHERE uri = '{uri}'");
            exists.Should().BeGreaterThan(0, $"repo_index row for {uri}");
        }
    }
}
