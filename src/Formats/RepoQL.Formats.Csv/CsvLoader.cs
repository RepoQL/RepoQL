using System.Globalization;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Csv.Analysis;
using RepoQL.Formats.Csv.Surface;
using RepoQL.Templating;
using RepoQL.Templating.Filters;

namespace RepoQL.Formats.Csv;

/// <summary>
/// Loads and materializes delimited text files into graph records.
///
/// Purpose: Enables CSV/TSV/PSV files to be indexed as queryable table structure,
/// including per-column type inference and x-ray summaries.
///
/// Complexity: Handles delimiter detection, row parsing, inference sampling, template
/// model construction, and graph materialization in one cohesive format boundary.
/// </summary>
public sealed class CsvLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider
{
    internal const string StateMetadataKey = "csv.state";
    private const int MaxSampleRows = 100;

    private static readonly SemanticMediaType CsvMediaType =
        SemanticMediaType.Create("text", "csv").WithKind("csv.table");

    private static readonly SemanticMediaType TsvMediaType =
        SemanticMediaType.Create("text", "tab-separated-values").WithKind("tsv.table");

    private static readonly SemanticMediaType PsvMediaType =
        SemanticMediaType.Create("text", "plain").WithKind("data.psv");

    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(CsvLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Csv.Templates",
        configure: StandardFilters.RegisterAll);

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return IsSupportedKind(mediaType.Kind);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var name = artifact.File.Name;
        if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = CsvMediaType;
            return Task.FromResult(true);
        }

        if (name.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = TsvMediaType;
            return Task.FromResult(true);
        }

        if (name.EndsWith(".psv", StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = PsvMediaType;
            return Task.FromResult(true);
        }

        if (artifact.MediaType is null)
        {
            return Task.FromResult(false);
        }

        if (IsSupportedKind(artifact.MediaType.Kind))
        {
            return Task.FromResult(true);
        }

        if (string.Equals(artifact.MediaType.Type, "text", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(artifact.MediaType.Subtype, "csv", StringComparison.OrdinalIgnoreCase))
            {
                artifact.MediaType = artifact.MediaType.WithKind(CsvMediaType.Kind!);
                return Task.FromResult(true);
            }

            if (string.Equals(artifact.MediaType.Subtype, "tab-separated-values", StringComparison.OrdinalIgnoreCase))
            {
                artifact.MediaType = artifact.MediaType.WithKind(TsvMediaType.Kind!);
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required for CSV loader.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        var delimiterResult = DelimiterDetector.Detect(text);
        var parsedRows = ParseAllRows(text, delimiterResult.Delimiter);
        var sampleRows = parsedRows.Take(MaxSampleRows).ToList();

        var inference = ColumnTypeInferrer.Infer(sampleRows);
        var totalDataRows = Math.Max(0, parsedRows.Count - (inference.HasHeader ? 1 : 0));
        var sampledDataRows = Math.Max(0, sampleRows.Count - (inference.HasHeader ? 1 : 0));
        var scaledColumns = ScaleColumnTokenEstimates(inference.Columns, totalDataRows, sampledDataRows);

        var mediaType = artifact.MediaType ?? InferMediaTypeFromExtension(artifact.File.Name) ?? CsvMediaType;
        var surface = new CsvDocumentSurface
        {
            DocumentId = Guid.NewGuid(),
            Delimiter = delimiterResult.Delimiter,
            HasHeader = inference.HasHeader,
            RowCount = totalDataRows,
            ColumnCount = scaledColumns.Count,
            Columns = scaledColumns,
            TotalEstimatedTokens = scaledColumns.Sum(c => c.EstimatedTokens)
        };

        var state = new CsvDocumentState
        {
            Surface = surface,
            Digest = loaded.Digest,
            Size = loaded.ByteLength,
            MediaType = mediaType,
            StoreUri = artifact.RepoUri.ToString()
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        return new DocumentModel(artifact.RepoUri, mediaType, text, metadata: metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        var state = document.GetMetadataOrDefault<CsvDocumentState>(StateMetadataKey)
                    ?? throw new InvalidOperationException("CSV document missing state metadata.");

        var fileName = GetFileName(document.Uri);
        var mediaKind = string.IsNullOrWhiteSpace(state.MediaType.Kind)
            ? CsvMediaType.Kind!
            : state.MediaType.Kind!;
        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

        var columns = state.Surface.Columns
            .OrderBy(c => c.Index)
            .Select(c => new Dictionary<string, object?>
            {
                ["name"] = c.Name,
                ["index"] = c.Index,
                ["type"] = c.DataType.ToString().ToLowerInvariant(),
                ["sample_values"] = c.SampleValues.ToList(),
                ["min_value"] = c.MinValue,
                ["max_value"] = c.MaxValue,
                ["estimated_tokens"] = c.EstimatedTokens
            })
            .ToList();

        var typeCounts = state.Surface.Columns
            .GroupBy(c => c.DataType)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => new Dictionary<string, object?>
            {
                ["type"] = g.Key.ToString().ToLowerInvariant(),
                ["count"] = g.Count()
            })
            .ToList();

        var model = new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["size_bytes"] = state.Size,
            ["token_count"] = tokenCount ?? 0,
            ["media_kind"] = mediaKind,
            ["delimiter_name"] = GetDelimiterName(state.Surface.Delimiter),
            ["row_count"] = state.Surface.RowCount,
            ["column_count"] = state.Surface.ColumnCount,
            ["has_header"] = state.Surface.HasHeader,
            ["column_names"] = state.Surface.Columns.OrderBy(c => c.Index).Select(c => c.Name).ToList(),
            ["columns"] = columns,
            ["total_estimated_tokens"] = state.Surface.TotalEstimatedTokens,
            ["type_counts"] = typeCounts
        };

        var headline = _renderer.RenderAsync("explore/headline", model).GetAwaiter().GetResult();
        var summary = _renderer.RenderAsync("explore/summary", model).GetAwaiter().GetResult();
        var structure = _renderer.RenderAsync("explore/structure", model).GetAwaiter().GetResult();

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = document.Uri,
            Headline = headline,
            Summary = summary,
            Structure = structure,
            TokenCount = tokenCount
        };

        var now = DateTimeOffset.UtcNow;
        var docNode = new Node
        {
            Id = state.Surface.DocumentId,
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = state.MediaType.ToString(),
                ["delimiter"] = Convert.ToString(state.Surface.Delimiter, CultureInfo.InvariantCulture),
                ["row_count"] = state.Surface.RowCount,
                ["column_count"] = state.Surface.ColumnCount,
                ["has_header"] = state.Surface.HasHeader
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var ordinal = 0;

        foreach (var column in state.Surface.Columns.OrderBy(c => c.Index))
        {
            var span = new Span
            {
                Id = Guid.NewGuid(),
                DocumentId = docNode.Id,
                StartLine = 1,
                EndLine = Math.Max(1, state.Surface.RowCount)
            };
            spans.Add(span);

            var sampleValues = new JsonArray();
            foreach (var value in column.SampleValues)
            {
                sampleValues.Add(value);
            }

            var columnNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "csv_column",
                SpanId = span.Id,
                Uri = RepoUri.FromSymbol(document.Uri.Container, column.Name, span.StartLine, span.EndLine),
                Props = new JsonObject
                {
                    ["name"] = column.Name,
                    ["index"] = column.Index,
                    ["type"] = column.DataType.ToString().ToLowerInvariant(),
                    ["sample_values"] = sampleValues,
                    ["min_value"] = column.MinValue,
                    ["max_value"] = column.MaxValue,
                    ["estimated_tokens"] = column.EstimatedTokens
                },
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(columnNode);
            edges.Add(CreateHasPart(docNode.Id, columnNode.Id, docNode.Id, ordinal++, now));
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges]
        };
    }

    /// <summary>
    /// Returns SQL schema scripts for CSV macros.
    /// </summary>
    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("csv_macros", CsvMacrosSql.Value);
    }

    private static bool IsSupportedKind(string? kind)
    {
        return string.Equals(kind, CsvMediaType.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, TsvMediaType.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, PsvMediaType.Kind, StringComparison.OrdinalIgnoreCase);
    }

    private static SemanticMediaType? InferMediaTypeFromExtension(string fileName)
    {
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            return CsvMediaType;
        if (fileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
            return TsvMediaType;
        if (fileName.EndsWith(".psv", StringComparison.OrdinalIgnoreCase))
            return PsvMediaType;

        return null;
    }

    private static List<IReadOnlyList<string>> ParseAllRows(string text, char delimiter)
    {
        var rows = new List<IReadOnlyList<string>>();
        using var reader = new StringReader(text);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            rows.Add(DelimiterDetector.ParseFields(line, delimiter));
        }

        return rows;
    }

    private static IReadOnlyList<CsvColumnInfo> ScaleColumnTokenEstimates(
        IReadOnlyList<CsvColumnInfo> columns,
        int totalDataRows,
        int sampledDataRows)
    {
        if (columns.Count == 0 || sampledDataRows <= 0 || totalDataRows <= sampledDataRows)
            return columns;

        var scale = (double)totalDataRows / sampledDataRows;
        return columns
            .Select(c => c with
            {
                EstimatedTokens = (int)Math.Round(c.EstimatedTokens * scale, MidpointRounding.AwayFromZero)
            })
            .ToList();
    }

    private static string GetDelimiterName(char delimiter)
    {
        return delimiter switch
        {
            ',' => "comma",
            '\t' => "tab",
            '|' => "pipe",
            ';' => "semicolon",
            _ => Convert.ToString(delimiter, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var localPath = uri.LocalPath;
                if (!string.IsNullOrEmpty(localPath))
                    return Path.GetFileName(localPath);
            }
        }
        catch
        {
            // Fall through to URI parsing.
        }

        var absolutePath = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = absolutePath.LastIndexOf('/') >= 0
            ? absolutePath[(absolutePath.LastIndexOf('/') + 1)..]
            : absolutePath;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    private static Edge CreateHasPart(Guid documentId, Guid childId, Guid scopeDocumentId, int ordinal, DateTimeOffset timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = documentId,
            DstId = childId,
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocumentId,
            CreatedAt = timestamp
        };

    private static readonly Lazy<string> CsvMacrosSql = new(() =>
        ReadEmbeddedResource("RepoQL.Formats.Csv.Schema.csv_macros.sql"));

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(CsvLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
