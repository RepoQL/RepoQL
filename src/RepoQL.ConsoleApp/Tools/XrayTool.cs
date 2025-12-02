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

    public enum SearchScope
    {
        File,
        Object,
        Both
    }

    private const string SummarizeInstructions = """
                                                 Find and preview repository content. Search files, objects (functions/classes/methods/headings/etc), or both.

                                                 <KillerFeatures>
                                                 - Inventory what exists with the least tokens possible so you can make informed decisions (detail = headline)
                                                 - Find what you are looking for with extremely effective search tools and token efficient responses (with configurable level of detail)
                                                 - Get saliant information you need from large files without reading them (e.g. detail=summary on sln file lists all projects)
                                                 </KillerFeatures>

                                                 <Examples>
                                                 detail=headline, scope=object, keywords=RefreshAsync → find method by name
                                                 detail=snippet, scope=object, keywords=IService → show interface code
                                                 detail=summary, scope=file, question=How does caching work? → find relevant docs
                                                 detail=summary, pattern=**/UserService.cs → understand file before reading
                                                 </Examples>
                                                 
                                                 Flow: headline → summary → snippet / Read tool for full content.
                                                 """; 

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false, Name = "xray"), Description(SummarizeInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> SummarizeAsync(
        [Description("Glob to filter by path. Examples: \"**/*.cs\", \"**/tests/**\", \"**/docs/*\".")] string? pattern = null,
        [Description("Media type filter (rarely needed). Example: \"*csharp*\". Prefer pattern instead.")] string? type = null,
        [Description("Literal term to find - boosts exact matches. Examples: \"RefreshAsync\", \"IEmbeddingProvider\".")] string? keywords = null,
        [Description("Natural language for semantic search. Example: \"How does authentication work?\".")] string? question = null,
        [Description("Required. Output detail: headline (one-line inventory), summary (structure), snippet (code).")] string? detail = null,
        [Description("Search scope: file (documents), object (functions/classes/symbols), both (default).")] string? scope = null,
        [Description("Max results. Defaults: headline=1000, summary=100, snippet=10.")] int limit = 0,
        CancellationToken cancellationToken = default)
    {
        SummaryDetail detailKind;
        SearchScope searchScope;

        try
        {
            detailKind = ParseDetail(detail);
            searchScope = ParseScope(scope);
        }
        catch (ArgumentException ex)
        {
            return $"Parameter error: {ex.Message}";
        }

        var effectiveLimit = limit > 0 ? limit : GetDefaultLimit(detailKind);
        var (containerPattern, fragment) = SplitGlobAndFragment(pattern);
        var globPattern = NormalizeGlobPattern(containerPattern);
        var typePattern = NormalizeTypePattern(type);

        IRepoQlClient client;
        try
        {
            client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return FormatError("Failed to connect to RepoQL server", ex, pattern, keywords, question, detail, scope);
        }

        // Query based on scope
        var documentRows = new List<DocumentRow>();
        var objectRows = new List<ObjectRow>();
        var errors = new List<string>();

        if (searchScope == SearchScope.File || searchScope == SearchScope.Both)
        {
            var docLimit = searchScope == SearchScope.Both ? effectiveLimit / 2 : effectiveLimit;
            try
            {
                documentRows = await QueryDocumentsAsync(client, globPattern, typePattern, keywords, question, Math.Max(docLimit, 1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"Document search failed: {ExtractErrorMessage(ex)}");
            }
        }

        if (searchScope == SearchScope.Object || searchScope == SearchScope.Both)
        {
            var objLimit = searchScope == SearchScope.Both ? effectiveLimit / 2 : effectiveLimit;
            try
            {
                objectRows = await QueryObjectsAsync(client, globPattern, typePattern, keywords, question, Math.Max(objLimit, 1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"Object search failed: {ExtractErrorMessage(ex)}");
            }
        }

        // If all queries failed, return error details
        if (errors.Count > 0 && documentRows.Count == 0 && objectRows.Count == 0)
        {
            return FormatError("Search failed", errors, pattern, keywords, question, detail, scope);
        }

        if (documentRows.Count == 0 && objectRows.Count == 0)
        {
            return searchScope switch
            {
                SearchScope.File => "No files matched the supplied filters.",
                SearchScope.Object => "No objects matched the supplied filters. Tip: Provide keywords or question for object search.",
                _ => "No results matched the supplied filters."
            };
        }

        var builder = new StringBuilder();
        var isFirst = true;

        // Format document rows
        foreach (var row in documentRows)
        {
            if (!isFirst)
            {
                if (detailKind == SummaryDetail.Headline)
                    builder.AppendLine();
                else
                    builder.AppendLine("---");
            }
            isFirst = false;

            switch (detailKind)
            {
                case SummaryDetail.Headline:
                    await FormatHeadlineAsync(builder, client, row, cancellationToken).ConfigureAwait(false);
                    break;
                case SummaryDetail.Summary:
                    await FormatDefaultAsync(builder, client, row, cancellationToken).ConfigureAwait(false);
                    break;
                case SummaryDetail.Snippet:
                    var uriForSnippet = AppendFragment(row.Uri, fragment);
                    await FormatSnippetAsync(builder, client, row, uriForSnippet, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        // Format object rows
        foreach (var row in objectRows)
        {
            if (!isFirst)
            {
                if (detailKind == SummaryDetail.Headline)
                    builder.AppendLine();
                else
                    builder.AppendLine("---");
            }
            isFirst = false;

            switch (detailKind)
            {
                case SummaryDetail.Headline:
                    FormatObjectHeadline(builder, row);
                    break;
                case SummaryDetail.Summary:
                    FormatObjectSummary(builder, row);
                    break;
                case SummaryDetail.Snippet:
                    FormatObjectSnippet(builder, row);
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

    private static SearchScope ParseScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return SearchScope.Both; // Default to both
        }

        return scope.Trim().ToLowerInvariant() switch
        {
            "file" or "files" or "document" or "documents" => SearchScope.File,
            "object" or "objects" or "symbol" or "symbols" => SearchScope.Object,
            "both" or "all" => SearchScope.Both,
            _ => throw new ArgumentException("Scope must be one of: file, object, both.", nameof(scope))
        };
    }

    private static int GetDefaultLimit(SummaryDetail detail) => detail switch
    {
        SummaryDetail.Headline => 1000,
        SummaryDetail.Summary => 100,
        SummaryDetail.Snippet => 10,
        _ => 100
    };

	private static string? NormalizeGlobPattern(string? glob) =>
		string.IsNullOrWhiteSpace(glob) ? null : glob.Trim();

    internal static (string? ContainerPattern, string? Fragment) SplitGlobAndFragment(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return (null, null);
        }

        var trimmed = pattern.Trim();
        var hashIndex = trimmed.IndexOf('#');
        if (hashIndex < 0)
        {
            return (trimmed, null);
        }

        var container = trimmed[..hashIndex].Trim();
        var fragmentPart = trimmed[(hashIndex + 1)..].Trim();
        var fragment = string.IsNullOrEmpty(fragmentPart) ? "#" : $"#{fragmentPart}";

        return (string.IsNullOrWhiteSpace(container) ? null : container, fragment);
    }

    internal static string AppendFragment(string uri, string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return uri;
        }

        return uri.Contains('#', StringComparison.Ordinal) ? uri : $"{uri}{fragment}";
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
		string? globPattern,
		string? typePattern,
		string? keywords,
		string? question,
		int limit,
		CancellationToken cancellationToken)
	{
		var whereClauses = new List<string> { "n.kind = 'document'" };
		var whereParameters = new List<object?>();
		if (!string.IsNullOrEmpty(globPattern))
		{
			whereClauses.Add("(glob_match(n.uri, ?, default_scheme := 'file:///') OR glob_match(n.uri, ?, default_scheme := 'docs:///') OR glob_match(n.uri, ?, default_scheme := 'embed:///'))");
			whereParameters.Add(globPattern);
			whereParameters.Add(globPattern);
			whereParameters.Add(globPattern);
		}

		if (!string.IsNullOrWhiteSpace(typePattern))
		{
			whereClauses.Add("a.media_type ILIKE ?");
			whereParameters.Add(typePattern);
		}

		var parameters = new List<object?>();
		string sql;

		const string WherePlaceholder = "{WHERE_CLAUSE}";

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
					FROM file_search(?, k := {searchLimit}, max_cand := 5000, question := ?)
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
					WHERE {WherePlaceholder}
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
			parameters.Add(keywordsParam);
			parameters.Add(questionParam);
			parameters.AddRange(whereParameters);
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
				WHERE {WherePlaceholder}
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
			parameters.AddRange(whereParameters);
			parameters.Add(limit);
		}

		sql = sql.Replace(WherePlaceholder, string.Join(" AND ", whereClauses));

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

    private async Task<List<ObjectRow>> QueryObjectsAsync(
        IRepoQlClient client,
        string? globPattern,
        string? typePattern,
        string? keywords,
        string? question,
        int limit,
        CancellationToken cancellationToken)
    {
        // Build query text from keywords and/or question
        var queryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(keywords))
        {
            queryParts.Add(keywords.Trim());
        }
        if (!string.IsNullOrWhiteSpace(question))
        {
            queryParts.Add(question.Trim());
        }

        var queryText = queryParts.Count > 0 ? string.Join(" ", queryParts) : string.Empty;

        // If no search terms provided, we can't use object_search meaningfully
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return new List<ObjectRow>();
        }

        // Build the object_search query with optional filters
        // Note: All parameters interpolated directly due to issues with ? placeholders in macro calls
        var escapedQuery = queryText.Replace("'", "''");
        var escapedGlob = globPattern?.Replace("'", "''");
        var escapedType = typePattern?.Replace("'", "''");

        var sql = $"""
            SELECT
                uri,
                symbol,
                kind,
                headline,
                structure,
                snippet,
                line_start,
                line_end,
                lang,
                score
            FROM object_search('{escapedQuery}', k := {limit}{(escapedGlob != null ? $", uri_glob := '{escapedGlob}'" : "")}{(escapedType != null ? $", mime_glob := '{escapedType}'" : "")})
            ORDER BY score DESC
            """;

        var response = await client.ExecuteRawQueryAsync(sql, null, null, cancellationToken).ConfigureAwait(false);
        var list = new List<ObjectRow>(response.Rows.Count);

        foreach (var row in response.Rows)
        {
            var values = row.Values;
            if (values.Count == 0) continue;

            var uri = ExtractString(values[0]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(uri)) continue;

            string? symbol = values.Count > 1 ? ExtractString(values[1]) : null;
            string? kind = values.Count > 2 ? ExtractString(values[2]) : null;
            string? headline = values.Count > 3 ? ExtractString(values[3]) : null;
            string? structure = values.Count > 4 ? ExtractString(values[4]) : null;
            string? snippet = values.Count > 5 ? ExtractString(values[5]) : null;

            int? lineStart = null;
            if (values.Count > 6)
            {
                var lineStr = ExtractString(values[6]);
                if (!string.IsNullOrWhiteSpace(lineStr) && int.TryParse(lineStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    lineStart = parsed;
                }
            }

            int? lineEnd = null;
            if (values.Count > 7)
            {
                var lineStr = ExtractString(values[7]);
                if (!string.IsNullOrWhiteSpace(lineStr) && int.TryParse(lineStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    lineEnd = parsed;
                }
            }

            string? lang = values.Count > 8 ? ExtractString(values[8]) : null;

            double? score = null;
            if (values.Count > 9)
            {
                var scoreString = ExtractString(values[9]);
                if (!string.IsNullOrWhiteSpace(scoreString) && double.TryParse(scoreString, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScore))
                {
                    score = parsedScore;
                }
            }

            list.Add(new ObjectRow(
                Uri: uri,
                Symbol: symbol,
                Kind: kind,
                Headline: headline,
                Structure: structure,
                Snippet: snippet,
                LineStart: lineStart,
                LineEnd: lineEnd,
                Lang: lang,
                Score: score));
        }

        return list;
    }

    private async Task FormatHeadlineAsync(StringBuilder builder, IRepoQlClient client, DocumentRow row, CancellationToken cancellationToken)
    {
        // Fetch annotation counts
        var annotations = await FetchAnnotationsAsync(client, row.Uri, cancellationToken).ConfigureAwait(false);
        var errorCount = annotations.Count(a => a.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        var warningCount = annotations.Count(a => a.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase));

        // Format: [ ⚠️ 5 | ❌ 2 ] uri - headline
        if (errorCount > 0 || warningCount > 0)
        {
            builder.Append("[ ");
            if (warningCount > 0)
            {
                builder.Append("⚠️ ");
                builder.Append(warningCount);
            }
            if (errorCount > 0)
            {
                if (warningCount > 0) builder.Append(" | ");
                builder.Append("❌ ");
                builder.Append(errorCount);
            }
            builder.Append(" ] ");
        }

        builder.Append(row.Uri);
        builder.Append(" - ");
        builder.Append(!string.IsNullOrWhiteSpace(row.Headline) ? row.Headline!.Trim() : ExtractFileName(row.Uri));
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

    private async Task FormatSnippetAsync(StringBuilder builder, IRepoQlClient client, DocumentRow row, string uriForSnippet, CancellationToken cancellationToken)
    {
        builder.AppendLine(row.Uri);
        var snippetText = await FetchSnippetAsync(client, uriForSnippet, cancellationToken).ConfigureAwait(false);
        var mediaType = !string.IsNullOrWhiteSpace(row.MediaType) ? row.MediaType : "text/plain";
        builder.AppendLine($"```{mediaType}");
        builder.AppendLine(snippetText);
        builder.AppendLine("```");

        var annotations = await FetchAnnotationsAsync(client, row.Uri, cancellationToken).ConfigureAwait(false);
        builder.AppendLine(FormatAnnotations(annotations));
    }

    // Object formatting methods
    private static void FormatObjectHeadline(StringBuilder builder, ObjectRow row)
    {
        // Format: [kind] symbol (lines N-M) - headline
        // e.g., [csharp.member] BuildEmbeddingWorkItems (lines 493-508) - builds work items for embedding
        var kindBadge = !string.IsNullOrWhiteSpace(row.Kind) ? $"[{row.Kind}] " : "";
        var symbol = !string.IsNullOrWhiteSpace(row.Symbol) ? row.Symbol : "(anonymous)";
        var lineInfo = row.LineStart.HasValue
            ? row.LineEnd.HasValue && row.LineEnd != row.LineStart
                ? $" (lines {row.LineStart}-{row.LineEnd})"
                : $" (line {row.LineStart})"
            : "";

        builder.Append(kindBadge);
        builder.Append(symbol);
        builder.Append(lineInfo);

        if (!string.IsNullOrWhiteSpace(row.Headline) && row.Headline != row.Symbol)
        {
            builder.Append(" - ");
            builder.Append(row.Headline.Trim());
        }

        builder.Append(" → ");
        builder.Append(row.Uri);
    }

    private static void FormatObjectSummary(StringBuilder builder, ObjectRow row)
    {
        // Format: uri with symbol and line info, plus structure
        var symbol = !string.IsNullOrWhiteSpace(row.Symbol) ? row.Symbol : "(anonymous)";
        var kind = !string.IsNullOrWhiteSpace(row.Kind) ? row.Kind : "object";

        builder.AppendLine($"{row.Uri}");
        builder.AppendLine($"{kind}: {symbol}");

        if (row.LineStart.HasValue)
        {
            var lineRange = row.LineEnd.HasValue && row.LineEnd != row.LineStart
                ? $"lines {row.LineStart}-{row.LineEnd}"
                : $"line {row.LineStart}";
            builder.AppendLine($"Location: {lineRange}");
        }

        if (!string.IsNullOrWhiteSpace(row.Structure))
        {
            builder.AppendLine("Structure:");
            builder.AppendLine(row.Structure.TrimEnd());
        }
        else if (!string.IsNullOrWhiteSpace(row.Headline))
        {
            builder.AppendLine(row.Headline.Trim());
        }
    }

    private static void FormatObjectSnippet(StringBuilder builder, ObjectRow row)
    {
        // Format: uri, metadata, and code snippet
        var symbol = !string.IsNullOrWhiteSpace(row.Symbol) ? row.Symbol : "(anonymous)";
        var kind = !string.IsNullOrWhiteSpace(row.Kind) ? row.Kind : "object";

        builder.AppendLine($"{row.Uri}");
        builder.AppendLine($"{kind}: {symbol}");

        if (row.LineStart.HasValue)
        {
            var lineRange = row.LineEnd.HasValue && row.LineEnd != row.LineStart
                ? $"lines {row.LineStart}-{row.LineEnd}"
                : $"line {row.LineStart}";
            builder.AppendLine($"Location: {lineRange}");
        }

        var lang = !string.IsNullOrWhiteSpace(row.Lang) ? row.Lang : "text";
        if (!string.IsNullOrWhiteSpace(row.Snippet))
        {
            builder.AppendLine($"```{lang}");
            builder.AppendLine(row.Snippet.TrimEnd());
            builder.AppendLine("```");
        }
        else if (!string.IsNullOrWhiteSpace(row.Structure))
        {
            builder.AppendLine("Structure:");
            builder.AppendLine(row.Structure.TrimEnd());
        }
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
            WITH doc AS (
                SELECT id AS doc_id FROM node
                WHERE lower(node.uri) = lower(repository_uri_container(?))
            )
            SELECT a.severity,
                   a.source,
                   a.rule_id,
                   a.message,
                   ann.resolved_target_uri,
                   s.start_line,
                   s.end_line
            FROM annotation a
            JOIN doc ON a.scope_document_id = doc.doc_id
            LEFT JOIN annotations ann ON ann.id = a.id
            LEFT JOIN span s ON a.target_span_id = s.id
            ORDER BY s.start_line NULLS LAST, ann.severity_rank DESC
            LIMIT 100
            """;

        RawQueryResponse response;
        try
        {
            response = await client.ExecuteRawQueryAsync(sql, new object?[] { uri }, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Return a synthetic annotation indicating the failure - annotations are supplementary,
            // so we don't fail the whole operation but inform the caller something went wrong
            return
            [
                new AnnotationRow(
                    Severity: "warning",
                    Source: "xray",
                    RuleId: "annotation-fetch-failed",
                    Message: $"Could not load annotations: {ExtractErrorMessage(ex)}",
                    TargetUri: uri,
                    StartLine: null,
                    EndLine: null)
            ];
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

            int? startLine = null;
            if (values.Count > 5)
            {
                var lineStr = ExtractString(values[5]);
                if (!string.IsNullOrWhiteSpace(lineStr) && int.TryParse(lineStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    startLine = parsed;
                }
            }

            int? endLine = null;
            if (values.Count > 6)
            {
                var lineStr = ExtractString(values[6]);
                if (!string.IsNullOrWhiteSpace(lineStr) && int.TryParse(lineStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    endLine = parsed;
                }
            }

            annotations.Add(new AnnotationRow(severity ?? "info", source, ruleId, message, targetUri, startLine, endLine));
        }

        return annotations;
    }

    private async Task<string> FetchSnippetAsync(IRepoQlClient client, string uri, CancellationToken cancellationToken)
    {
        // snippet() macro automatically returns full file for document URIs (no fragment)
        // and targeted context for URIs with fragments (like #line=10)
        const string sql = """
            SELECT line_number,
                   text,
                   is_focus
            FROM snippet(?, 3)
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

            // GitHub Actions format: ::error file={name},line={line},endLine={endLine},title={title}::{message}
            var emoji = annotation.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? "❌" :
                       annotation.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "⚠️" : "ℹ️";

            builder.Append("::");
            builder.Append(annotation.Severity);

            if (!string.IsNullOrWhiteSpace(annotation.TargetUri))
            {
                // Extract filename from URI
                var fileName = ExtractFileName(annotation.TargetUri);
                builder.Append(" file=");
                builder.Append(fileName);
            }

            if (annotation.StartLine.HasValue)
            {
                builder.Append(",line=");
                builder.Append(annotation.StartLine.Value);

                if (annotation.EndLine.HasValue && annotation.EndLine.Value != annotation.StartLine.Value)
                {
                    builder.Append(",endLine=");
                    builder.Append(annotation.EndLine.Value);
                }
            }

            builder.Append(",title=");
            builder.Append(emoji);
            builder.Append(rulePart);
            builder.Append("::");
            builder.AppendLine(annotation.Message ?? "(no message)");
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

    /// <summary>
    /// Extracts a meaningful error message from an exception, handling nested gRPC exceptions.
    /// </summary>
    private static string ExtractErrorMessage(Exception ex)
    {
        // Unwrap RpcException to get the actual error message
        if (ex is Grpc.Core.RpcException rpcEx)
        {
            var detail = rpcEx.Status.Detail;
            if (!string.IsNullOrWhiteSpace(detail))
                return detail;
        }

        // Check for inner exceptions (common with gRPC/network errors)
        if (ex.InnerException is not null)
        {
            var inner = ExtractErrorMessage(ex.InnerException);
            if (!string.IsNullOrWhiteSpace(inner) && inner != ex.Message)
                return $"{ex.Message} -> {inner}";
        }

        return ex.Message;
    }

    /// <summary>
    /// Formats an error message with context about the failed operation.
    /// </summary>
    private static string FormatError(string operation, Exception ex, string? pattern, string? keywords, string? question, string? detail, string? scope)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Error: {operation}");
        builder.AppendLine($"Details: {ExtractErrorMessage(ex)}");
        AppendQueryContext(builder, pattern, keywords, question, detail, scope);
        return builder.ToString();
    }

    /// <summary>
    /// Formats an error message with multiple error details.
    /// </summary>
    private static string FormatError(string operation, IReadOnlyList<string> errors, string? pattern, string? keywords, string? question, string? detail, string? scope)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Error: {operation}");
        foreach (var error in errors)
        {
            builder.AppendLine($"  - {error}");
        }
        AppendQueryContext(builder, pattern, keywords, question, detail, scope);
        return builder.ToString();
    }

    /// <summary>
    /// Appends query context to help diagnose issues.
    /// </summary>
    private static void AppendQueryContext(StringBuilder builder, string? pattern, string? keywords, string? question, string? detail, string? scope)
    {
        builder.AppendLine();
        builder.AppendLine("Query context:");
        if (!string.IsNullOrWhiteSpace(pattern))
            builder.AppendLine($"  pattern: {pattern}");
        if (!string.IsNullOrWhiteSpace(keywords))
            builder.AppendLine($"  keywords: {keywords}");
        if (!string.IsNullOrWhiteSpace(question))
            builder.AppendLine($"  question: {question}");
        if (!string.IsNullOrWhiteSpace(detail))
            builder.AppendLine($"  detail: {detail}");
        if (!string.IsNullOrWhiteSpace(scope))
            builder.AppendLine($"  scope: {scope}");
    }

    private sealed record DocumentRow(
        string Uri,
        string? Headline,
        string? Summary,
        string? Structure,
        string? MediaType,
        long? Size,
        double? Score);

    private sealed record ObjectRow(
        string Uri,
        string? Symbol,
        string? Kind,
        string? Headline,
        string? Structure,
        string? Snippet,
        int? LineStart,
        int? LineEnd,
        string? Lang,
        double? Score);

    private sealed record AnnotationRow(
        string Severity,
        string? Source,
        string? RuleId,
        string? Message,
        string? TargetUri,
        int? StartLine,
        int? EndLine);
}
