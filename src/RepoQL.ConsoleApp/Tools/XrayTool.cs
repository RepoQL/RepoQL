using System.ComponentModel;
using System.Globalization;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class XrayTool(RepoQlClientProvider clientProvider)
{
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));

    public enum SummaryDetail
    {
        Headline,
        Summary,
        Snippet
    }

    private const string SummarizeInstructions = """
                                                 See a content-aware summary of file contents, filtering by glob pattern, type, and search with a configurable level of detail.
                                                 Use this tool FIRST to very efficiently explore the codebase and work out what exists without reading whole files
                                                 """;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "xray"), Description(SummarizeInstructions)]
    
    public async Task<string> SummarizeAsync(
        [Description("Glob pattern for RepoURIs (default **/*).")] string? pattern = null,
        [Description("Optional wildcard pattern for media type, e.g. *csharp*.")] string? type = null,
        [Description("Literal filename or symbol filters passed to file_search keywords.")] string? keywords = null,
        [Description("Optional natural-language question passed to file_search.")] string? question = null,
        [Description("Detail level: headline, summary, snippet.")] string? detail = null,
        [Description("Maximum results to return. Uses detail-specific defaults when not provided.")] int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var detailKind = ParseDetail(detail);
        var effectiveLimit = limit > 0 ? limit : GetDefaultLimit(detailKind);
        var likeFile = BuildLikePattern("file:///", pattern);
        var likeEmbed = BuildLikePattern("embed:///", pattern);
        var typePattern = NormalizeTypePattern(type);

        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var rows = await QueryDocumentsAsync(client, likeFile, likeEmbed, typePattern, keywords, question, effectiveLimit, cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return "No documents matched the supplied filters.";
        }

        var builder = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine("---");
            }

            var row = rows[i];
            switch (detailKind)
            {
                case SummaryDetail.Headline:
                    FormatHeadline(builder, row);
                    break;
                case SummaryDetail.Summary:
                    await FormatDefaultAsync(builder, client, row, cancellationToken).ConfigureAwait(false);
                    break;
                case SummaryDetail.Snippet:
                    await FormatSnippetAsync(builder, client, row, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return builder.ToString();
    }

    private static SummaryDetail ParseDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("Detail parameter is required (headline, summary, default, snippet).", nameof(detail));
        }

        return detail.Trim().ToLowerInvariant() switch
        {
            "headline" => SummaryDetail.Headline,
            "summary" => SummaryDetail.Summary,
            "snippet" => SummaryDetail.Snippet,
            _ => throw new ArgumentException("Detail must be one of: headline, summary, default, snippet.", nameof(detail))
        };
    }

    private static int GetDefaultLimit(SummaryDetail detail) => detail switch
    {
        SummaryDetail.Headline => 1000,
        SummaryDetail.Summary => 100,
        SummaryDetail.Snippet => 10,
        _ => 100
    };

    private static string BuildLikePattern(string schemePrefix, string? glob)
    {
        var pattern = string.IsNullOrWhiteSpace(glob) ? "**/*" : glob.Trim();
        pattern = pattern.Replace('\\', '/');
        if (!pattern.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) &&
            !pattern.StartsWith("embed:///", StringComparison.OrdinalIgnoreCase))
        {
            if (pattern.StartsWith("/"))
            {
                pattern = pattern.TrimStart('/');
            }
            pattern = schemePrefix + pattern;
        }

        pattern = pattern.Replace("**", "%");
        pattern = pattern.Replace("*", "%");
        pattern = pattern.Replace("?", "_");
        if (!pattern.Contains('%') && !pattern.Contains('_'))
        {
            if (!pattern.EndsWith('%'))
            {
                pattern += "%";
            }
        }

        return pattern.ToLowerInvariant();
    }

    private static string? NormalizeTypePattern(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var t = type.Trim()
            .Replace('\\', '/')
            .Replace("**", "%")
            .Replace("*", "%")
            .Replace("?", "_");

        return t;
    }

    private async Task<List<DocumentRow>> QueryDocumentsAsync(
        IRepoQlClient client,
        string likeFile,
        string likeEmbed,
        string? typePattern,
        string? keywords,
        string? question,
        int limit,
        CancellationToken cancellationToken)
    {
        var parameters = new List<object?>();
        string sql;

        var where = new StringBuilder();
        where.Append("(lower(n.uri) LIKE ? ESCAPE '\\' OR lower(n.uri) LIKE ? ESCAPE '\\')");
        parameters.Add(likeFile);
        parameters.Add(likeEmbed);

        if (!string.IsNullOrWhiteSpace(typePattern))
        {
            where.Append(" AND a.media_type ILIKE ?");
            parameters.Add(typePattern);
        }

        var keywordsText = keywords?.Trim();
        var questionText = question?.Trim();
        var hasKeywords = !string.IsNullOrEmpty(keywordsText);
        var hasQuestion = !string.IsNullOrEmpty(questionText);

        if (hasKeywords || hasQuestion)
        {
            var searchLimit = Math.Max(limit * 3, limit);
            sql = $"""
                WITH search AS (
                    SELECT doc_id, score
                    FROM file_search(?, ?, k := {searchLimit}, max_cand := 5000)
                ),
                filtered AS (
                    SELECT n.id,
                           n.uri,
                           a.headline,
                           a.summary,
                           a.structure,
                           a.media_type,
                           a.byte_size
                    FROM node n
                    JOIN artifact a ON a.id = n.artifact_id
                    WHERE n.kind = 'document' AND {where}
                )
                SELECT f.uri,
                       f.headline,
                       f.summary,
                       f.structure,
                       f.media_type,
                       f.byte_size,
                       s.score
                FROM filtered f
                JOIN search s ON s.doc_id = f.id
                ORDER BY s.score DESC, lower(f.uri)
                LIMIT ?
                """;
            var keywordsParam = hasKeywords ? keywordsText! : string.Empty;
            object? questionParam = hasQuestion ? questionText : null;
            parameters.Insert(0, questionParam);
            parameters.Insert(0, keywordsParam);
            parameters.Add(limit);
        }
        else
        {
            sql = $"""
                SELECT n.uri,
                       a.headline,
                       a.summary,
                       a.structure,
                       a.media_type,
                       a.byte_size,
                       NULL AS score
                FROM node n
                JOIN artifact a ON a.id = n.artifact_id
                WHERE n.kind = 'document' AND {where}
                ORDER BY
                    CASE
                        WHEN lower(n.uri) LIKE 'embed://%' THEN 0
                        WHEN lower(n.uri) LIKE 'file:///readme%' THEN 1
                        WHEN lower(n.uri) LIKE 'file:///docs/%' THEN 2
                        ELSE 3
                    END,
                    lower(n.uri)
                LIMIT ?
                """;
            parameters.Add(limit);
        }

        var response = await client.ExecuteRawQueryAsync(sql, parameters.ToArray(), null, cancellationToken).ConfigureAwait(false);
        var list = new List<DocumentRow>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            var values = row.Values;
            if (values.Count == 0)
            {
                continue;
            }

            var uri = ExtractString(values[0]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(uri))
            {
                continue;
            }

            string? headline = values.Count > 1 ? ExtractString(values[1]) : null;
            string? summary = values.Count > 2 ? ExtractString(values[2]) : null;
            string? structure = values.Count > 3 ? ExtractString(values[3]) : null;
            string? mediaType = values.Count > 4 ? ExtractString(values[4]) : null;
            long? size = null;
            if (values.Count > 5)
            {
                var sizeString = ExtractString(values[5]);
                if (!string.IsNullOrWhiteSpace(sizeString) && long.TryParse(sizeString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize))
                {
                    size = parsedSize;
                }
            }

            double? score = null;
            if (values.Count > 6)
            {
                var scoreString = ExtractString(values[6]);
                if (!string.IsNullOrWhiteSpace(scoreString) && double.TryParse(scoreString, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScore))
                {
                    score = parsedScore;
                }
            }

            list.Add(new DocumentRow(
                Uri: uri,
                Headline: headline,
                Summary: summary,
                Structure: structure,
                MediaType: mediaType,
                Size: size,
                Score: score));
        }

        return list;
    }

    private static void FormatHeadline(StringBuilder builder, DocumentRow row)
    {
        var headline = !string.IsNullOrWhiteSpace(row.Headline) ? row.Headline!.Trim() : ExtractFileName(row.Uri);
        builder.Append(row.Uri);
        builder.Append(" - ");
        builder.Append(headline);
    }

    private async Task FormatSummaryAsync(StringBuilder builder, IRepoQlClient client, DocumentRow row, CancellationToken cancellationToken)
    {
        var summary = !string.IsNullOrWhiteSpace(row.Summary) ? row.Summary!.Trim() :
                      !string.IsNullOrWhiteSpace(row.Structure) ? row.Structure!.Trim() :
                      "(no summary stored)";
        builder.AppendLine(row.Uri);
        builder.AppendLine(summary);
        var annotations = await FetchAnnotationsAsync(client, row.Uri, cancellationToken).ConfigureAwait(false);
        builder.AppendLine(FormatAnnotations(annotations));
    }

    private async Task FormatDefaultAsync(StringBuilder builder, IRepoQlClient client, DocumentRow row, CancellationToken cancellationToken)
    {
        var headline = !string.IsNullOrWhiteSpace(row.Headline) ? row.Headline!.Trim() : ExtractFileName(row.Uri);
        var structure = !string.IsNullOrWhiteSpace(row.Structure) ? row.Structure!.TrimEnd() : "(no structure stored)";
        builder.AppendLine($"{row.Uri} - {headline}");
        builder.AppendLine(structure);
        var related = await FetchRelatedRecordsAsync(client, row.Uri, cancellationToken).ConfigureAwait(false);
        builder.AppendLine("Related Records:");
        if (related.Count == 0)
        {
            builder.AppendLine("- (none)");
        }
        else
        {
            foreach (var r in related)
            {
                builder.AppendLine($"- {r}");
            }
        }

        var annotations = await FetchAnnotationsAsync(client, row.Uri, cancellationToken).ConfigureAwait(false);
        builder.AppendLine(FormatAnnotations(annotations));
    }

    private async Task FormatSnippetAsync(StringBuilder builder, IRepoQlClient client, DocumentRow row, CancellationToken cancellationToken)
    {
        builder.AppendLine(row.Uri);
        var snippetText = await FetchSnippetAsync(client, row.Uri, cancellationToken).ConfigureAwait(false);
        var mediaType = !string.IsNullOrWhiteSpace(row.MediaType) ? row.MediaType : "text/plain";
        builder.AppendLine($"```{mediaType}");
        builder.AppendLine(snippetText);
        builder.AppendLine("```");

        var annotations = await FetchAnnotationsAsync(client, row.Uri, cancellationToken).ConfigureAwait(false);
        builder.AppendLine(FormatAnnotations(annotations));
    }

    private static string ExtractFileName(string uri)
    {
        var trimmed = uri.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 && idx < trimmed.Length - 1
            ? trimmed[(idx + 1)..]
            : trimmed;
    }

    private async Task<List<string>> FetchRelatedRecordsAsync(IRepoQlClient client, string uri, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH doc AS (
                SELECT id FROM node WHERE lower(uri) = lower(?) AND kind = 'document' LIMIT 1
            )
            SELECT child.uri
            FROM doc
            JOIN edge e ON e.source_node_id = doc.id AND e.is_composition = TRUE
            JOIN node child ON child.id = e.destination_node_id
            ORDER BY e.ordinal
            LIMIT 5
            """;

        var response = await client.ExecuteRawQueryAsync(sql, new object?[] { uri }, null, cancellationToken).ConfigureAwait(false);
        if (response.Rows.Count == 0)
        {
            return new List<string>();
        }

        var list = new List<string>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            if (row.Values.Count == 0) continue;
            var relatedUri = row.Values[0].StringValue;
            if (!string.IsNullOrWhiteSpace(relatedUri))
            {
                list.Add(relatedUri!);
            }
        }

        return list;
    }

    private async Task<List<AnnotationRow>> FetchAnnotationsAsync(IRepoQlClient client, string uri, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT severity,
                   source,
                   rule_id,
                   message,
                   resolved_target_uri
            FROM annotations_for(?, NULL, NULL)
            LIMIT 10
            """;

        RawQueryResponse response;
        try
        {
            response = await client.ExecuteRawQueryAsync(sql, new object?[] { uri }, null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new List<AnnotationRow>();
        }

        var annotations = new List<AnnotationRow>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            var values = row.Values;
            if (values.Count == 0) continue;
            var severity = ExtractString(values[0]) ?? "info";
            var source = values.Count > 1 ? ExtractString(values[1]) : null;
            var ruleId = values.Count > 2 ? ExtractString(values[2]) : null;
            var message = values.Count > 3 ? ExtractString(values[3]) : null;
            var targetUri = values.Count > 4 ? ExtractString(values[4]) : null;
            annotations.Add(new AnnotationRow(severity ?? "info", source, ruleId, message, targetUri));
        }

        return annotations;
    }

    private async Task<string> FetchSnippetAsync(IRepoQlClient client, string uri, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT line_number,
                   text,
                   is_focus
            FROM snippet(?, 2)
            ORDER BY line_number
            """;

        var response = await client.ExecuteRawQueryAsync(sql, new object?[] { uri }, null, cancellationToken).ConfigureAwait(false);
        if (response.Rows.Count == 0)
        {
            return "(no snippet available)";
        }

        var builder = new StringBuilder();
        foreach (var row in response.Rows)
        {
            var values = row.Values;
            if (values.Count < 2) continue;
            var lineNumber = ExtractString(values[0]) ?? "?";
            var text = ExtractString(values[1]) ?? string.Empty;
            var isFocus = values.Count > 2 && ExtractBool(values[2]);
            var prefix = isFocus ? ">" : " ";
            builder.Append(prefix);
            builder.Append(lineNumber);
            builder.Append(": ");
            builder.AppendLine(text);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatAnnotations(IReadOnlyList<AnnotationRow> annotations)
    {
        if (annotations.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        foreach (var annotation in annotations)
        {
            var rulePart = !string.IsNullOrWhiteSpace(annotation.RuleId)
                ? $"{annotation.Source ?? "unknown"}/{annotation.RuleId}"
                : annotation.Source ?? "unknown";

            builder.Append("- [");
            builder.Append(annotation.Severity);
            builder.Append("] ");
            builder.Append(rulePart);
            builder.Append(": ");
            builder.AppendLine(annotation.Message ?? "(no message)");
            if (!string.IsNullOrWhiteSpace(annotation.TargetUri))
            {
                builder.AppendLine($"  ↳ {annotation.TargetUri}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string? ExtractString(Value value) =>
        value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => null
        };

    private static bool ExtractBool(Value value) =>
        value.KindCase switch
        {
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.NumberValue => Math.Abs(value.NumberValue) > double.Epsilon,
            Value.KindOneofCase.StringValue => bool.TryParse(value.StringValue, out var parsed) && parsed,
            _ => false
        };

    private sealed record DocumentRow(
        string Uri,
        string? Headline,
        string? Summary,
        string? Structure,
        string? MediaType,
        long? Size,
        double? Score);

    private sealed record AnnotationRow(
        string Severity,
        string? Source,
        string? RuleId,
        string? Message,
        string? TargetUri);
}
