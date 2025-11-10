using System.Collections.Generic;
using System.Data;
using System.Linq;
using AwesomeAssertions;
using RepoQL.Contracts;
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

    public NodeAssertions Nodes => new(_store.Connection);
    public EmbeddingAssertions Embeddings => new(_store.Connection);
    public RepoIndexAssertions RepoIndex => new(_store.Connection);

    public sealed class NodeAssertions
    {
        private readonly IDbConnection _connection;

        internal NodeAssertions(IDbConnection connection) => _connection = connection;

        public void ShouldContainDocument(string uri)
        {
            var exists = ExecuteScalar(_connection,
                "SELECT count(*) FROM node WHERE kind = 'document' AND uri = ?",
                uri);
            exists.Should().BeGreaterThan(0, $"document {uri} should exist");
        }
    }

    public sealed class EmbeddingAssertions
    {
        private readonly IDbConnection _connection;

        internal EmbeddingAssertions(IDbConnection connection) => _connection = connection;

        public void ShouldHaveEmbeddingsFor(params string[] uris)
        {
            var actual = ExecuteQuery(_connection, "SELECT uri FROM document_embedding")
                .Select(r => (string)r["uri"])
                .ToHashSet();
            foreach (var uri in uris)
            {
                actual.Should().Contain(uri, $"embedding for {uri} should exist");
            }
        }

        public void ShouldHaveScope(string uri, string scope)
        {
            var exists = ExecuteScalar(_connection,
                "SELECT count(*) FROM document_embedding WHERE uri = ? AND scope = ?",
                uri, scope);
            exists.Should().BeGreaterThan(0, $"embedding for {uri} in scope {scope}");
        }
    }

    public sealed class RepoIndexAssertions
    {
        private readonly IDbConnection _connection;

        internal RepoIndexAssertions(IDbConnection connection) => _connection = connection;

        public void ShouldContainEntry(string uri)
        {
            var exists = ExecuteScalar(_connection,
                "SELECT count(*) FROM repo_index WHERE uri = ?",
                uri);
            exists.Should().BeGreaterThan(0, $"repo_index row for {uri}");
        }
    }

    private static IEnumerable<IDataRecord> ExecuteQuery(IDbConnection connection, string sql, params object[] args)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
        {
            var parameter = cmd.CreateParameter();
            parameter.Value = args[i];
            cmd.Parameters.Add(parameter);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return reader;
        }
    }

    private static long ExecuteScalar(IDbConnection connection, string sql, params object[] args)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
        {
            var parameter = cmd.CreateParameter();
            parameter.Value = args[i];
            cmd.Parameters.Add(parameter);
        }

        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
