using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using RepoQL.Contracts;

namespace RepoQL.Web.Services;

/// <summary>
/// Executes SQL against the RepoQL host and shapes responses for UI consumption.
/// </summary>
internal sealed class SqlExecutionService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<SqlExecutionService> _logger;

    public SqlExecutionService(RepoQlConnectionManager connectionManager, ILogger<SqlExecutionService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<SqlExecutionResult> ExecuteAsync(string sql, int? rowLimit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("SQL text cannot be empty.", nameof(sql));

        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Executing SQL (limit: {Limit})", rowLimit);
        }

        var response = await client.ExecuteRawQueryAsync(sql, rowLimit: rowLimit, cancellationToken: cancellationToken).ConfigureAwait(false);

        var columns = response.Columns.Select(c => c.Name ?? string.Empty).ToArray();
        var rows = response.Rows
            .Select(row => row.Values.Select(FormatValue).ToArray())
            .Cast<IReadOnlyList<string>>()
            .ToArray();

        return new SqlExecutionResult(
            Columns: columns,
            Rows: rows,
            RowCount: response.RowCount,
            Truncated: response.Truncated);
    }

    private static string FormatValue(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString("G"),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            Value.KindOneofCase.NullValue => "null",
            Value.KindOneofCase.ListValue => JsonFormatter.Default.Format(value),
            Value.KindOneofCase.StructValue => JsonFormatter.Default.Format(value),
            _ => string.Empty
        };
    }
}
