using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Reads lint annotations for read modifier output using DuckDbDataStore.
/// Complexity: Translates file URI scopes into an annotations view query with ordering for rendering.
/// </summary>
internal sealed class DatabaseLintAnnotationProvider(DuckDbDataStore db) : ILintAnnotationProvider
{
    public Task<IReadOnlyList<LintAnnotation>> GetLintAnnotationsAsync(IReadOnlyList<string> fileUris, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (fileUris.Count == 0)
            return Task.FromResult<IReadOnlyList<LintAnnotation>>([]);

        var normalizedUris = fileUris
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Select(uri => uri.Replace("'", "''", StringComparison.Ordinal).ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedUris.Count == 0)
            return Task.FromResult<IReadOnlyList<LintAnnotation>>([]);

        var inClause = string.Join(", ", normalizedUris.Select(uri => $"'{uri}'"));

        var sql = $"""
            SELECT
                resolved_target_uri,
                repository_uri_container(resolved_target_uri) AS file_uri,
                severity,
                severity_rank,
                rule_id,
                source,
                message,
                CAST(repository_uri_line_start(resolved_target_uri) AS INTEGER) AS line_start
            FROM Annotations
            WHERE kind = 'lint'
              AND lower(repository_uri_container(resolved_target_uri)) IN ({inClause})
            ORDER BY severity_rank DESC, file_uri, line_start NULLS LAST, resolved_target_uri
            """;

        var rows = db.Query(sql);
        var annotations = new List<LintAnnotation>(rows.Count);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var resolvedTargetUri = row.TryGetValue("resolved_target_uri", out var resolved)
                ? resolved?.ToString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(resolvedTargetUri))
                continue;

            var fileUri = row.TryGetValue("file_uri", out var file)
                ? file?.ToString() ?? string.Empty
                : string.Empty;

            var severity = row.TryGetValue("severity", out var sev)
                ? sev?.ToString() ?? string.Empty
                : string.Empty;

            var message = row.TryGetValue("message", out var msg)
                ? msg?.ToString() ?? string.Empty
                : string.Empty;

            var ruleId = row.TryGetValue("rule_id", out var rule)
                ? rule?.ToString()
                : null;

            var source = row.TryGetValue("source", out var sourceValue)
                ? sourceValue?.ToString()
                : null;

            var severityRank = TryConvertInt(row, "severity_rank") ?? 0;
            var lineStart = TryConvertInt(row, "line_start");

            annotations.Add(new LintAnnotation(
                FileUri: fileUri,
                ResolvedTargetUri: resolvedTargetUri,
                Severity: severity,
                SeverityRank: severityRank,
                Message: message,
                RuleId: ruleId,
                Source: source,
                LineStart: lineStart));
        }

        return Task.FromResult<IReadOnlyList<LintAnnotation>>(annotations);
    }

    private static int? TryConvertInt(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            short shortValue => shortValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }
}
