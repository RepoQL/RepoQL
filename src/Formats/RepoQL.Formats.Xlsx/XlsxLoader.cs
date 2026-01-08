using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Xlsx.Analysis;
using RepoQL.Formats.Xlsx.Surface;
using RepoQL.Templating;

namespace RepoQL.Formats.Xlsx;

/// <summary>
/// Loads and materializes XLSX (Excel) files into graph records.
///
/// Purpose: Enables discovery and querying of Excel workbook structure including
/// worksheets, tables, named ranges, charts, and pivot tables. Supports header
/// detection and column type analysis for small business spreadsheet discovery.
///
/// Complexity: Parsing OpenXML structure requires navigating multiple document parts.
/// Header detection and column analysis add additional processing. The complexity
/// is contained here and exposed as simple surface models to the rest of the system.
/// </summary>
public sealed partial class XlsxLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider
{
    internal const string StateMetadataKey = "xlsx.state";

    private readonly ILogger<XlsxLoader> _logger;

    private static readonly SemanticMediaType XlsxMediaType = SemanticMediaType
        .Create("application", "vnd.openxmlformats-officedocument.spreadsheetml.sheet")
        .WithKind("xlsx.workbook");

    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(XlsxLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Xlsx.Templates");

    public XlsxLoader(ILogger<XlsxLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<XlsxLoader>.Instance;
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        if (string.Equals(mediaType.Kind, XlsxMediaType.Kind, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(mediaType.Type, XlsxMediaType.Type, StringComparison.OrdinalIgnoreCase)
               && string.Equals(mediaType.Subtype, XlsxMediaType.Subtype, StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var name = artifact.File.Name.ToLowerInvariant();
        if (name.EndsWith(".xlsx"))
        {
            artifact.MediaType = XlsxMediaType;
            return Task.FromResult(true);
        }

        if (artifact.MediaType is not null &&
            string.Equals(artifact.MediaType.Subtype, "vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = artifact.MediaType.WithKind("xlsx.workbook");
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required to load XLSX.");

        var mediaType = artifact.MediaType ?? XlsxMediaType;

        // Read file bytes for digest computation
        await using var stream = artifact.File.CreateReadStream();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        var bytes = memoryStream.ToArray();
        var digest = ContentDigest.FromBytes(bytes);
        var size = bytes.Length;

        // Parse with OpenXML
        memoryStream.Position = 0;
        using var document = SpreadsheetDocument.Open(memoryStream, false);

        var surface = ParseWorkbook(document);
        var docId = Guid.NewGuid();

        var documentProps = new JsonObject
        {
            ["media_type"] = mediaType.ToString(),
            ["byte_size"] = size,
            ["sheet_count"] = surface.Worksheets.Count,
            ["total_rows"] = surface.TotalRows,
            ["table_count"] = surface.TotalTables,
            ["named_range_count"] = surface.NamedRanges.Count,
            ["chart_count"] = surface.TotalCharts,
            ["pivot_table_count"] = surface.TotalPivotTables,
            ["has_formulas"] = surface.HasFormulas,
            ["has_totals"] = surface.HasTotals
        };

        var finalSurface = surface with
        {
            DocumentId = docId,
            DocumentProperties = documentProps
        };

        var state = new XlsxDocumentState
        {
            Surface = finalSurface,
            Digest = digest,
            Size = size,
            MediaType = mediaType,
            StoreUri = artifact.RepoUri.ToString()
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        // Note: We don't store text content for XLSX files (binary format)
        return new DocumentModel(artifact.RepoUri, mediaType, string.Empty, null, metadata);
    }

    private WorkbookSurface ParseWorkbook(SpreadsheetDocument document)
    {
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidOperationException("XLSX file has no workbook part.");

        var workbook = workbookPart.Workbook;
        var sheets = workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var definedNames = workbook.DefinedNames?.Elements<DefinedName>().ToList() ?? [];

        var worksheets = new List<WorksheetInfo>();
        var namedRanges = new List<NamedRangeInfo>();

        // Parse worksheets
        int sheetIndex = 0;
        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value == null)
                continue;

            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            var worksheetInfo = ParseWorksheet(
                worksheetPart,
                sheet,
                sheetIndex,
                stylesheet,
                sharedStrings);

            worksheets.Add(worksheetInfo);
            sheetIndex++;
        }

        // Parse named ranges
        foreach (var definedName in definedNames)
        {
            var namedRange = ParseNamedRange(definedName);
            if (namedRange != null)
                namedRanges.Add(namedRange);
        }

        return new WorkbookSurface
        {
            DocumentId = Guid.Empty, // Will be set later
            DocumentProperties = new JsonObject(), // Will be set later
            Worksheets = worksheets,
            NamedRanges = namedRanges
        };
    }

    private WorksheetInfo ParseWorksheet(
        WorksheetPart worksheetPart,
        Sheet sheet,
        int sheetIndex,
        Stylesheet? stylesheet,
        SharedStringTable? sharedStrings)
    {
        var worksheet = worksheetPart.Worksheet;
        var sheetData = worksheet.GetFirstChild<SheetData>();

        var nodeId = Guid.NewGuid();
        var spanId = Guid.NewGuid();
        var sheetName = sheet.Name?.Value ?? $"Sheet{sheetIndex + 1}";
        var isHidden = sheet.State?.Value == SheetStateValues.Hidden ||
                       sheet.State?.Value == SheetStateValues.VeryHidden;

        // Count rows and columns
        int rowCount = 0;
        int columnCount = 0;
        string? usedRange = null;

        if (sheetData != null)
        {
            var rows = sheetData.Elements<Row>().ToList();
            rowCount = rows.Count;

            if (rows.Count > 0)
            {
                var allCells = rows.SelectMany(r => r.Elements<Cell>()).ToList();
                if (allCells.Count > 0)
                {
                    var maxCol = allCells
                        .Select(c => c.CellReference?.Value)
                        .Where(r => r != null)
                        .Select(r => HeaderDetector.ColumnLetterToIndex(GetColumnLetter(r!)))
                        .DefaultIfEmpty(-1)
                        .Max();

                    columnCount = maxCol + 1;

                    var firstCell = allCells.First().CellReference?.Value ?? "A1";
                    var lastRow = rows.Last().RowIndex?.Value ?? (uint)rowCount;
                    var lastColLetter = HeaderDetector.IndexToColumnLetter(maxCol);
                    usedRange = $"{GetColumnLetter(firstCell)}1:{lastColLetter}{lastRow}";
                }
            }
        }

        // Detect header row
        var mergeCells = worksheet.GetFirstChild<MergeCells>();
        var headerResult = HeaderDetector.DetectHeaderRow(sheetData, mergeCells, stylesheet, sharedStrings);

        // Analyze columns
        IReadOnlyList<ColumnInfo> columns = [];
        if (headerResult.HasHeader && sheetData != null)
        {
            columns = ColumnAnalyzer.AnalyzeAllColumns(
                sheetData,
                headerResult.Columns,
                headerResult.HeaderRowIndex!.Value,
                stylesheet,
                sharedStrings);
        }
        else if (headerResult.Columns.Count > 0)
        {
            // No header, but we have column positions - create basic column info
            columns = headerResult.Columns.Select(c => new ColumnInfo
            {
                Letter = c.Letter,
                Index = c.Index,
                Header = null,
                DataType = ColumnDataType.Unknown
            }).ToList();
        }

        // Count formulas and detect totals
        int formulaCount = sheetData != null ? ColumnAnalyzer.CountFormulas(sheetData) : 0;
        bool hasTotals = sheetData != null && ColumnAnalyzer.HasAggregateFormulas(sheetData);

        // Parse tables
        var tables = ParseTables(worksheetPart, stylesheet, sharedStrings);

        // Parse charts
        var charts = ParseCharts(worksheetPart);

        // Parse pivot tables
        var pivotTables = ParsePivotTables(worksheetPart);

        return new WorksheetInfo
        {
            NodeId = nodeId,
            SpanId = spanId,
            Name = sheetName,
            Index = sheetIndex,
            RowCount = rowCount,
            ColumnCount = columnCount,
            UsedRange = usedRange,
            IsHidden = isHidden,
            HasHeaderRow = headerResult.HasHeader,
            HeaderRowIndex = headerResult.HeaderRowIndex,
            HeaderConfidence = headerResult.Confidence,
            Columns = columns,
            FormulaCount = formulaCount,
            HasTotals = hasTotals,
            Tables = tables,
            Charts = charts,
            PivotTables = pivotTables
        };
    }

    private IReadOnlyList<TableInfo> ParseTables(
        WorksheetPart worksheetPart,
        Stylesheet? stylesheet,
        SharedStringTable? sharedStrings)
    {
        var tables = new List<TableInfo>();

        foreach (var tableDefPart in worksheetPart.TableDefinitionParts)
        {
            var table = tableDefPart.Table;
            if (table == null)
                continue;

            var tableColumns = new List<TableColumnInfo>();
            if (table.TableColumns != null)
            {
                foreach (var col in table.TableColumns.Elements<TableColumn>())
                {
                    tableColumns.Add(new TableColumnInfo
                    {
                        Name = col.Name?.Value ?? "Unknown",
                        CalculatedFormula = col.CalculatedColumnFormula?.Text,
                        TotalsFunction = col.TotalsRowFunction?.Value.ToString()
                    });
                }
            }

            var range = table.Reference?.Value ?? "";
            var (rowCount, colCount) = ParseRangeDimensions(range);

            tables.Add(new TableInfo
            {
                NodeId = Guid.NewGuid(),
                SpanId = Guid.NewGuid(),
                Name = table.Name?.Value ?? "UnnamedTable",
                DisplayName = table.DisplayName?.Value,
                Range = range,
                RowCount = rowCount,
                ColumnCount = colCount,
                HasHeaderRow = table.HeaderRowCount?.Value != 0,
                HasTotalsRow = table.TotalsRowCount?.Value > 0,
                Columns = tableColumns
            });
        }

        return tables;
    }

    private IReadOnlyList<ChartInfo> ParseCharts(WorksheetPart worksheetPart)
    {
        var charts = new List<ChartInfo>();

        var drawingPart = worksheetPart.DrawingsPart;
        if (drawingPart == null)
            return charts;

        foreach (var chartPart in drawingPart.ChartParts)
        {
            try
            {
                var chartSpace = chartPart.ChartSpace;
                var chart = chartSpace?.GetFirstChild<DocumentFormat.OpenXml.Drawing.Charts.Chart>();
                if (chart == null)
                    continue;

                var plotArea = chart.PlotArea;
                string chartType = "unknown";
                int seriesCount = 0;

                // Detect chart type from first chart element
                if (plotArea != null)
                {
                    var firstChart = plotArea.Elements().FirstOrDefault(e =>
                        e.LocalName.EndsWith("Chart", StringComparison.OrdinalIgnoreCase));

                    if (firstChart != null)
                    {
                        chartType = firstChart.LocalName.Replace("Chart", "").ToLowerInvariant();

                        // Count series
                        seriesCount = firstChart.Descendants()
                            .Count(d => d.LocalName == "ser");
                    }
                }

                var title = chart.Title?.ChartText?.RichText?.InnerText ??
                            chart.Title?.ChartText?.InnerText;

                charts.Add(new ChartInfo
                {
                    NodeId = Guid.NewGuid(),
                    SpanId = Guid.NewGuid(),
                    Name = $"Chart{charts.Count + 1}",
                    Title = title,
                    ChartType = chartType,
                    SeriesCount = seriesCount,
                    HasLegend = chart.Legend != null
                });
            }
            catch
            {
                // Skip charts that can't be parsed
            }
        }

        return charts;
    }

    private IReadOnlyList<PivotTableInfo> ParsePivotTables(WorksheetPart worksheetPart)
    {
        var pivotTables = new List<PivotTableInfo>();

        foreach (var pivotTablePart in worksheetPart.PivotTableParts)
        {
            try
            {
                var pivotTableDef = pivotTablePart.PivotTableDefinition;
                if (pivotTableDef == null)
                    continue;

                var rowFields = new List<string>();
                var colFields = new List<string>();
                var valueFields = new List<string>();
                var filterFields = new List<string>();

                // Get field names from cache definition
                var cacheDefPart = pivotTablePart.PivotTableCacheDefinitionPart;
                var cacheFields = cacheDefPart?.PivotCacheDefinition?.CacheFields?
                    .Elements<CacheField>()
                    .Select(f => f.Name?.Value ?? "")
                    .ToList() ?? [];

                // Parse row fields
                if (pivotTableDef.RowFields != null)
                {
                    foreach (var field in pivotTableDef.RowFields.Elements<Field>())
                    {
                        var idx = (int)(field.Index?.Value ?? 0);
                        if (idx >= 0 && idx < cacheFields.Count)
                            rowFields.Add(cacheFields[idx]);
                    }
                }

                // Parse column fields
                if (pivotTableDef.ColumnFields != null)
                {
                    foreach (var field in pivotTableDef.ColumnFields.Elements<Field>())
                    {
                        var idx = (int)(field.Index?.Value ?? 0);
                        if (idx >= 0 && idx < cacheFields.Count)
                            colFields.Add(cacheFields[idx]);
                    }
                }

                // Parse data fields (values)
                if (pivotTableDef.DataFields != null)
                {
                    foreach (var dataField in pivotTableDef.DataFields.Elements<DataField>())
                    {
                        var name = dataField.Name?.Value ?? "";
                        if (string.IsNullOrEmpty(name))
                        {
                            var idx = (int)(dataField.Field?.Value ?? 0);
                            if (idx >= 0 && idx < cacheFields.Count)
                                name = $"Sum of {cacheFields[idx]}";
                        }
                        valueFields.Add(name);
                    }
                }

                pivotTables.Add(new PivotTableInfo
                {
                    NodeId = Guid.NewGuid(),
                    SpanId = Guid.NewGuid(),
                    Name = pivotTableDef.Name?.Value ?? "PivotTable",
                    Location = pivotTableDef.Location?.Reference?.Value,
                    RowFields = rowFields,
                    ColumnFields = colFields,
                    ValueFields = valueFields,
                    FilterFields = filterFields
                });
            }
            catch
            {
                // Skip pivot tables that can't be parsed
            }
        }

        return pivotTables;
    }

    private NamedRangeInfo? ParseNamedRange(DefinedName definedName)
    {
        var name = definedName.Name?.Value;
        var refersTo = definedName.Text;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(refersTo))
            return null;

        // Skip built-in names like _xlnm.Print_Area
        var isBuiltIn = name.StartsWith("_xlnm.", StringComparison.OrdinalIgnoreCase);

        return new NamedRangeInfo
        {
            NodeId = Guid.NewGuid(),
            SpanId = Guid.NewGuid(),
            Name = name,
            RefersTo = refersTo,
            Scope = definedName.LocalSheetId != null ? "worksheet" : "workbook",
            Comment = definedName.Comment?.Value,
            IsHidden = definedName.Hidden?.Value ?? false,
            IsBuiltIn = isBuiltIn
        };
    }

    private static (int rows, int cols) ParseRangeDimensions(string range)
    {
        try
        {
            var parts = range.Split(':');
            if (parts.Length != 2)
                return (0, 0);

            var (startCol, startRow) = ParseCellReference(parts[0]);
            var (endCol, endRow) = ParseCellReference(parts[1]);

            return (endRow - startRow + 1, endCol - startCol + 1);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (int col, int row) ParseCellReference(string reference)
    {
        int i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
            i++;

        var colLetter = reference[..i];
        var row = int.Parse(reference[i..]);
        var col = HeaderDetector.ColumnLetterToIndex(colLetter);

        return (col, row);
    }

    private static string GetColumnLetter(string cellReference)
    {
        int i = 0;
        while (i < cellReference.Length && char.IsLetter(cellReference[i]))
            i++;
        return cellReference[..i];
    }

    public Records Materialize(DocumentModel document)
    {
        if (document.GetMetadataOrDefault<XlsxDocumentState>(StateMetadataKey) is not { } state)
            throw new InvalidOperationException("XLSX document missing state metadata.");

        // Compute x-ray fields via Liquid templates
        string? headline = null;
        string? summary = null;
        string? structure = null;
        int? tokenCount = null;

        try
        {
            var fileName = GetFileName(document.Uri);
            var model = BuildXrayModel(state, fileName);

            // Render summary and structure first so we can calculate token count for headline
            summary = _renderer.RenderAsync("xray/summary", model).GetAwaiter().GetResult();
            structure = _renderer.RenderAsync("xray/structure", model).GetAwaiter().GetResult();

            // For binary formats, estimate tokens from the text representation (summary + structure)
            var textForTokens = string.Join("\n", new[] { summary, structure }.Where(s => !string.IsNullOrEmpty(s)));
            tokenCount = TokenEstimator.EstimateTokensSafe(textForTokens);

            // Add token count to model for headline template
            model["token_count"] = tokenCount ?? 0;

            headline = _renderer.RenderAsync("xray/headline", model).GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore templating errors; x-ray is best-effort
        }

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = null, // Binary format, no text content
            StoreUri = state.StoreUri,
            Headline = headline,
            Summary = summary,
            Structure = structure,
            TokenCount = tokenCount
        };

        var nodes = new List<Node>();
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var now = DateTimeOffset.UtcNow;

        // Create document node
        var docNode = new Node
        {
            Id = state.Surface.DocumentId,
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = state.Surface.DocumentProperties,
            CreatedAt = now,
            UpdatedAt = now
        };
        nodes.Add(docNode);

        int ordinal = 0;

        // Create worksheet nodes
        foreach (var worksheet in state.Surface.Worksheets)
        {
            var wsSpan = new Span
            {
                Id = worksheet.SpanId,
                DocumentId = docNode.Id,
                StartLine = 1,
                EndLine = worksheet.RowCount > 0 ? worksheet.RowCount : 1
            };
            spans.Add(wsSpan);

            var columnTypesJson = new JsonObject();
            foreach (var col in worksheet.Columns.Take(26)) // Limit to first 26 columns
            {
                if (col.DataType != ColumnDataType.Unknown)
                {
                    columnTypesJson[col.Letter] = col.DataType.ToString().ToLowerInvariant();
                }
            }

            var wsNode = new Node
            {
                Id = worksheet.NodeId,
                Kind = "xlsx_worksheet",
                SpanId = worksheet.SpanId,
                Props = new JsonObject
                {
                    ["name"] = worksheet.Name,
                    ["index"] = worksheet.Index,
                    ["row_count"] = worksheet.RowCount,
                    ["column_count"] = worksheet.ColumnCount,
                    ["used_range"] = worksheet.UsedRange,
                    ["hidden"] = worksheet.IsHidden,
                    ["has_header_row"] = worksheet.HasHeaderRow,
                    ["header_row_index"] = worksheet.HeaderRowIndex,
                    ["header_confidence"] = worksheet.HeaderConfidence,
                    ["formula_count"] = worksheet.FormulaCount,
                    ["has_totals"] = worksheet.HasTotals,
                    ["column_types"] = columnTypesJson
                },
                Headline = BuildWorksheetHeadline(worksheet),
                Structure = BuildWorksheetStructure(worksheet),
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(wsNode);
            edges.Add(CreateHasPart(docNode.Id, wsNode.Id, docNode.Id, ordinal++, now));

            // Create table nodes
            int tableOrdinal = 0;
            foreach (var table in worksheet.Tables)
            {
                var tableSpan = new Span
                {
                    Id = table.SpanId,
                    DocumentId = docNode.Id
                };
                spans.Add(tableSpan);

                var tableColumnsJson = new JsonArray();
                foreach (var col in table.Columns)
                {
                    tableColumnsJson.Add(new JsonObject
                    {
                        ["name"] = col.Name,
                        ["type"] = col.DataType.ToString().ToLowerInvariant(),
                        ["totals_function"] = col.TotalsFunction
                    });
                }

                var tableNode = new Node
                {
                    Id = table.NodeId,
                    Kind = "xlsx_table",
                    SpanId = table.SpanId,
                    Props = new JsonObject
                    {
                        ["name"] = table.Name,
                        ["display_name"] = table.DisplayName,
                        ["range"] = table.Range,
                        ["row_count"] = table.RowCount,
                        ["column_count"] = table.ColumnCount,
                        ["has_header_row"] = table.HasHeaderRow,
                        ["has_totals_row"] = table.HasTotalsRow,
                        ["columns"] = tableColumnsJson
                    },
                    CreatedAt = now,
                    UpdatedAt = now
                };
                nodes.Add(tableNode);
                edges.Add(CreateHasPart(wsNode.Id, tableNode.Id, docNode.Id, tableOrdinal++, now));
            }

            // Create chart nodes
            int chartOrdinal = 0;
            foreach (var chart in worksheet.Charts)
            {
                var chartSpan = new Span
                {
                    Id = chart.SpanId,
                    DocumentId = docNode.Id
                };
                spans.Add(chartSpan);

                var chartNode = new Node
                {
                    Id = chart.NodeId,
                    Kind = "xlsx_chart",
                    SpanId = chart.SpanId,
                    Props = new JsonObject
                    {
                        ["name"] = chart.Name,
                        ["title"] = chart.Title,
                        ["chart_type"] = chart.ChartType,
                        ["series_count"] = chart.SeriesCount,
                        ["data_range"] = chart.DataRange,
                        ["has_legend"] = chart.HasLegend
                    },
                    CreatedAt = now,
                    UpdatedAt = now
                };
                nodes.Add(chartNode);
                edges.Add(CreateHasPart(wsNode.Id, chartNode.Id, docNode.Id, chartOrdinal++, now));
            }

            // Create pivot table nodes
            int pivotOrdinal = 0;
            foreach (var pivot in worksheet.PivotTables)
            {
                var pivotSpan = new Span
                {
                    Id = pivot.SpanId,
                    DocumentId = docNode.Id
                };
                spans.Add(pivotSpan);

                var pivotNode = new Node
                {
                    Id = pivot.NodeId,
                    Kind = "xlsx_pivot_table",
                    SpanId = pivot.SpanId,
                    Props = new JsonObject
                    {
                        ["name"] = pivot.Name,
                        ["source_range"] = pivot.SourceRange,
                        ["location"] = pivot.Location,
                        ["row_fields"] = new JsonArray(pivot.RowFields.Select(f => JsonValue.Create(f)).ToArray()),
                        ["column_fields"] = new JsonArray(pivot.ColumnFields.Select(f => JsonValue.Create(f)).ToArray()),
                        ["value_fields"] = new JsonArray(pivot.ValueFields.Select(f => JsonValue.Create(f)).ToArray()),
                        ["filter_fields"] = new JsonArray(pivot.FilterFields.Select(f => JsonValue.Create(f)).ToArray())
                    },
                    CreatedAt = now,
                    UpdatedAt = now
                };
                nodes.Add(pivotNode);
                edges.Add(CreateHasPart(wsNode.Id, pivotNode.Id, docNode.Id, pivotOrdinal++, now));
            }
        }

        // Create named range nodes
        int namedRangeOrdinal = 0;
        foreach (var namedRange in state.Surface.NamedRanges.Where(nr => !nr.IsBuiltIn))
        {
            var nrSpan = new Span
            {
                Id = namedRange.SpanId,
                DocumentId = docNode.Id
            };
            spans.Add(nrSpan);

            var nrNode = new Node
            {
                Id = namedRange.NodeId,
                Kind = "xlsx_named_range",
                SpanId = namedRange.SpanId,
                Props = new JsonObject
                {
                    ["name"] = namedRange.Name,
                    ["refers_to"] = namedRange.RefersTo,
                    ["scope"] = namedRange.Scope,
                    ["comment"] = namedRange.Comment,
                    ["hidden"] = namedRange.IsHidden
                },
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(nrNode);
            edges.Add(CreateHasPart(docNode.Id, nrNode.Id, docNode.Id, namedRangeOrdinal++, now));
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges]
        };
    }

    private Dictionary<string, object?> BuildXrayModel(XlsxDocumentState state, string fileName)
    {
        var surface = state.Surface;

        // Build sheet summaries
        var sheets = surface.Worksheets.Select(ws => new Dictionary<string, object?>
        {
            ["name"] = ws.Name,
            ["row_count"] = ws.RowCount,
            ["column_count"] = ws.ColumnCount,
            ["hidden"] = ws.IsHidden,
            ["has_header_row"] = ws.HasHeaderRow,
            ["header_row_index"] = ws.HeaderRowIndex,
            ["detected_columns"] = ws.Columns.Select(c => new Dictionary<string, object?>
            {
                ["letter"] = c.Letter,
                ["header"] = c.Header ?? $"Column {c.Letter}",
                ["type"] = c.DataType.ToString().ToLowerInvariant(),
                ["sample_value"] = c.SampleValue,
                ["sample_values"] = c.SampleValues,
                ["is_formula"] = c.SampleIsFormula,
                ["has_formulas"] = c.HasFormulas,
                ["min_value"] = c.MinValue,
                ["max_value"] = c.MaxValue,
                ["min_date"] = c.MinDate,
                ["max_date"] = c.MaxDate,
                ["min_date_formatted"] = FormatExcelDate(c.MinDate),
                ["max_date_formatted"] = FormatExcelDate(c.MaxDate)
            }).ToList(),
            ["tables"] = ws.Tables.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["range"] = t.Range,
                ["row_count"] = t.RowCount
            }).ToList(),
            ["charts"] = ws.Charts.Select(c => new Dictionary<string, object?>
            {
                ["name"] = c.Name,
                ["chart_type"] = c.ChartType
            }).ToList(),
            ["pivot_tables"] = ws.PivotTables.Select(p => new Dictionary<string, object?>
            {
                ["name"] = p.Name
            }).ToList()
        }).ToList();

        var tables = surface.Worksheets.SelectMany(ws => ws.Tables).Select(t => new Dictionary<string, object?>
        {
            ["name"] = t.Name
        }).ToList();

        var namedRanges = surface.NamedRanges.Where(nr => !nr.IsBuiltIn).Select(nr => new Dictionary<string, object?>
        {
            ["name"] = nr.Name,
            ["refers_to"] = nr.RefersTo
        }).ToList();

        // Aggregate column types
        var typeCounts = surface.AggregateColumnTypes
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new Dictionary<string, object?>
            {
                ["type"] = kv.Key.ToString().ToLowerInvariant(),
                ["count"] = kv.Value
            }).ToList();

        // Get sheet names for headline
        var sheetNames = surface.Worksheets
            .Where(ws => !ws.IsHidden)
            .Select(ws => ws.Name)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["size_bytes"] = state.Size,
            ["sheet_count"] = surface.Worksheets.Count,
            ["total_rows"] = surface.TotalRows,
            ["table_count"] = surface.TotalTables,
            ["chart_count"] = surface.TotalCharts,
            ["pivot_table_count"] = surface.TotalPivotTables,
            ["named_range_count"] = surface.NamedRanges.Count(nr => !nr.IsBuiltIn),
            ["has_formulas"] = surface.HasFormulas,
            ["has_totals"] = surface.HasTotals,
            ["sheet_names"] = sheetNames,
            ["sheets"] = sheets,
            ["tables"] = tables,
            ["named_ranges"] = namedRanges,
            ["column_type_counts"] = typeCounts
        };
    }

    private static string BuildWorksheetHeadline(WorksheetInfo ws)
    {
        var parts = new List<string>
        {
            ws.Name,
            $"{ws.RowCount} rows",
            $"{ws.ColumnCount} cols"
        };

        if (ws.HasHeaderRow)
            parts.Add("headers");
        if (ws.HasTotals)
            parts.Add("totals");
        if (ws.Tables.Count > 0)
            parts.Add($"{ws.Tables.Count} table(s)");

        // Add column names that have data
        var columnsWithData = ws.Columns
            .Where(c => c.NonEmptyCount > 0 && !string.IsNullOrWhiteSpace(c.Header))
            .Select(c => c.Header!)
            .ToList();

        if (columnsWithData.Count > 0)
        {
            parts.Add(string.Join(", ", columnsWithData));
        }

        return string.Join(" | ", parts);
    }

    private static string BuildWorksheetStructure(WorksheetInfo ws)
    {
        var lines = new List<string>();

        if (ws.HasHeaderRow && ws.Columns.Count > 0)
        {
            lines.Add("Columns:");
            foreach (var col in ws.Columns)
            {
                var header = col.Header ?? $"Column {col.Letter}";
                lines.Add($"  {col.Letter}: {header} ({col.DataType.ToString().ToLowerInvariant()})");
            }
        }

        if (ws.Tables.Count > 0)
        {
            lines.Add("Tables:");
            foreach (var table in ws.Tables)
            {
                lines.Add($"  - {table.Name}: {table.Range} ({table.RowCount} rows)");
            }
        }

        return string.Join("\n", lines);
    }

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var lp = uri.LocalPath;
                if (!string.IsNullOrEmpty(lp))
                    return Path.GetFileName(lp);
            }
        }
        catch { }

        var ap = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = ap.LastIndexOf('/') >= 0 ? ap[(ap.LastIndexOf('/') + 1)..] : ap;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    /// <summary>
    /// Converts an Excel serial date number to a formatted date string.
    /// Excel stores dates as days since 1900-01-01 (with a leap year bug for dates before 1900-03-01).
    /// </summary>
    private static string? FormatExcelDate(double? excelDate)
    {
        if (excelDate == null || excelDate <= 0)
            return null;

        try
        {
            // Excel's epoch is 1900-01-01, but there's a bug where 1900 is treated as a leap year
            // Days since 1899-12-30 (to account for the bug)
            var date = DateTime.FromOADate(excelDate.Value);
            return date.ToString("yyyy-MM-dd");
        }
        catch
        {
            return null;
        }
    }

    private static Edge CreateHasPart(Guid sourceId, Guid destId, Guid scopeDocId, int ordinal, DateTimeOffset timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = sourceId,
            DstId = destId,
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocId,
            CreatedAt = timestamp
        };

    /// <summary>
    /// Returns SQL schema scripts for XLSX macros.
    /// </summary>
    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("xlsx_macros", XlsxMacrosSql.Value);
    }

    private static readonly Lazy<string> XlsxMacrosSql = new(() =>
        ReadEmbeddedResource("RepoQL.Formats.Xlsx.Schema.xlsx_macros.sql"));

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(XlsxLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [LoggerMessage(LogLevel.Warning, "Failed to parse {Name} as XLSX")]
    partial void LogFailedToParseXlsx(Exception ex, string name);
}
