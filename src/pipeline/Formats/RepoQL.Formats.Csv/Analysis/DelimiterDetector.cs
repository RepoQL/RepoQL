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

        var sampledLines = SampleLines(text);
        if (sampledLines.Count == 0)
            return new DelimiterResult(',', 1, 0f);

        var candidates = new List<DelimiterResult>();

        foreach (var delimiter in CandidateDelimiters)
        {
            var fieldCounts = sampledLines
                .Select(line => ParseFields(line, delimiter).Count)
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
            var fallbackFieldCount = ParseFields(sampledLines[0], ',').Count;
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

    private static List<string> SampleLines(string text)
    {
        var lines = new List<string>(MaxLinesToSample);

        using var reader = new StringReader(text);
        while (lines.Count < MaxLinesToSample)
        {
            var line = reader.ReadLine();
            if (line == null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            lines.Add(line);
        }

        return lines;
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
