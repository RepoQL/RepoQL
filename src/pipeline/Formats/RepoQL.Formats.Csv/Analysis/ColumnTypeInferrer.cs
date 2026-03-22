using System.Globalization;
using RepoQL.Contracts;
using RepoQL.Formats.Csv.Surface;

namespace RepoQL.Formats.Csv.Analysis;

/// <summary>
/// Infers headers, column data types, and token estimates from sampled rows.
///
/// Purpose: Converts raw delimited rows into typed column metadata suitable for
/// discovery-oriented x-ray surfaces.
///
/// Complexity: Combines heuristic header detection, dominant-type inference,
/// sample extraction, numeric range tracking, and token-cost estimation.
/// </summary>
internal static class ColumnTypeInferrer
{
    private const float DominantTypeThreshold = 0.7f;
    private const int MaxSampleValues = 5;
    private const int MaxSampleLength = 40;

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "MM/dd/yyyy"];

    /// <summary>
    /// Infer metadata from raw text lines using the provided delimiter.
    /// </summary>
    /// <param name="lines">Raw line collection.</param>
    /// <param name="delimiter">Delimiter used by the file.</param>
    /// <returns>Header and column inference result.</returns>
    public static InferenceResult Infer(IReadOnlyList<string> lines, char delimiter)
    {
        var rows = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => (IReadOnlyList<string>)DelimiterDetector.ParseFields(line, delimiter))
            .ToList();

        return Infer(rows);
    }

    /// <summary>
    /// Infer metadata from parsed rows.
    /// </summary>
    /// <param name="rows">Parsed rows (field lists by row).</param>
    /// <returns>Header and column inference result.</returns>
    public static InferenceResult Infer(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
            return new InferenceResult(false, []);

        var columnCount = rows.Max(row => row.Count);
        if (columnCount == 0)
            return new InferenceResult(false, []);

        var hasHeader = DetectHeader(rows, columnCount);
        var dataRows = hasHeader ? rows.Skip(1).ToList() : rows.ToList();
        var dataRowCount = dataRows.Count;
        var columns = new List<CsvColumnInfo>(columnCount);

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var name = ResolveColumnName(rows[0], columnIndex, hasHeader);
            columns.Add(InferColumn(columnIndex, name, dataRows, dataRowCount));
        }

        return new InferenceResult(hasHeader, columns);
    }

    private static CsvColumnInfo InferColumn(
        int columnIndex,
        string name,
        IReadOnlyList<IReadOnlyList<string>> dataRows,
        int dataRowCount)
    {
        var typeCounts = new Dictionary<CsvColumnType, int>();
        var sampleValues = new List<string>(MaxSampleValues);
        var seenSamples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double? minNumeric = null;
        double? maxNumeric = null;
        var nonEmptyCount = 0;

        foreach (var row in dataRows)
        {
            var value = GetValue(row, columnIndex);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var normalized = value.Trim();
            nonEmptyCount++;

            var valueType = InferValueType(normalized, out var numericValue);
            typeCounts.TryGetValue(valueType, out var count);
            typeCounts[valueType] = count + 1;

            if (numericValue.HasValue)
            {
                minNumeric = minNumeric.HasValue
                    ? Math.Min(minNumeric.Value, numericValue.Value)
                    : numericValue.Value;
                maxNumeric = maxNumeric.HasValue
                    ? Math.Max(maxNumeric.Value, numericValue.Value)
                    : numericValue.Value;
            }

            if (sampleValues.Count < MaxSampleValues)
            {
                var sample = Truncate(normalized);
                if (seenSamples.Add(sample))
                    sampleValues.Add(sample);
            }
        }

        var dominantType = DetermineDominantType(typeCounts, nonEmptyCount);
        var isNumericColumn = dominantType is CsvColumnType.Integer or CsvColumnType.Float;
        var estimatedTokens = EstimateTokens(sampleValues, dataRowCount);

        return new CsvColumnInfo
        {
            Index = columnIndex,
            Name = name,
            DataType = dominantType,
            SampleValues = sampleValues,
            MinValue = isNumericColumn && minNumeric.HasValue
                ? minNumeric.Value.ToString("G", CultureInfo.InvariantCulture)
                : null,
            MaxValue = isNumericColumn && maxNumeric.HasValue
                ? maxNumeric.Value.ToString("G", CultureInfo.InvariantCulture)
                : null,
            NonEmptyCount = nonEmptyCount,
            EstimatedTokens = estimatedTokens
        };
    }

    private static bool DetectHeader(IReadOnlyList<IReadOnlyList<string>> rows, int columnCount)
    {
        if (rows.Count == 0)
            return false;

        var firstRow = rows[0];
        var headerCandidateColumns = new bool[columnCount];
        var hasNamedHeaderCandidate = false;

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var value = GetValue(firstRow, columnIndex).Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (InferValueType(value, out _) != CsvColumnType.Varchar)
                return false;

            headerCandidateColumns[columnIndex] = true;
            hasNamedHeaderCandidate = true;
        }

        if (!hasNamedHeaderCandidate)
            return false;

        // Single row: if all values are non-numeric strings, treat as header-only
        if (rows.Count == 1)
            return true;

        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                if (!headerCandidateColumns[columnIndex])
                    continue;

                var value = GetValue(row, columnIndex).Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (InferValueType(value, out _) != CsvColumnType.Varchar)
                    return true;
            }
        }

        return false;
    }

    private static string ResolveColumnName(
        IReadOnlyList<string> firstRow,
        int columnIndex,
        bool hasHeader)
    {
        if (!hasHeader)
            return $"column_{columnIndex + 1}";

        var header = GetValue(firstRow, columnIndex).Trim();
        return string.IsNullOrWhiteSpace(header)
            ? $"column_{columnIndex + 1}"
            : header;
    }

    private static CsvColumnType DetermineDominantType(
        Dictionary<CsvColumnType, int> typeCounts,
        int nonEmptyCount)
    {
        if (nonEmptyCount == 0 || typeCounts.Count == 0)
            return CsvColumnType.Unknown;

        var dominant = typeCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .First();

        var ratio = (float)dominant.Value / nonEmptyCount;
        return ratio >= DominantTypeThreshold ? dominant.Key : CsvColumnType.Varchar;
    }

    private static CsvColumnType InferValueType(string value, out double? numericValue)
    {
        numericValue = null;

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            numericValue = longValue;
            return CsvColumnType.Integer;
        }

        if (double.TryParse(
                value,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var doubleValue))
        {
            numericValue = doubleValue;
            return CsvColumnType.Float;
        }

        if (TryParseBoolean(value))
            return CsvColumnType.Boolean;

        if (DateTime.TryParseExact(
                value,
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return CsvColumnType.Date;
        }

        if (LooksLikeTimestamp(value)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))
        {
            return CsvColumnType.Timestamp;
        }

        return CsvColumnType.Varchar;
    }

    private static bool TryParseBoolean(string value)
    {
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.Ordinal)
            || value.Equals("0", StringComparison.Ordinal);
    }

    private static bool LooksLikeTimestamp(string value)
    {
        return value.Contains(':', StringComparison.Ordinal)
            || value.Contains('T', StringComparison.OrdinalIgnoreCase)
            || value.Contains(" am", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" pm", StringComparison.OrdinalIgnoreCase);
    }

    private static int EstimateTokens(List<string> sampleValues, int dataRowCount)
    {
        if (sampleValues.Count == 0 || dataRowCount <= 0)
            return 0;

        var averageSampleTokens = sampleValues.Average(TokenEstimator.EstimateTokens);
        return (int)Math.Round(averageSampleTokens * dataRowCount, MidpointRounding.AwayFromZero);
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxSampleLength
            ? value
            : value[..MaxSampleLength];
    }

    private static string GetValue(IReadOnlyList<string> row, int columnIndex)
    {
        return columnIndex >= row.Count ? string.Empty : row[columnIndex];
    }
}

/// <summary>
/// Result of CSV header and column inference.
///
/// Purpose: Packages header detection and per-column inference in one transfer
/// object for loader orchestration.
///
/// Complexity: None - immutable value record.
/// </summary>
/// <param name="HasHeader">Whether the first row is treated as a header.</param>
/// <param name="Columns">Per-column analysis results.</param>
internal sealed record InferenceResult(bool HasHeader, IReadOnlyList<CsvColumnInfo> Columns);
