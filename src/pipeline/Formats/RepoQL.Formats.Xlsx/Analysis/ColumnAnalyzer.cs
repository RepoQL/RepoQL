using System.Globalization;
using DocumentFormat.OpenXml.Spreadsheet;
using RepoQL.Formats.Xlsx.Surface;

namespace RepoQL.Formats.Xlsx.Analysis;

/// <summary>
/// Analyzes data types in Excel worksheet columns.
///
/// Purpose: Determines the dominant data type for each column to enable
/// type-aware querying and discovery. Critical for understanding financial
/// data in small business spreadsheets.
///
/// Complexity: Type detection considers:
/// - Cell value type (shared string, number, boolean)
/// - Number format codes (currency, percentage, date)
/// - Actual cell values for ambiguous cases
/// Samples up to 1000 rows for performance.
/// </summary>
internal static class ColumnAnalyzer
{
    private const int MaxRowsToSample = 1000;
    private const int MaxSampleValues = 5;
    private const int MaxSampleValueLength = 40;
    private const float HomogeneityThreshold = 0.7f;

    public sealed record ColumnAnalysisResult
    {
        public ColumnDataType DominantType { get; init; } = ColumnDataType.Unknown;
        public IReadOnlyDictionary<ColumnDataType, int> TypeCounts { get; init; } = new Dictionary<ColumnDataType, int>();
        public float Homogeneity { get; init; }
        public bool HasFormulas { get; init; }
        public int FormulaCount { get; init; }
        public string? SampleValue { get; init; }
        public IReadOnlyList<string> SampleValues { get; init; } = [];
        public bool SampleIsFormula { get; init; }
        public double? MinValue { get; init; }
        public double? MaxValue { get; init; }
        public double? MinDate { get; init; }
        public double? MaxDate { get; init; }
        public int NonEmptyCount { get; init; }
    }

    public static ColumnAnalysisResult AnalyzeColumn(
        SheetData sheetData,
        string columnLetter,
        int startRow,
        Stylesheet? stylesheet,
        SharedStringTable? sharedStrings)
    {
        var typeCounts = new Dictionary<ColumnDataType, int>();
        int formulaCount = 0;
        string? sampleValue = null;
        bool sampleIsFormula = false;
        var sampleValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double? minValue = null, maxValue = null;
        double? minDate = null, maxDate = null;
        int nonEmpty = 0;

        var rows = sheetData.Elements<Row>()
            .Where(r => r.RowIndex?.Value >= (uint)startRow)
            .Take(MaxRowsToSample)
            .ToList();

        foreach (var row in rows)
        {
            var cell = row.Elements<Cell>()
                .FirstOrDefault(c => GetColumnLetter(c.CellReference?.Value) == columnLetter);

            if (cell == null)
                continue;

            bool isFormula = cell.CellFormula != null;
            if (isFormula)
                formulaCount++;

            if (IsEmptyCell(cell))
                continue;

            nonEmpty++;

            var cellType = ClassifyCellType(cell, stylesheet, sharedStrings);
            typeCounts.TryGetValue(cellType, out var count);
            typeCounts[cellType] = count + 1;

            // Get display value for samples
            var displayValue = GetDisplayValue(cell, sharedStrings);

            if (sampleValue == null && displayValue != null)
            {
                sampleValue = TruncateValue(displayValue);
                sampleIsFormula = isFormula;
            }

            // Collect unique sample values (for text columns)
            if (displayValue != null && sampleValues.Count < MaxSampleValues)
            {
                var truncated = TruncateValue(displayValue);
                if (!string.IsNullOrWhiteSpace(truncated))
                    sampleValues.Add(truncated);
            }

            // Track numeric min/max
            if ((cellType == ColumnDataType.Numeric || cellType == ColumnDataType.Currency ||
                 cellType == ColumnDataType.Percentage) &&
                TryGetNumericValue(cell, out var numVal))
            {
                minValue = minValue == null ? numVal : Math.Min(minValue.Value, numVal);
                maxValue = maxValue == null ? numVal : Math.Max(maxValue.Value, numVal);
            }

            // Track date min/max (Excel stores dates as numbers)
            if ((cellType == ColumnDataType.Date || cellType == ColumnDataType.DateTime) &&
                TryGetNumericValue(cell, out var dateVal))
            {
                minDate = minDate == null ? dateVal : Math.Min(minDate.Value, dateVal);
                maxDate = maxDate == null ? dateVal : Math.Max(maxDate.Value, dateVal);
            }
        }

        if (nonEmpty == 0)
        {
            return new ColumnAnalysisResult
            {
                DominantType = ColumnDataType.Unknown,
                HasFormulas = formulaCount > 0,
                FormulaCount = formulaCount
            };
        }

        var dominant = typeCounts.OrderByDescending(kv => kv.Value).First();
        float homogeneity = (float)dominant.Value / nonEmpty;

        var dominantType = homogeneity >= HomogeneityThreshold
            ? dominant.Key
            : ColumnDataType.Mixed;

        return new ColumnAnalysisResult
        {
            DominantType = dominantType,
            TypeCounts = typeCounts,
            Homogeneity = homogeneity,
            HasFormulas = formulaCount > 0,
            FormulaCount = formulaCount,
            SampleValue = sampleValue,
            SampleValues = sampleValues.ToList(),
            SampleIsFormula = sampleIsFormula,
            MinValue = minValue,
            MaxValue = maxValue,
            MinDate = minDate,
            MaxDate = maxDate,
            NonEmptyCount = nonEmpty
        };
    }

    public static IReadOnlyList<ColumnInfo> AnalyzeAllColumns(
        SheetData sheetData,
        IReadOnlyList<HeaderDetector.DetectedColumn> detectedColumns,
        int headerRowIndex,
        Stylesheet? stylesheet,
        SharedStringTable? sharedStrings)
    {
        var results = new List<ColumnInfo>();
        int dataStartRow = headerRowIndex + 1;

        foreach (var col in detectedColumns)
        {
            var analysis = AnalyzeColumn(
                sheetData,
                col.Letter,
                dataStartRow,
                stylesheet,
                sharedStrings);

            results.Add(new ColumnInfo
            {
                Letter = col.Letter,
                Index = col.Index,
                Header = col.HeaderText,
                IsMergedHeader = col.IsMerged,
                MergedWithColumn = col.MergedWithColumn,
                DataType = analysis.DominantType,
                TypeCounts = analysis.TypeCounts,
                Homogeneity = analysis.Homogeneity,
                HasFormulas = analysis.HasFormulas,
                FormulaCount = analysis.FormulaCount,
                SampleValue = analysis.SampleValue,
                SampleValues = analysis.SampleValues,
                SampleIsFormula = analysis.SampleIsFormula,
                MinValue = analysis.MinValue,
                MaxValue = analysis.MaxValue,
                MinDate = analysis.MinDate,
                MaxDate = analysis.MaxDate,
                NonEmptyCount = analysis.NonEmptyCount
            });
        }

        return results;
    }

    private static string TruncateValue(string value)
    {
        if (value.Length <= MaxSampleValueLength)
            return value;
        return value[..(MaxSampleValueLength - 3)] + "...";
    }

    private static bool TryGetNumericValue(Cell cell, out double value)
    {
        value = 0;
        var text = cell.CellValue?.Text;
        if (string.IsNullOrEmpty(text))
            return false;
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static ColumnDataType ClassifyCellType(
        Cell cell,
        Stylesheet? stylesheet,
        SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value != null)
        {
            var dataType = cell.DataType.Value;
            if (dataType == CellValues.SharedString ||
                dataType == CellValues.String ||
                dataType == CellValues.InlineString)
            {
                return ColumnDataType.Text;
            }

            if (dataType == CellValues.Boolean)
                return ColumnDataType.Boolean;

            if (dataType == CellValues.Error)
                return ColumnDataType.Unknown;
        }

        if (cell.StyleIndex != null && stylesheet != null)
        {
            var formatType = GetFormatType(cell.StyleIndex.Value, stylesheet);
            if (formatType != ColumnDataType.Unknown)
                return formatType;
        }

        var value = cell.CellValue?.Text;
        if (!string.IsNullOrEmpty(value) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return ColumnDataType.Numeric;

        return ColumnDataType.Text;
    }

    private static ColumnDataType GetFormatType(uint styleIndex, Stylesheet stylesheet)
    {
        try
        {
            var cellFormats = stylesheet.CellFormats;
            if (cellFormats == null || (int)styleIndex >= cellFormats.Count)
                return ColumnDataType.Unknown;

            var format = cellFormats.Elements<CellFormat>().ElementAtOrDefault((int)styleIndex);
            if (format?.NumberFormatId == null)
                return ColumnDataType.Unknown;

            var formatId = (int)format.NumberFormatId.Value;

            switch (formatId)
            {
                case >= 14 and <= 22: return ColumnDataType.Date;
                case >= 45 and <= 47: return ColumnDataType.DateTime;
                case >= 5 and <= 8:
                case >= 37 and <= 40: return ColumnDataType.Currency;
                case 9:
                case 10: return ColumnDataType.Percentage;
                case 0:
                case 1:
                case 2:
                case 3:
                case 4: return ColumnDataType.Numeric;
            }

            if (format.NumberFormatId.Value >= 164)
            {
                var customFormat = stylesheet.NumberingFormats?
                    .Elements<NumberingFormat>()
                    .FirstOrDefault(nf => nf.NumberFormatId?.Value == format.NumberFormatId.Value);

                if (customFormat?.FormatCode != null)
                {
                    var code = customFormat.FormatCode.Value?.ToLowerInvariant() ?? "";

                    if (code.Contains('$') || code.Contains("usd") || code.Contains("eur") ||
                        code.Contains("gbp") || code.Contains("currency"))
                        return ColumnDataType.Currency;

                    if (code.Contains('%'))
                        return ColumnDataType.Percentage;

                    if (code.Contains('d') || code.Contains('m') || code.Contains('y') ||
                        code.Contains("date"))
                        return ColumnDataType.Date;

                    if (code.Contains('h') || code.Contains('s') || code.Contains("am") ||
                        code.Contains("pm"))
                        return ColumnDataType.DateTime;
                }
            }
        }
        catch
        {
            // Ignore format parsing errors
        }

        return ColumnDataType.Unknown;
    }

    private static bool IsEmptyCell(Cell cell)
    {
        if (cell.CellValue == null && cell.InlineString == null)
            return true;

        var value = cell.CellValue?.Text;
        return string.IsNullOrWhiteSpace(value) && cell.InlineString?.Text?.Text == null;
    }

    private static string? GetDisplayValue(Cell cell, SharedStringTable? sharedStrings)
    {
        return HeaderDetector.GetCellStringValue(cell, sharedStrings);
    }

    private static string? GetColumnLetter(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
            return null;

        int i = 0;
        while (i < cellReference.Length && char.IsLetter(cellReference[i]))
            i++;

        return cellReference[..i];
    }

    public static bool HasAggregateFormulas(SheetData sheetData)
    {
        var aggregatePatterns = new[] { "SUM(", "AVERAGE(", "COUNT(", "SUBTOTAL(", "TOTAL" };

        foreach (var row in sheetData.Elements<Row>())
        {
            foreach (var cell in row.Elements<Cell>())
            {
                var formula = cell.CellFormula?.Text;
                if (string.IsNullOrEmpty(formula))
                    continue;

                var upper = formula.ToUpperInvariant();
                if (aggregatePatterns.Any(p => upper.Contains(p)))
                    return true;
            }
        }

        return false;
    }

    public static int CountFormulas(SheetData sheetData)
    {
        return sheetData.Elements<Row>()
            .SelectMany(r => r.Elements<Cell>())
            .Count(c => c.CellFormula != null);
    }
}
