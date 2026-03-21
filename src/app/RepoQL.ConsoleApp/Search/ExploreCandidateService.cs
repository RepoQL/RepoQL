using System.Data;
using RepoQL.Data.DuckDB;
using RepoQL.Explore.Search;

namespace RepoQL.ConsoleApp.Search;

internal sealed class ExploreCandidateService : IExploreCandidateService
{
    private readonly DuckDbDataStore _db;

    public ExploreCandidateService(DuckDbDataStore db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<ExploreCandidateResult> SearchAsync(
        string? query,
        string? scope,
        int k,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sql = $"""
            SELECT
                doc_id,
                node_id,
                uri,
                path,
                node_scope,
                kind,
                symbol,
                lang,
                mime,
                headline,
                structure,
                snippet,
                line_start,
                line_end,
                CAST(bm25_score AS DOUBLE) AS bm25_score,
                CAST(fuzzy_score AS DOUBLE) AS fuzzy_score,
                CAST(sem_score AS DOUBLE) AS sem_score,
                CAST(score AS DOUBLE) AS score,
                confidence,
                sem_provenance
            FROM _explore_candidates(
                q := {ToSqlLiteral(query)},
                uri_glob := {ToSqlLiteral(scope)},
                k := {Math.Max(1, k)}
            )
            ORDER BY score DESC, LENGTH(uri)
            """;

        var candidates = _db.Read(sql, MapCandidate, cancellationToken);
        var totalMatched = candidates
            .Select(static candidate => candidate.DocId)
            .Distinct()
            .Count();

        return Task.FromResult(new ExploreCandidateResult(candidates, totalMatched));
    }

    private static ExploreCandidate MapCandidate(IDataRecord record)
    {
        return new ExploreCandidate(
            DocId: record.GetGuid(record.GetOrdinal("doc_id")),
            NodeId: record.GetGuid(record.GetOrdinal("node_id")),
            Uri: record.GetString(record.GetOrdinal("uri")),
            Path: GetNullableString(record, "path"),
            NodeScope: record.GetString(record.GetOrdinal("node_scope")),
            Kind: GetNullableString(record, "kind"),
            Symbol: GetNullableString(record, "symbol"),
            Lang: GetNullableString(record, "lang"),
            Mime: GetNullableString(record, "mime"),
            Headline: GetNullableString(record, "headline"),
            Structure: GetNullableString(record, "structure"),
            Snippet: GetNullableString(record, "snippet"),
            LineStart: GetNullableInt32(record, "line_start"),
            LineEnd: GetNullableInt32(record, "line_end"),
            BM25Score: GetDouble(record, "bm25_score"),
            FuzzyScore: GetDouble(record, "fuzzy_score"),
            SemScore: GetDouble(record, "sem_score"),
            Score: GetDouble(record, "score"),
            Confidence: GetInt32(record, "confidence"),
            SemProvenance: GetNullableString(record, "sem_provenance"));
    }

    private static string ToSqlLiteral(string? value)
        => value is null ? "NULL" : $"'{value.Replace("'", "''")}'";

    private static string? GetNullableString(IDataRecord record, string name)
    {
        var ordinal = record.GetOrdinal(name);
        return record.IsDBNull(ordinal) ? null : record.GetString(ordinal);
    }

    private static int? GetNullableInt32(IDataRecord record, string name)
    {
        var ordinal = record.GetOrdinal(name);
        return record.IsDBNull(ordinal) ? null : Convert.ToInt32(record.GetValue(ordinal));
    }

    private static int GetInt32(IDataRecord record, string name)
    {
        var ordinal = record.GetOrdinal(name);
        return record.IsDBNull(ordinal) ? 0 : Convert.ToInt32(record.GetValue(ordinal));
    }

    private static double GetDouble(IDataRecord record, string name)
    {
        var ordinal = record.GetOrdinal(name);
        return record.IsDBNull(ordinal) ? 0.0 : Convert.ToDouble(record.GetValue(ordinal));
    }
}
