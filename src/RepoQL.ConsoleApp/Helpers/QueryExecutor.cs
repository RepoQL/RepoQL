using System.Threading;
using System.Threading.Tasks;
using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Formatters;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Helpers;

internal sealed class QueryExecutor(ResultFormatterFactory formatterFactory)
{
    private readonly ResultFormatterFactory _formatterFactory = formatterFactory;

    public async Task<QueryExecutionResult> ExecuteAsync(
        string sql,
        int maxRows,
        ResultFormat format,
        CancellationToken cancellationToken)
    {
        await using var client = await RepoQlClient.CreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        RawQueryResponse result;
        long? total = null;

        if (format == ResultFormat.Unstructured)
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

internal readonly record struct QueryExecutionResult(string[] Lines, long TotalRowCount);
