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
        _ = _clientProvider.EnsureStarted();
    }

    public async Task<QueryExecutionResult> ExecuteAsync(
        string sql,
        int maxRows,
        ResultFormat format,
        CancellationToken cancellationToken)
    {
        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);

        RawQueryResponse result;
        long? total = null;

        if (format == ResultFormat.Toon)
        {
            result = await client.ExecuteRawQueryAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
            total = result.RowCount;
        }
        else
        {
            result = await client.ExecuteRawQueryAsync(sql, parameters: null, rowLimit: maxRows, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var formatter = _formatterFactory.GetFormatter(format);
        var lines = await formatter.FormatAsync(result, maxRows, total, cancellationToken).ConfigureAwait(false);

        return new QueryExecutionResult(lines, total ?? result.RowCount);
    }
}