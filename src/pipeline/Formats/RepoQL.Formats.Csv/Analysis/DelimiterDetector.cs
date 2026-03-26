using System.Text;

namespace RepoQL.Formats.Csv.Analysis;

/// <summary>
/// Detects the most likely delimiter in a delimited text document.
///
/// Purpose: Chooses the delimiter (comma, tab, pipe, semicolon) using modal
/// field-count consistency across sampled lines.
///
/// Complexity: RFC 4180 field parsing (quoted values and escaped quotes) is
/// applied per line to avoid false delimiter matches.
/// </summary>
internal static class DelimiterDetector
{
    private const int MaxLinesToSample = 20;
    private const int MinimumFieldCount = 2;
    private const float MinimumConsistency = 0.8f;

    private static readonly char[] CandidateDelimiters = [',', '\t', '|', ';'];

    /// <summary>
    /// Detect the delimiter from raw text content.
    /// </summary>
    /// <param name="text">File content.</param>
    /// <returns>Detected delimiter details, or comma fallback.</returns>
    public static DelimiterResult Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new DelimiterResult(',', 1, 0f);

        var sampledRows = ReadLogicalRows(text, MaxLinesToSample);
        if (sampledRows.Count == 0)
            return new DelimiterResult(',', 1, 0f);

        var candidates = new List<DelimiterResult>();

        foreach (var delimiter in CandidateDelimiters)
        {
            var fieldCounts = sampledRows
                .Select(row => ParseFields(row, delimiter))
                .Select(row => row.Count)
                .ToList();

            if (fieldCounts.Count == 0)
                continue;

            var modalGroup = fieldCounts
                .GroupBy(count => count)
                .OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Key)
                .First();

            var modalFieldCount = modalGroup.Key;
            var consistency = (float)modalGroup.Count() / fieldCounts.Count;

            if (modalFieldCount >= MinimumFieldCount && consistency >= MinimumConsistency)
            {
                candidates.Add(new DelimiterResult(delimiter, modalFieldCount, consistency));
            }
        }

        if (candidates.Count == 0)
        {
            var fallbackFieldCount = ParseFields(sampledRows[0], ',').Count;
            return new DelimiterResult(',', Math.Max(1, fallbackFieldCount), 0f);
        }

        return candidates
            .OrderByDescending(candidate => candidate.Consistency)
            .ThenBy(candidate => candidate.Delimiter == ',' ? 0 : 1)
            .First();
    }

    /// <summary>
    /// Parses a delimited line using RFC 4180 quoting rules.
    /// </summary>
    /// <param name="line">A single text line.</param>
    /// <param name="delimiter">Delimiter to split on.</param>
    /// <returns>Parsed field values.</returns>
    internal static List<string> ParseFields(string line, char delimiter)
    {
        var result = new List<string>();
        var inQuotes = false;
        var current = new StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    internal static List<IReadOnlyList<string>> ReadRows(string text, char delimiter, int maxRows, out int totalRowCount)
    {
        var rows = new List<IReadOnlyList<string>>(Math.Min(maxRows, MaxLinesToSample));
        totalRowCount = 0;
        if (string.IsNullOrEmpty(text))
            return rows;

        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    currentField.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (c == delimiter && !inQuotes)
            {
                currentRow.Add(currentField.ToString());
                currentField.Clear();
                continue;
            }

            if ((c == '\r' || c == '\n') && !inQuotes)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                currentRow.Add(currentField.ToString());
                currentField.Clear();
                CompleteRow(currentRow, rows, maxRows, ref totalRowCount);
                currentRow = new List<string>();
                continue;
            }

            currentField.Append(c);
        }

        if (inQuotes)
        {
            RecoverUnterminatedQuotedRow(currentRow, currentField.ToString(), delimiter, rows, maxRows, ref totalRowCount);
            return rows;
        }

        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            CompleteRow(currentRow, rows, maxRows, ref totalRowCount);
        }

        return rows;
    }

    private static List<string> ReadLogicalRows(string text, int maxRows)
    {
        var rows = new List<string>(Math.Min(maxRows, MaxLinesToSample));
        if (string.IsNullOrEmpty(text))
            return rows;

        var currentRow = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length && rows.Count < maxRows; i++)
        {
            var c = text[i];

            if (c == '"')
            {
                currentRow.Append(c);
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    currentRow.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if ((c == '\r' || c == '\n') && !inQuotes)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                CompleteLogicalRow(currentRow.ToString(), rows, maxRows);
                currentRow.Clear();
                continue;
            }

            currentRow.Append(c);
        }

        if (currentRow.Length == 0)
            return rows;

        if (inQuotes)
        {
            foreach (var line in SplitPhysicalLines(currentRow.ToString()))
            {
                if (rows.Count >= maxRows)
                    break;
                CompleteLogicalRow(line, rows, maxRows);
            }

            return rows;
        }

        CompleteLogicalRow(currentRow.ToString(), rows, maxRows);
        return rows;
    }

    private static void CompleteRow(
        List<string> row,
        List<IReadOnlyList<string>> rows,
        int maxRows,
        ref int totalRowCount)
    {
        if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            return;

        totalRowCount++;
        if (rows.Count < maxRows)
            rows.Add(row.ToArray());
    }

    private static void CompleteLogicalRow(string row, List<string> rows, int maxRows)
    {
        if (string.IsNullOrWhiteSpace(row))
            return;

        if (rows.Count < maxRows)
            rows.Add(row);
    }

    private static void RecoverUnterminatedQuotedRow(
        List<string> currentRow,
        string unterminatedField,
        char delimiter,
        List<IReadOnlyList<string>> rows,
        int maxRows,
        ref int totalRowCount)
    {
        var physicalLines = SplitPhysicalLines(unterminatedField).ToList();
        if (physicalLines.Count == 0)
        {
            currentRow.Add(unterminatedField);
            CompleteRow(currentRow, rows, maxRows, ref totalRowCount);
            return;
        }

        currentRow.Add(physicalLines[0]);
        CompleteRow(currentRow, rows, maxRows, ref totalRowCount);

        for (var i = 1; i < physicalLines.Count; i++)
        {
            CompleteRow(ParseFields(physicalLines[i], delimiter), rows, maxRows, ref totalRowCount);
        }
    }

    private static IEnumerable<string> SplitPhysicalLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n')
                continue;

            yield return text[start..i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }

        if (start < text.Length)
            yield return text[start..];
    }
}

/// <summary>
/// Result for delimiter detection.
///
/// Purpose: Carries the selected delimiter and confidence signals to downstream
/// parsing logic.
///
/// Complexity: None - immutable value record.
/// </summary>
/// <param name="Delimiter">Detected delimiter character.</param>
/// <param name="FieldCount">Modal number of fields per sampled line.</param>
/// <param name="Consistency">Fraction of sampled lines matching the modal field count.</param>
internal sealed record DelimiterResult(char Delimiter, int FieldCount, float Consistency);
