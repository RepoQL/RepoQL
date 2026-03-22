using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Contracts.Inference;

namespace RepoQL.Query;

/// <summary>
/// Purpose: Execute SQL queries with parameter substitution, type inference, and budget management.
/// Complexity: Handles parameter escaping, column schema inference, token budget summarization
/// via LLM, and result truncation. No transport knowledge — pure business logic.
/// </summary>
public sealed class QueryEngine : IQueryEngine
{
    private readonly IQueryDataSource _dataSource;
    private readonly IInferenceProvider? _inference;

    public QueryEngine(IQueryDataSource dataSource, IInferenceProvider? inference = null)
    {
        _dataSource = dataSource;
        _inference = inference;
    }

    public async Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancel = default)
    {
        var sw = Stopwatch.StartNew();

        var sql = SubstituteParameters(request.Sql, request.Parameters ?? []);
        var allRows = _dataSource.Query(sql, cancel);

        var limited = request.Limit > 0;
        var take = limited ? allRows.Take(request.Limit).ToList() : allRows;

        // Infer column schema from first row
        var columns = new List<QueryColumn>();
        var resultRows = new List<IReadOnlyList<object?>>();

        IReadOnlyDictionary<string, object?>? first = null;
        string[]? colNames = null;

        foreach (var r in take)
        {
            if (first is null)
            {
                first = r;
                colNames = first.Keys.ToArray();
                foreach (var col in colNames)
                    columns.Add(new QueryColumn { Name = col, TypeName = InferDbType(first[col]) });
            }

            var values = new object?[colNames!.Length];
            for (var i = 0; i < colNames.Length; i++)
            {
                r.TryGetValue(colNames[i], out var value);
                values[i] = value;
            }
            resultRows.Add(values);
        }

        var truncated = limited && allRows.Count > request.Limit;
        var rowCount = resultRows.Count;

        // Token budget summarization
        var summarized = false;
        var originalRowCount = 0;
        if (request.TokenBudget > 0 && resultRows.Count > 0)
        {
            var formatted = FormatForTokenEstimation(columns, resultRows);
            var estimatedTokens = TokenEstimator.EstimateTokens(formatted);

            if (estimatedTokens > request.TokenBudget)
            {
                var intent = ExtractSqlComment(request.Sql);
                if (!string.IsNullOrWhiteSpace(intent) && _inference is { Available: true })
                {
                    try
                    {
                        var summary = await _inference.CompleteAsync(
                            new InferenceRequest
                            {
                                Context = formatted,
                                Prompt = intent,
                                MaxTokens = request.TokenBudget
                            },
                            cancel).ConfigureAwait(false);

                        originalRowCount = rowCount;
                        columns.Clear();
                        columns.Add(new QueryColumn { Name = "summary", TypeName = "VARCHAR" });
                        resultRows.Clear();
                        resultRows.Add(new object?[] { summary.Content });
                        rowCount = 1;
                        summarized = true;
                    }
                    catch
                    {
                        // Summarization failed — return original results
                    }
                }
            }
        }

        return new QueryResult
        {
            Columns = columns,
            Rows = resultRows,
            RowCount = rowCount,
            Truncated = truncated,
            Summarized = summarized,
            OriginalRowCount = originalRowCount,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    #region Helpers

    internal static string SubstituteParameters(string sql, IReadOnlyList<QueryParameter> parameters)
    {
        if (parameters.Count == 0)
            return sql;

        var result = new StringBuilder(sql.Length + parameters.Count * 20);
        var paramIndex = 0;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            // Skip string literals (single quotes)
            if (c == '\'')
            {
                var start = i;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }
                    if (sql[i] == '\'')
                        break;
                    i++;
                }
                result.Append(sql.AsSpan(start, i - start + 1));
                continue;
            }

            if (c == '?')
            {
                if (paramIndex < parameters.Count)
                {
                    result.Append(ToSqlLiteral(parameters[paramIndex]));
                    paramIndex++;
                }
                else
                {
                    result.Append('?');
                }
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    private static string ToSqlLiteral(QueryParameter p) => p.Kind switch
    {
        QueryParameterKind.Null => "NULL",
        QueryParameterKind.Bool => p.BoolValue == true ? "TRUE" : "FALSE",
        QueryParameterKind.Number => (p.NumberValue ?? 0).ToString(CultureInfo.InvariantCulture),
        QueryParameterKind.String => $"'{(p.StringValue ?? "").Replace("'", "''")}'",
        _ => "NULL"
    };

    internal static string InferDbType(object? sample)
    {
        if (sample is null || sample is DBNull) return "UNKNOWN";
        return sample switch
        {
            bool => "BOOLEAN",
            byte or sbyte or short or ushort or int => "INTEGER",
            uint or long => "BIGINT",
            ulong => "UBIGINT",
            float or double or decimal => "DOUBLE",
            DateTime => "TIMESTAMP",
            Guid => "UUID",
            byte[] => "BLOB",
            _ => "VARCHAR"
        };
    }

    internal static string? ExtractSqlComment(string sql)
    {
        var singleLine = Regex.Match(sql, @"--\s*(.+?)(?:\r?\n|$)");
        if (singleLine.Success)
            return singleLine.Groups[1].Value.Trim();

        var block = Regex.Match(sql, @"/\*\s*([\s\S]*?)\s*\*/");
        if (block.Success)
            return block.Groups[1].Value.Trim();

        return null;
    }

    private static string FormatForTokenEstimation(
        IReadOnlyList<QueryColumn> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\t", columns.Select(c => c.Name)));

        foreach (var row in rows)
        {
            var values = row.Select(v => v switch
            {
                string s => s,
                double d => d.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString(CultureInfo.InvariantCulture),
                decimal dec => dec.ToString(CultureInfo.InvariantCulture),
                bool b => b.ToString(),
                null => "NULL",
                _ => v.ToString() ?? ""
            });
            sb.AppendLine(string.Join("\t", values));
        }

        return sb.ToString();
    }

    #endregion
}
