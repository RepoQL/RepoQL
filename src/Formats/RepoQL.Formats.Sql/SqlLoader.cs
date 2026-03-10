using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;
using RepoQL.Templating.Filters;

namespace RepoQL.Formats.Sql;

/// <summary>
/// Loader + materializer for SQL files.
/// Extracts tables, views, functions, procedures, and indexes.
/// </summary>
public sealed partial class SqlLoader : IFormatLoader, IFormatMaterializer
{
    internal const string StateKey = "sql.state";

    private static readonly SemanticMediaType SqlType = SemanticMediaType
        .Create("text", "plain")
        .WithKind("query.sql");

    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(SqlLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Sql.Templates",
        configure: StandardFilters.RegisterAll);

    // Regex patterns for SQL object detection
    [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?(?<type>TABLE|VIEW|FUNCTION|PROCEDURE|MACRO|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>\w+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CreateObjectRegex();

    [GeneratedRegex(@"CREATE\s+(?<unique>UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>\w+)\s+ON\s+(?<table>\w+)\s*\((?<columns>[^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CreateIndexRegex();

    /// <inheritdoc />
    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return string.Equals(mediaType.Kind, SqlType.Kind, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var name = artifact.File.Name;
        if (name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = SqlType;
            return Task.FromResult(true);
        }
        return Task.FromResult(artifact.MediaType is not null &&
            string.Equals(artifact.MediaType.Kind, "query.sql", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null) throw new InvalidOperationException("RepoUri required for SQL loader.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = loaded.Text;
        var digest = loaded.Digest;

        var objects = ParseSqlObjects(text);

        var state = new SqlState
        {
            Digest = digest.ToString(),
            Size = loaded.ByteLength,
            MediaType = artifact.MediaType ?? SqlType,
            StoreUri = artifact.RepoUri.ToString(),
            Objects = objects
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateKey] = state
        };

        return new DocumentModel(artifact.RepoUri, state.MediaType, text, metadata: metadata);
    }

    /// <inheritdoc />
    public Records Materialize(DocumentModel document)
    {
        var state = document.GetMetadataOrDefault<SqlState>(StateKey)
                    ?? throw new InvalidOperationException("SQL missing state");

        var fileName = GetFileName(document.Uri);

        // Build template model
        var primaryObject = state.Objects.FirstOrDefault(o =>
            o.Type is SqlObjectType.Table or SqlObjectType.View or SqlObjectType.Function or SqlObjectType.Procedure or SqlObjectType.Macro);

        var indexes = state.Objects.Where(o => o.Type == SqlObjectType.Index).ToList();
        var tables = state.Objects.Where(o => o.Type == SqlObjectType.Table).ToList();
        var views = state.Objects.Where(o => o.Type == SqlObjectType.View).ToList();
        var functions = state.Objects.Where(o => o.Type is SqlObjectType.Function or SqlObjectType.Procedure or SqlObjectType.Macro).ToList();

        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

        var model = new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["size_bytes"] = state.Size,
            ["token_count"] = tokenCount,
            ["primary_object"] = primaryObject is null ? null : new Dictionary<string, object?>
            {
                ["type"] = primaryObject.Type.ToString().ToUpperInvariant(),
                ["name"] = primaryObject.Name,
                ["columns"] = primaryObject.Columns.Select(c => new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["type"] = c.Type
                }).ToList(),
                ["return_type"] = primaryObject.ReturnType,
                ["source_tables"] = primaryObject.SourceTables
            },
            ["tables"] = tables.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["columns"] = t.Columns.Select(c => new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["type"] = c.Type
                }).ToList()
            }).ToList(),
            ["views"] = views.Select(v => new Dictionary<string, object?>
            {
                ["name"] = v.Name,
                ["source_tables"] = v.SourceTables
            }).ToList(),
            ["functions"] = functions.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["type"] = f.Type.ToString().ToUpperInvariant(),
                ["params"] = f.Columns.Select(c => new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["type"] = c.Type,
                    ["default"] = c.Default
                }).ToList(),
                ["return_type"] = f.ReturnType
            }).ToList(),
            ["indexes"] = indexes.Select(i => new Dictionary<string, object?>
            {
                ["name"] = i.Name,
                ["on_table"] = i.OnTable,
                ["columns"] = i.IndexColumns,
                ["is_unique"] = i.IsUnique
            }).ToList(),
            ["index_count"] = indexes.Count,
            ["has_multiple_objects"] = state.Objects.Count > 1
        };

        var headline = _renderer.Render("explore/headline", model);
        var summary = _renderer.Render("explore/summary", model);
        var structure = _renderer.Render("explore/structure", model);

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = document.Uri.ToString(),
            Headline = headline,
            Summary = summary,
            Structure = structure,
            TokenCount = tokenCount
        };

        var now = DateTimeOffset.UtcNow;

        var docNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = state.MediaType.ToString(),
                ["object_count"] = state.Objects.Count
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var edges = new List<Edge>();
        var spans = new List<Span>();

        var ordinal = 0;

        // Create nodes for each SQL object
        foreach (var obj in state.Objects)
        {
            Span? span = null;
            if (obj.Line > 0)
            {
                var s = new Span
                {
                    Id = Guid.NewGuid(),
                    DocumentId = docNode.Id,
                    StartLine = obj.Line,
                    EndLine = obj.Line,
                };
                span = s;
                spans.Add(s);
            }

            var props = new JsonObject
            {
                ["type"] = GetObjectTypeToken(obj.Type),
                ["name"] = obj.Name
            };

            if (obj.OnTable is not null)
                props["on_table"] = obj.OnTable;
            if (obj.IsUnique)
                props["is_unique"] = true;

            var objType = GetObjectTypeToken(obj.Type);
            var nodeHeadline = obj.OnTable is not null
                ? $"{objType} {obj.Name} on {obj.OnTable}"
                : $"{objType} {obj.Name}";

            var node = new Node
            {
                Id = Guid.NewGuid(),
                Kind = $"sql.{objType}",
                SpanId = span?.Id,
                Uri = RepoUri.FromSymbol(document.Uri.Container, obj.Name, span?.StartLine, span?.EndLine),
                Props = props,
                Headline = nodeHeadline,
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(node);
            edges.Add(HasPart(docNode.Id, node.Id, docNode.Id, ordinal++, now));
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges]
        };
    }

    private static List<SqlObject> ParseSqlObjects(string text)
    {
        var objects = new List<SqlObject>();
        var lines = text.Split('\n');

        // Find CREATE TABLE/VIEW/FUNCTION/etc.
        foreach (Match match in CreateObjectRegex().Matches(text))
        {
            var typeName = match.Groups["type"].Value.ToUpperInvariant();
            var name = match.Groups["name"].Value;
            var line = GetLineNumber(text, match.Index);

            var objType = typeName switch
            {
                "TABLE" => SqlObjectType.Table,
                "VIEW" => SqlObjectType.View,
                "FUNCTION" => SqlObjectType.Function,
                "PROCEDURE" => SqlObjectType.Procedure,
                "MACRO" => SqlObjectType.Macro,
                "TRIGGER" => SqlObjectType.Trigger,
                _ => SqlObjectType.Table
            };

            var obj = new SqlObject
            {
                Type = objType,
                Name = name,
                Line = line,
                Columns = objType == SqlObjectType.Table
                    ? ParseTableColumns(text, match.Index)
                    : objType is SqlObjectType.Function or SqlObjectType.Procedure or SqlObjectType.Macro
                        ? ParseFunctionParams(text, match.Index)
                        : [],
                ReturnType = objType is SqlObjectType.Function or SqlObjectType.Macro
                    ? ParseReturnType(text, match.Index)
                    : null,
                SourceTables = objType == SqlObjectType.View
                    ? ParseViewSources(text, match.Index)
                    : []
            };

            objects.Add(obj);
        }

        // Find CREATE INDEX
        foreach (Match match in CreateIndexRegex().Matches(text))
        {
            var name = match.Groups["name"].Value;
            var table = match.Groups["table"].Value;
            var columns = match.Groups["columns"].Value
                .Split(',')
                .Select(c => c.Trim().Split(' ')[0].Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();
            var isUnique = match.Groups["unique"].Success;
            var line = GetLineNumber(text, match.Index);

            objects.Add(new SqlObject
            {
                Type = SqlObjectType.Index,
                Name = name,
                Line = line,
                OnTable = table,
                IndexColumns = columns,
                IsUnique = isUnique
            });
        }

        return objects;
    }

    private static List<SqlColumn> ParseTableColumns(string text, int startIndex)
    {
        var columns = new List<SqlColumn>();

        // Find the opening paren after CREATE TABLE name
        var parenStart = text.IndexOf('(', startIndex);
        if (parenStart < 0) return columns;

        // Find matching closing paren (accounting for nested parens)
        var depth = 1;
        var parenEnd = parenStart + 1;
        while (parenEnd < text.Length && depth > 0)
        {
            if (text[parenEnd] == '(') depth++;
            else if (text[parenEnd] == ')') depth--;
            parenEnd++;
        }

        if (depth != 0) return columns;

        var body = text.Substring(parenStart + 1, parenEnd - parenStart - 2);

        // Split on commas (but not inside parens)
        var parts = SplitOnTopLevelCommas(body);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Skip constraints
            if (trimmed.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("FOREIGN", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Parse column: name TYPE [DEFAULT ...] [constraints]
            var tokens = trimmed.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 1)
            {
                var colName = tokens[0].Trim('"', '`', '[', ']');
                var colType = tokens.Length >= 2 ? tokens[1] : null;

                // Handle multi-word types like "NOT NULL" suffix
                if (colType is not null && tokens.Length >= 3 &&
                    !string.Equals(tokens[2], "DEFAULT", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(tokens[2], "NOT", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(tokens[2], "PRIMARY", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(tokens[2], "REFERENCES", StringComparison.OrdinalIgnoreCase))
                {
                    // Could be part of type like "DOUBLE PRECISION"
                    if (tokens[2].All(c => char.IsLetter(c)))
                        colType += " " + tokens[2];
                }

                columns.Add(new SqlColumn { Name = colName, Type = colType });
            }
        }

        return columns;
    }

    private static List<SqlColumn> ParseFunctionParams(string text, int startIndex)
    {
        var columns = new List<SqlColumn>();

        // Find the opening paren
        var parenStart = text.IndexOf('(', startIndex);
        if (parenStart < 0) return columns;

        // Find matching closing paren
        var depth = 1;
        var parenEnd = parenStart + 1;
        while (parenEnd < text.Length && depth > 0)
        {
            if (text[parenEnd] == '(') depth++;
            else if (text[parenEnd] == ')') depth--;
            parenEnd++;
        }

        if (depth != 0) return columns;

        var body = text.Substring(parenStart + 1, parenEnd - parenStart - 2);
        var parts = SplitOnTopLevelCommas(body);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Parse param: name TYPE [DEFAULT value] or just TYPE (for anonymous params)
            var tokens = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2)
            {
                var name = tokens[0];
                var type = tokens[1];
                string? defaultVal = null;

                var defaultIdx = Array.FindIndex(tokens, t => string.Equals(t, "DEFAULT", StringComparison.OrdinalIgnoreCase) ||
                                                              string.Equals(t, ":=", StringComparison.OrdinalIgnoreCase));
                if (defaultIdx >= 0 && defaultIdx + 1 < tokens.Length)
                {
                    defaultVal = tokens[defaultIdx + 1];
                }

                columns.Add(new SqlColumn { Name = name, Type = type, Default = defaultVal });
            }
            else if (tokens.Length == 1)
            {
                // Anonymous param or just type
                columns.Add(new SqlColumn { Name = tokens[0], Type = null });
            }
        }

        return columns;
    }

    private static string? ParseReturnType(string text, int startIndex)
    {
        // Look for RETURNS or → after the params
        var searchText = text.Substring(startIndex, Math.Min(500, text.Length - startIndex));

        // Find closing paren of params
        var parenStart = searchText.IndexOf('(', StringComparison.Ordinal);
        if (parenStart < 0) return null;

        var depth = 1;
        var parenEnd = parenStart + 1;
        while (parenEnd < searchText.Length && depth > 0)
        {
            if (searchText[parenEnd] == '(') depth++;
            else if (searchText[parenEnd] == ')') depth--;
            parenEnd++;
        }

        if (parenEnd >= searchText.Length) return null;

        var afterParams = searchText.Substring(parenEnd);

        // Look for RETURNS keyword
        var returnsMatch = Regex.Match(afterParams, @"RETURNS\s+(\w+)", RegexOptions.IgnoreCase);
        if (returnsMatch.Success)
            return returnsMatch.Groups[1].Value;

        // Look for → TABLE or AS TABLE
        if (afterParams.Contains("TABLE", StringComparison.OrdinalIgnoreCase))
            return "TABLE";

        return null;
    }

    private static List<string> ParseViewSources(string text, int startIndex)
    {
        var sources = new List<string>();

        // Look for FROM and JOIN clauses
        var searchText = text.Substring(startIndex, Math.Min(2000, text.Length - startIndex));

        // Find tables after FROM
        var fromMatches = Regex.Matches(searchText, @"\bFROM\s+(\w+)", RegexOptions.IgnoreCase);
        foreach (Match m in fromMatches)
            sources.Add(m.Groups[1].Value);

        // Find tables after JOIN
        var joinMatches = Regex.Matches(searchText, @"\bJOIN\s+(\w+)", RegexOptions.IgnoreCase);
        foreach (Match m in joinMatches)
            sources.Add(m.Groups[1].Value);

        return sources.Distinct().ToList();
    }

    private static List<string> SplitOnTopLevelCommas(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start < text.Length)
            parts.Add(text[start..]);

        return parts;
    }

    private static int GetLineNumber(string text, int charIndex)
    {
        var line = 1;
        for (var i = 0; i < charIndex && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var lp = uri.LocalPath;
                if (!string.IsNullOrEmpty(lp)) return Path.GetFileName(lp);
            }
        }
        catch { }
        var ap = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = ap.LastIndexOf('/') >= 0 ? ap[(ap.LastIndexOf('/') + 1)..] : ap;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    private static string GetObjectTypeToken(SqlObjectType type) => type switch
    {
        SqlObjectType.Table => "table",
        SqlObjectType.View => "view",
        SqlObjectType.Function => "function",
        SqlObjectType.Procedure => "procedure",
        SqlObjectType.Macro => "macro",
        SqlObjectType.Trigger => "trigger",
        SqlObjectType.Index => "index",
        _ => "table"
    };

    private static Edge HasPart(Guid documentId, Guid childId, Guid scopeDocumentId, int ordinal, DateTimeOffset ts)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = documentId,
            DstId = childId,
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocumentId,
            CreatedAt = ts
        };
}
