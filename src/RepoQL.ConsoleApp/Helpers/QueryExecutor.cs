using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Formatters;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Helpers;

internal sealed class QueryExecutor
{
    private readonly ResultFormatterFactory _formatterFactory;
    private readonly RepoQlClientProvider _clientProvider;

    public QueryExecutor(ResultFormatterFactory formatterFactory, RepoQlClientProvider clientProvider)
    {
        _formatterFactory = formatterFactory;
        _clientProvider = clientProvider;
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        string sql,
        int maxRows,
        ResultFormat format,
        int tokenBudget = 0,
        CancellationToken cancellationToken = default)
    {
        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);

        RawQueryResponse result;
        long? total = null;

        if (format == ResultFormat.Toon)
        {
            result = await client.ExecuteRawQueryAsync(sql, tokenBudget: tokenBudget, cancellationToken: cancellationToken).ConfigureAwait(false);
            total = result.RowCount;
        }
        else
        {
            result = await client.ExecuteRawQueryAsync(sql, parameters: null, rowLimit: maxRows, tokenBudget: tokenBudget, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var formatter = _formatterFactory.GetFormatter(format);
        var lines = await formatter.FormatAsync(result, maxRows, total, cancellationToken).ConfigureAwait(false);

        return new QueryExecutionResult(
            lines,
            total ?? result.RowCount,
            result.ExecutionTimeMs,
            result.IndexPending,
            result.IndexTotal,
            result.IndexFailed,
            result.IndexStale,
            result.SemanticEnabled,
            result.SemanticReady,
            result.SemanticPercent,
            result.Summarized,
            result.OriginalRowCount);
    }
}
