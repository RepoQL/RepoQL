using System.Globalization;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using ModelContextProtocol.Protocol;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Resources;

/// <summary>
/// Provides MCP resource handlers so clients can fetch repository content by RepoURI.
/// </summary>
internal sealed class RepoResourceService
{
    private const string SummaryUriPrefix = "summarize::";
    private readonly RepoQlClientProvider _clientProvider;

    public RepoResourceService(RepoQlClientProvider clientProvider)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    }

    public Task<TextResourceContents> FetchResourceAsync(string resourceUri, CancellationToken cancellationToken = default)
        => FetchResourceContentAsync(resourceUri, cancellationToken);

    private async Task<TextResourceContents> FetchResourceContentAsync(string uriString, CancellationToken cancellationToken)
    {
        var view = ResolveView(uriString, out var rawUri);

        if (!RepoUri.TryParse(rawUri, out var repoUri))
        {
            throw new ArgumentException($"Invalid RepoURI: {rawUri}", nameof(uriString));
        }

        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var document = await FetchDocumentDataAsync(client, repoUri, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            throw new FileNotFoundException($"No artifact found for RepoURI '{repoUri.AbsoluteUri}'.");
        }

        return view switch
        {
            ResourceView.Summary => await BuildSummaryResourceAsync(client, document, repoUri, uriString, cancellationToken).ConfigureAwait(false),
            _ => BuildDocumentResource(document, repoUri, uriString)
        };
    }

    private static TextResourceContents BuildDocumentResource(DocumentData document, RepoUri repoUri, string requestedUri)
    {
        var text = document.TextContent;
        if (text is null)
        {
            text = "(No text_content stored for this artifact. It may be binary or indexing is incomplete.)";
        }

        var sliced = SliceContent(text, repoUri);
        return new TextResourceContents
        {
            Uri = requestedUri,
            MimeType = string.IsNullOrWhiteSpace(document.MediaType) ? "text/plain; charset=utf-8" : document.MediaType,
            Text = sliced
        };
    }

    private async Task<TextResourceContents> BuildSummaryResourceAsync(
        IRepoQlClient client,
        DocumentData document,
        RepoUri repoUri,
        string requestedUri,
        CancellationToken cancellationToken)
    {
        var annotations = await FetchAnnotationsAsync(client, document.CanonicalUri, cancellationToken).ConfigureAwait(false);
        var markdown = BuildSummaryMarkdown(document, annotations, repoUri);
        return new TextResourceContents
        {
            Uri = requestedUri,
            MimeType = "text/markdown; charset=utf-8",
            Text = markdown
        };
    }

    private static string SliceContent(string text, RepoUri uri)
    {
        var result = text;

        if (uri.Loc.Char is { } charRange)
        {
            result = SliceByChar(result, charRange);
        }

        if (uri.Loc.Line is { } lineRange)
        {
            result = SliceByLine(result, lineRange);
        }

        return result;
    }

    private static string SliceByChar(string text, (long? Start, long? End) range)
    {
        if (text.Length == 0) return text;

        var start = (int)Math.Clamp(range.Start ?? 0, 0, text.Length);
        var end = (int)Math.Clamp(range.End ?? text.Length, start, text.Length);
        return text[start..end];
    }

    private static string SliceByLine(string text, (int? Start, int? End) range)
    {
        if (text.Length == 0) return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length == 0) return string.Empty;

        var startLine = Math.Max(range.Start ?? 1, 1);
        var endLine = Math.Max(range.End ?? startLine, startLine);
        startLine = Math.Min(startLine, lines.Length);
        endLine = Math.Min(endLine, lines.Length);

        return string.Join(Environment.NewLine, lines[(startLine - 1)..endLine]);
    }

    private static ResourceView ResolveView(string uriString, out string rawUri)
    {
        if (uriString.StartsWith(SummaryUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            rawUri = uriString.Substring(SummaryUriPrefix.Length);
            return ResourceView.Summary;
        }

        rawUri = uriString;
        return ResourceView.Document;
    }

    private static string? ExtractString(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => null
        };
    }

    private static string? FormatValue(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            Value.KindOneofCase.StructValue => JsonFormatter.Default.Format(value.StructValue),
            Value.KindOneofCase.ListValue => JsonFormatter.Default.Format(value.ListValue),
            Value.KindOneofCase.NullValue => null,
            _ => null
        };
    }

    private static async Task<DocumentData?> FetchDocumentDataAsync(IRepoQlClient client, RepoUri repoUri, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT a.text_content,
                   a.media_type,
                   a.headline,
                   a.summary,
                   a.structure,
                   n.uri,
                   a.id
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE lower(n.uri) = lower(?)
            LIMIT 1
            """;

        foreach (var candidate in EnumerateLookupUris(repoUri))
        {
            var response = await client.ExecuteRawQueryAsync(Sql, new object?[] { candidate }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response.Rows.Count == 0 || response.Rows[0].Values.Count < 7)
            {
                continue;
            }

            var values = response.Rows[0].Values;
            return new DocumentData(
                CanonicalUri: ExtractString(values[5]) ?? candidate,
                TextContent: ExtractString(values[0]),
                MediaType: ExtractString(values[1]),
                Headline: ExtractString(values[2]),
                Summary: ExtractString(values[3]),
                Structure: ExtractString(values[4]),
                ArtifactId: ExtractString(values[6])
            );
        }

        return null;
    }

    private static async Task<IReadOnlyList<AnnotationRecord>> FetchAnnotationsAsync(IRepoQlClient client, string canonicalUri, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT kind,
                   severity,
                   source,
                   rule_id,
                   message,
                   resolved_target_uri,
                   data,
                   created_at
            FROM annotations_for(?, NULL, NULL)
            LIMIT 20
            """;

        RawQueryResponse response;
        try
        {
            response = await client.ExecuteRawQueryAsync(Sql, new object?[] { canonicalUri }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<AnnotationRecord>();
        }
        if (response.Rows.Count == 0)
        {
            return Array.Empty<AnnotationRecord>();
        }

        var results = new List<AnnotationRecord>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            var values = row.Values;
            string GetValue(int index) => index < values.Count ? ExtractString(values[index]) ?? string.Empty : string.Empty;
            string? GetOptional(int index) => index < values.Count ? ExtractString(values[index]) : null;
            string? GetFormatted(int index) => index < values.Count ? FormatValue(values[index]) : null;

            DateTimeOffset? createdAt = null;
            var createdRaw = GetOptional(7);
            if (!string.IsNullOrWhiteSpace(createdRaw) && DateTimeOffset.TryParse(createdRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                createdAt = parsed;
            }

            results.Add(new AnnotationRecord(
                Kind: GetValue(0),
                Severity: GetValue(1),
                Source: GetValue(2),
                RuleId: GetOptional(3),
                Message: GetValue(4),
                ResolvedTargetUri: GetOptional(5),
                Data: GetFormatted(6),
                CreatedAt: createdAt
            ));
        }

        return results;
    }

    private static string BuildSummaryMarkdown(DocumentData document, IReadOnlyList<AnnotationRecord> annotations, RepoUri originalUri)
    {
        var sb = new StringBuilder();
        var headline = string.IsNullOrWhiteSpace(document.Headline) ? "(no headline stored)" : document.Headline!.Trim();
        sb.AppendLine("# ").AppendLine(headline).AppendLine();

        sb.AppendLine("## Summary");
        if (string.IsNullOrWhiteSpace(document.Summary))
        {
            sb.AppendLine("Not available.");
        }
        else
        {
            sb.AppendLine(document.Summary.Trim());
        }
        sb.AppendLine();

        sb.AppendLine("## Structure");
        if (string.IsNullOrWhiteSpace(document.Structure))
        {
            sb.AppendLine("Not available.");
        }
        else
        {
            sb.AppendLine("```");
            sb.AppendLine(document.Structure.TrimEnd());
            sb.AppendLine("```");
        }
        sb.AppendLine();

        sb.AppendLine("## Metadata");
        sb.AppendLine($"- RepoURI: `{document.CanonicalUri}`");
        if (!string.Equals(document.CanonicalUri, originalUri.AbsoluteUri, StringComparison.Ordinal))
        {
            sb.AppendLine($"- Requested URI: `{originalUri.AbsoluteUri}`");
        }
        if (!string.IsNullOrWhiteSpace(document.MediaType))
        {
            sb.AppendLine($"- Media type: `{document.MediaType}`");
        }
        sb.AppendLine();

        sb.AppendLine("## Annotations (top 20)");
        if (annotations.Count == 0)
        {
            sb.AppendLine("No annotations found for this document.");
        }
        else
        {
            foreach (var annotation in annotations)
            {
                var severity = string.IsNullOrWhiteSpace(annotation.Severity) ? "unknown" : annotation.Severity;
                var kind = string.IsNullOrWhiteSpace(annotation.Kind) ? "annotation" : annotation.Kind;
                var source = string.IsNullOrWhiteSpace(annotation.Source) ? "unknown source" : annotation.Source;
                var rule = string.IsNullOrWhiteSpace(annotation.RuleId) ? string.Empty : $" / {annotation.RuleId}";
                sb.AppendLine($"- **[{severity}]** `{kind}` — {annotation.Message}");
                sb.AppendLine($"  - Source: {source}{rule}");
                if (!string.IsNullOrWhiteSpace(annotation.ResolvedTargetUri))
                {
                    sb.AppendLine($"  - Target: `{annotation.ResolvedTargetUri}`");
                }
                if (!string.IsNullOrWhiteSpace(annotation.Data) && annotation.Data!.Length < 400)
                {
                    sb.AppendLine($"  - Data: {annotation.Data}");
                }
                else if (!string.IsNullOrWhiteSpace(annotation.Data))
                {
                    sb.AppendLine("  - Data: (omitted – payload too large to display inline)");
                }
                if (annotation.CreatedAt is { } created)
                {
                    sb.AppendLine($"  - Created: {created:yyyy-MM-dd HH:mm:ss K}");
                }
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> EnumerateLookupUris(RepoUri repoUri)
    {
        yield return repoUri.AbsoluteUri;
        if (!string.IsNullOrEmpty(repoUri.Loc.Raw))
        {
            yield return repoUri.Container.AbsoluteUri;
        }
    }

    private enum ResourceView
    {
        Document,
        Summary
    }

    private sealed record DocumentData(
        string CanonicalUri,
        string? TextContent,
        string? MediaType,
        string? Headline,
        string? Summary,
        string? Structure,
        string? ArtifactId);

    private sealed record AnnotationRecord(
        string Kind,
        string Severity,
        string Source,
        string? RuleId,
        string Message,
        string? ResolvedTargetUri,
        string? Data,
        DateTimeOffset? CreatedAt);

}
