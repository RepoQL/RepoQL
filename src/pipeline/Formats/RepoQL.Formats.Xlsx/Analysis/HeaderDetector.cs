using DocumentFormat.OpenXml.Spreadsheet;
using RepoQL.Formats.Xlsx.Surface;

namespace RepoQL.Formats.Xlsx.Analysis;

/// <summary>
/// Detects header rows in Excel worksheets using multi-signal scoring.
///
/// Purpose: Automatically identifies the header row in spreadsheets that
/// may not have explicit table formatting. Critical for understanding
/// column semantics in messy small business spreadsheets.
///
/// Complexity: Uses a weighted scoring algorithm that considers:
/// - Cell data types (text vs numeric)
/// - Styling (bold, background color)
/// - Value characteristics (length, uniqueness)
/// - Type transitions between adjacent rows
/// The algorithm tolerates partial headers and merged cells.
/// </summary>
internal static class HeaderDetector
{
    /// <summary>
    /// Minimum confidence score to consider a row as a header.
    /// </summary>
    private const float MinConfidenceThreshold = 0.4f;

    /// <summary>
    /// Maximum number of rows to scan when looking for headers.
    /// </summary>
    private const int MaxRowsToScan = 15;

    /// <summary>
    /// Result of header detection for a worksheet.
    /// </summary>
    public sealed record HeaderDetectionResult
    {
        /// <summary>
        /// Whether a header row was detected with sufficient confidence.
        /// </summary>
        public bool HasHeader { get; init; }

        /// <summary>
        /// 1-based row index of the detected header, or null if no header found.
        /// </summary>
        public int? HeaderRowIndex { get; init; }

        /// <summary>
        /// Confidence score (0.0 to 1.0) for the detected header.
        /// </summary>
        public float Confidence { get; init; }

        /// <summary>
        /// Detected column headers with their positions.
        /// </summary>
        public IReadOnlyList<DetectedColumn> Columns { get; init; } = [];
    }

    /// <summary>
    /// A detected column header.
    /// </summary>
    public sealed record DetectedColumn
    {
        /// <summary>
        /// Column letter (A, B, C, etc.)
        /// </summary>
        public required string Letter { get; init; }

        /// <summary>
        /// Zero-based column index.
        /// </summary>
        public required int Index { get; init; }

        /// <summary>
        /// Header text, or null if cell was empty.
        /// </summary>
        public string? HeaderText { get; init; }

        /// <summary>
        /// Whether this column is part of a merged cell.
        /// </summary>
        public bool IsMerged { get; init; }

        /// <summary>
        /// If merged, the column letter of the master cell.
        /// </summary>
        public string? MergedWithColumn { get; init; }
    }

    /// <summary>
    /// Detects the header row in a worksheet.
    /// </summary>
    /// <param name="sheetData">The worksheet's SheetData element.</param>
    /// <param name="mergeCells">Optional merge cell information.</param>
    /// <param name="stylesheet">Optional stylesheet for formatting analysis.</param>
    /// <param name="sharedStrings">Shared strings table for text lookup.</param>
    /// <returns>Header detection result.</returns>
    public static HeaderDetectionResult DetectHeaderRow(
        SheetData? sheetData,
        MergeCells? mergeCells,
        Stylesheet? stylesheet,
        SharedStringTable? sharedStrings)
    {
        if (sheetData == null)
            return new HeaderDetectionResult { HasHeader = false };

        var rows = sheetData.Elements<Row>().Take(MaxRowsToScan).ToList();
        if (rows.Count == 0)
            return new HeaderDetectionResult { HasHeader = false };

        var candidates = new List<(int rowIndex, float score, Row row)>();

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowIndex = (int)(row.RowIndex?.Value ?? (uint)(i + 1));
            var cells = row.Elements<Cell>().ToList();

            if (cells.Count == 0)
                continue;

            var score = CalculateHeaderScore(
                cells,
                i + 1 < rows.Count ? rows[i + 1] : null,
                stylesheet,
                sharedStrings);

            if (score > 0)
            {
                candidates.Add((rowIndex, score, row));
            }
        }

        if (candidates.Count == 0)
            return new HeaderDetectionResult { HasHeader = false };

        var best = candidates.OrderByDescending(c => c.score).First();

        if (best.score < MinConfidenceThreshold)
            return new HeaderDetectionResult { HasHeader = false };

        var columns = ExtractColumns(best.row, mergeCells, sharedStrings);

        return new HeaderDetectionResult
        {
            HasHeader = true,
            HeaderRowIndex = best.rowIndex,
            Confidence = Math.Min(1.0f, best.score),
            Columns = columns
        };
    }

    private static float CalculateHeaderScore(
        List<Cell> cells,
        Row? nextRow,
        Stylesheet? stylesheet,
        SharedStringTable? sharedStrings)
    {
        if (cells.Count == 0)
            return 0;

        float totalScore = 0;
        int textCells = 0;
        int styledCells = 0;
        int shortValueCells = 0;
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int nonEmpty = 0;

        foreach (var cell in cells)
        {
            var value = GetCellStringValue(cell, sharedStrings);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            nonEmpty++;
            values.Add(value);

            // Signal 1: Text values (headers are usually text)
            if (IsTextCell(cell, sharedStrings))
            {
                textCells++;
            }

            // Signal 2: Short values (< 50 chars)
            if (value.Length < 50)
            {
                shortValueCells++;
            }

            // Signal 3: Bold or background styling
            if (HasHeaderStyling(cell, stylesheet))
            {
                styledCells++;
            }
        }

        if (nonEmpty == 0)
            return 0;

        // Calculate component scores

        // Text ratio: Higher is better for headers
        float textRatio = (float)textCells / nonEmpty;
        totalScore += textRatio * 0.3f;

        // Short value ratio
        float shortRatio = (float)shortValueCells / nonEmpty;
        totalScore += shortRatio * 0.15f;

        // Styling ratio
        float styleRatio = (float)styledCells / nonEmpty;
        totalScore += styleRatio * 0.2f;

        // Uniqueness: All distinct values suggest headers
        float uniqueRatio = (float)values.Count / nonEmpty;
        if (uniqueRatio > 0.9f)
            totalScore += 0.2f;

        // Type transition to next row (data row should have more numbers/dates)
        if (nextRow != null)
        {
            var nextCells = nextRow.Elements<Cell>().ToList();
            if (nextCells.Count > 0 && HasTypeTransition(cells, nextCells, sharedStrings))
            {
                totalScore += 0.25f;
            }
        }

        // Bonus: High text ratio with good coverage
        if (textRatio > 0.7f && nonEmpty >= 3)
        {
            totalScore += 0.1f;
        }

        return totalScore;
    }

    private static bool IsTextCell(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
            return true;
        if (cell.DataType?.Value == CellValues.String)
            return true;
        if (cell.DataType?.Value == CellValues.InlineString)
            return true;

        // If no data type, check if value looks like text
        var value = cell.CellValue?.Text;
        if (string.IsNullOrEmpty(value))
            return false;

        // If it's not parseable as a number, it's text
        return !double.TryParse(value, out _);
    }

    private static bool HasHeaderStyling(Cell cell, Stylesheet? stylesheet)
    {
        if (stylesheet == null || cell.StyleIndex == null)
            return false;

        try
        {
            var styleIndex = (int)cell.StyleIndex.Value;
            var cellFormats = stylesheet.CellFormats;
            if (cellFormats == null || styleIndex >= cellFormats.Count)
                return false;

            var format = cellFormats.Elements<CellFormat>().ElementAtOrDefault(styleIndex);
            if (format == null)
                return false;

            // Check for bold font
            if (format.FontId != null)
            {
                var fontId = (int)format.FontId.Value;
                var font = stylesheet.Fonts?.Elements<Font>().ElementAtOrDefault(fontId);
                if (font?.Bold != null)
                    return true;
            }

            // Check for fill (background color)
            if (format.FillId != null && format.FillId.Value > 0)
            {
                return true;
            }
        }
        catch
        {
            // Ignore styling errors
        }

        return false;
    }

    private static bool HasTypeTransition(
        List<Cell> headerRow,
        List<Cell> dataRow,
        SharedStringTable? sharedStrings)
    {
        int headerTextCount = headerRow.Count(c => IsTextCell(c, sharedStrings));
        int dataTextCount = dataRow.Count(c => IsTextCell(c, sharedStrings));

        if (headerRow.Count == 0 || dataRow.Count == 0)
            return false;

        float headerTextRatio = (float)headerTextCount / headerRow.Count;
        float dataTextRatio = (float)dataTextCount / dataRow.Count;

        // Header should have more text than data row
        return headerTextRatio > dataTextRatio + 0.2f;
    }

    private static IReadOnlyList<DetectedColumn> ExtractColumns(
        Row row,
        MergeCells? mergeCells,
        SharedStringTable? sharedStrings)
    {
        var columns = new List<DetectedColumn>();
        var mergeMap = BuildMergeMap(mergeCells);

        foreach (var cell in row.Elements<Cell>())
        {
            var reference = cell.CellReference?.Value;
            if (string.IsNullOrEmpty(reference))
                continue;

            var (letter, index) = ParseCellReference(reference);
            var value = GetCellStringValue(cell, sharedStrings);

            // Check if cell is merged
            string? mergedWith = null;
            bool isMerged = false;
            if (mergeMap.TryGetValue(reference, out var masterRef) && masterRef != reference)
            {
                isMerged = true;
                mergedWith = ParseCellReference(masterRef).letter;
            }

            columns.Add(new DetectedColumn
            {
                Letter = letter,
                Index = index,
                HeaderText = string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
                IsMerged = isMerged,
                MergedWithColumn = mergedWith
            });
        }

        return columns.OrderBy(c => c.Index).ToList();
    }

    private static Dictionary<string, string> BuildMergeMap(MergeCells? mergeCells)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (mergeCells == null)
            return map;

        foreach (var merge in mergeCells.Elements<MergeCell>())
        {
            var range = merge.Reference?.Value;
            if (string.IsNullOrEmpty(range))
                continue;

            var parts = range.Split(':');
            if (parts.Length != 2)
                continue;

            var masterRef = parts[0];
            // Expand the range and map all cells to the master
            foreach (var cellRef in ExpandRange(range))
            {
                map[cellRef] = masterRef;
            }
        }

        return map;
    }

    private static IEnumerable<string> ExpandRange(string range)
    {
        var parts = range.Split(':');
        if (parts.Length != 2)
        {
            yield return range;
            yield break;
        }

        var (startLetter, startRow) = ParseCellReferenceWithRow(parts[0]);
        var (endLetter, endRow) = ParseCellReferenceWithRow(parts[1]);

        var startCol = ColumnLetterToIndex(startLetter);
        var endCol = ColumnLetterToIndex(endLetter);

        for (int row = startRow; row <= endRow; row++)
        {
            for (int col = startCol; col <= endCol; col++)
            {
                yield return $"{IndexToColumnLetter(col)}{row}";
            }
        }
    }

    private static (string letter, int row) ParseCellReferenceWithRow(string reference)
    {
        int i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
            i++;

        var letter = reference[..i];
        var row = int.Parse(reference[i..]);
        return (letter, row);
    }

    private static (string letter, int index) ParseCellReference(string reference)
    {
        int i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
            i++;

        var letter = reference[..i];
        var index = ColumnLetterToIndex(letter);
        return (letter, index);
    }

    internal static int ColumnLetterToIndex(string letter)
    {
        int index = 0;
        foreach (char c in letter.ToUpperInvariant())
        {
            index = index * 26 + (c - 'A' + 1);
        }
        return index - 1; // Zero-based
    }

    internal static string IndexToColumnLetter(int index)
    {
        string result = "";
        index++; // Convert to 1-based
        while (index > 0)
        {
            index--;
            result = (char)('A' + index % 26) + result;
            index /= 26;
        }
        return result;
    }

    internal static string? GetCellStringValue(Cell cell, SharedStringTable? sharedStrings)
    {
        var value = cell.CellValue?.Text;
        if (string.IsNullOrEmpty(value))
        {
            // Check for inline string
            if (cell.InlineString?.Text != null)
                return cell.InlineString.Text.Text;
            return null;
        }

        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings != null)
        {
            if (int.TryParse(value, out var index))
            {
                var item = sharedStrings.Elements<SharedStringItem>().ElementAtOrDefault(index);
                return item?.Text?.Text ?? item?.InnerText;
            }
        }

        return value;
    }
}
