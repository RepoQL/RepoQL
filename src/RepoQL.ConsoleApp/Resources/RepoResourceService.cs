using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Resources;

/// <summary>
/// Provides MCP resource handlers so clients can fetch repository content by RepoURI.
/// </summary>
internal sealed class RepoResourceService
{
    private static readonly ResourceTemplate DocumentTemplate = new()
    {
        Name = "document",
        Title = "RepoQL document",
        Description = "Fetch repository content by RepoURI (file:///…, help:///…, github://…). "
                    + "Supports #line= and #char= fragments for slicing, and glob patterns (e.g., file:///src/**/*.cs) to read multiple files.",
        UriTemplate = "{+uri}",
        MimeType = "text/plain; charset=utf-8"
    };

    private readonly RepoQlClientProvider _clientProvider;

    public RepoResourceService(RepoQlClientProvider clientProvider)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    }

    /// <summary>
    /// MCP handler: List available resource templates.
    /// Includes the generic document template plus a template for each imported repository mount.
    /// </summary>
    public async ValueTask<ListResourceTemplatesResult> ListTemplatesAsync(
        RequestContext<ListResourceTemplatesRequestParams> context,
        CancellationToken cancellationToken)
    {
        var templates = new List<ResourceTemplate> { DocumentTemplate };

        // Add templates for each persisted mount (e.g., imported GitHub repos)
        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            const string sql = "SELECT id, scheme, authority, path_prefix FROM file_system_mount";
            var response = await client.ExecuteRawQueryAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var row in response.Rows)
            {
                if (row.Values.Count < 4) continue;

                var id = ExtractString(row.Values[0]) ?? "";
                var scheme = ExtractString(row.Values[1]) ?? "";
                var authority = ExtractString(row.Values[2]) ?? "";
                var pathPrefix = ExtractString(row.Values[3]) ?? "";

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(scheme)) continue;

                // Build a descriptive URI template for this mount
                // e.g., github://anthropics/claude-code/{+path}
                var uriTemplate = string.IsNullOrEmpty(authority)
                    ? $"{scheme}:///{pathPrefix}/{{+path}}"
                    : $"{scheme}://{authority}/{pathPrefix}/{{+path}}";

                var displayName = string.IsNullOrEmpty(authority)
                    ? $"{scheme}:{pathPrefix}"
                    : $"{scheme}:{authority}/{pathPrefix}";

                templates.Add(new ResourceTemplate
                {
                    Name = id,
                    Title = $"Imported: {displayName}",
                    Description = $"Browse files from imported {scheme} repository",
                    UriTemplate = uriTemplate,
                    MimeType = "text/plain; charset=utf-8"
                });
            }
        }
        catch
        {
            // If mount query fails, still return the base document template
        }

        var result = new ListResourceTemplatesResult
        {
            ResourceTemplates = templates
        };
        return result;
    }

    /// <summary>
    /// MCP handler: Read a resource by URI or glob pattern.
    /// </summary>
    public async ValueTask<ReadResourceResult> ReadResourceAsync(
        RequestContext<ReadResourceRequestParams> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var uriString = context.Params?.Uri;
        if (string.IsNullOrWhiteSpace(uriString))
        {
            throw new ArgumentException("Resource URI cannot be empty.", nameof(context));
        }

        // Check if this is a glob pattern
        if (IsGlobPattern(uriString))
        {
            var contents = await FetchGlobContentsAsync(uriString, cancellationToken).ConfigureAwait(false);
            return new ReadResourceResult { Contents = contents };
        }

        var content = await FetchResourceContentAsync(uriString, cancellationToken).ConfigureAwait(false);
        return new ReadResourceResult
        {
            Contents = [content]
        };
    }

    private static bool IsGlobPattern(string uri)
    {
        return uri.Contains('*') || uri.Contains('?');
    }

    private async Task<List<ResourceContents>> FetchGlobContentsAsync(string globUri, CancellationToken cancellationToken)
    {
        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);

        // Query for matching documents using glob_match
        const string sql = """
            SELECT n.uri, a.text_content
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.kind = 'document'
              AND (glob_match(n.uri, ?, default_scheme := 'file:///')
                   OR glob_match(n.uri, ?, default_scheme := 'help:///'))
            ORDER BY n.uri
            LIMIT 50
            """;

        var response = await client.ExecuteRawQueryAsync(sql, [globUri, globUri], cancellationToken: cancellationToken).ConfigureAwait(false);

        var contents = new List<ResourceContents>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            var uri = row.Values.Count > 0 ? ExtractString(row.Values[0]) : null;
            var text = row.Values.Count > 1 ? ExtractString(row.Values[1]) : null;

            if (string.IsNullOrEmpty(uri)) continue;

            contents.Add(new TextResourceContents
            {
                Uri = uri,
                MimeType = "text/plain; charset=utf-8",
                Text = text ?? "(empty or binary file)"
            });
        }

        if (contents.Count == 0)
        {
            contents.Add(new TextResourceContents
            {
                Uri = globUri,
                MimeType = "text/plain; charset=utf-8",
                Text = $"No files matched pattern: {globUri}"
            });
        }

        return contents;
    }

    public Task<TextResourceContents> FetchResourceAsync(string resourceUri, CancellationToken cancellationToken = default)
        => FetchResourceContentAsync(resourceUri, cancellationToken);

    public async Task<List<TextResourceContents>> FetchGlobAsync(string globUri, CancellationToken cancellationToken = default)
    {
        var contents = await FetchGlobContentsAsync(globUri, cancellationToken).ConfigureAwait(false);
        return contents.OfType<TextResourceContents>().ToList();
    }

    private async Task<TextResourceContents> FetchResourceContentAsync(string uriString, CancellationToken cancellationToken)
    {
        if (!RepoUri.TryParse(uriString, out var repoUri))
        {
            throw new ArgumentException($"Invalid RepoURI: {uriString}", nameof(uriString));
        }

        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);

        // Symbol URIs without explicit line ranges use snippet macro for resolution
        if (repoUri.Loc.Symbol is not null && repoUri.Loc.Line is null)
        {
            return await FetchSymbolContentAsync(client, uriString, cancellationToken).ConfigureAwait(false);
        }

        var document = await FetchDocumentDataAsync(client, repoUri, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            throw new FileNotFoundException($"No artifact found for RepoURI '{repoUri.AbsoluteUri}'.");
        }

        return BuildDocumentResource(document, repoUri, uriString);
    }

    private async Task<TextResourceContents> FetchSymbolContentAsync(
        IRepoQlClient client, string uri, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT text, resolved_uri, language
            FROM snippet(?, 5)
            ORDER BY line_number
            """;

        var response = await client.ExecuteRawQueryAsync(sql, [uri], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.Rows.Count == 0)
        {
            throw new FileNotFoundException($"Symbol not found: {uri}");
        }

        // Concatenate all lines from snippet result
        var text = string.Join("\n", response.Rows
            .Select(r => ExtractString(r.Values[0]) ?? "")
            .ToList());

        var resolvedUri = ExtractString(response.Rows[0].Values[1]) ?? uri;
        var language = ExtractString(response.Rows[0].Values[2]);
        var mimeType = !string.IsNullOrEmpty(language)
            ? $"text/plain; charset=utf-8; lang={language}"
            : "text/plain; charset=utf-8";

        return new TextResourceContents
        {
            Uri = resolvedUri,
            MimeType = mimeType,
            Text = text
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

    private static IEnumerable<string> EnumerateLookupUris(RepoUri repoUri)
    {
        yield return repoUri.AbsoluteUri;
        if (!string.IsNullOrEmpty(repoUri.Loc.Raw))
        {
            yield return repoUri.Container.AbsoluteUri;
        }
    }

    private sealed record DocumentData(
        string CanonicalUri,
        string? TextContent,
        string? MediaType,
        string? Headline,
        string? Summary,
        string? Structure,
        string? ArtifactId);
}
