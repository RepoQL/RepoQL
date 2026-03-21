using System.Globalization;
using System.Text;
using RepoQL.Contracts;

using RepoQL.Explore;

namespace RepoQL.Read;

/// <summary>
/// Purpose: Renders full document content with line numbers for the content modifier.
/// Complexity: Centralizes output assembly, binary detection, and token accounting so dispatch stays simple.
/// </summary>
public sealed class ContentHandler : IModifierHandler
{
    public string ModifierName => "content";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

    public Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var content = BuildContent(documents, out var totalLines, ct);
        var tokenCount = TokenEstimator.EstimateTokens(content);
        var exceedsBudget = tokenCount > tokenBudget;

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["file_count"] = documents.Count,
            ["total_lines"] = totalLines
        };

        var metadata = new ResultMetadata(
            FilesConsulted: documents.Select(doc => doc.Uri).ToList(),
            Warning: null,
            Extra: extra);

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: documents.Count,
            Shown: documents.Count,
            ExceedsBudget: exceedsBudget,
            Metadata: metadata));
    }

    private static string BuildContent(
        IReadOnlyList<ReadDocument> documents,
        out int totalLines,
        CancellationToken ct)
    {
        totalLines = 0;

        if (documents.Count == 0)
            return "No files matched.";

        var builder = new StringBuilder();

        for (var i = 0; i < documents.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var doc = documents[i];

            if (builder.Length > 0)
                builder.AppendLine();

            builder.AppendLine($"--- {doc.Uri} ---");

            if (IsBinary(doc.MediaType))
            {
                var sizeSuffix = TryGetByteSize(doc, out var bytes)
                    ? $", {bytes.ToString(CultureInfo.InvariantCulture)} bytes"
                    : string.Empty;
                builder.AppendLine($"{doc.Uri} (binary file{sizeSuffix})");
                continue;
            }

            if (doc.TextContent is null)
            {
                builder.AppendLine($"{doc.Uri} (no content available)");
                continue;
            }

            AppendLineNumbers(builder, doc.TextContent, ref totalLines);
        }

        return builder.ToString();
    }

    private static void AppendLineNumbers(StringBuilder builder, string text, ref int totalLines)
    {
        var lines = text.Split('\n', StringSplitOptions.None);
        var width = Math.Max(2, lines.Length.ToString(CultureInfo.InvariantCulture).Length);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var lineNumber = lineIndex + 1;
            var line = lines[lineIndex];
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            builder.Append(lineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(width));
            builder.Append(": ");
            builder.AppendLine(line);
        }

        totalLines += lines.Length;
    }

    private static bool IsBinary(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        var normalized = mediaType.Trim().ToUpperInvariant();

        if (normalized.StartsWith("TEXT/", StringComparison.Ordinal))
            return false;

        if (normalized.StartsWith("APPLICATION/", StringComparison.Ordinal))
        {
            if (normalized.StartsWith("APPLICATION/JSON", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/XML", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/JAVASCRIPT", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/TYPESCRIPT", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/GRAPHQL", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/TOML", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/X-SH", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/X-PYTHON", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/X-RUBY", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/X-PERL", StringComparison.Ordinal)
                || normalized.StartsWith("APPLICATION/X-PHP", StringComparison.Ordinal))
            {
                return false;
            }

            if (normalized.Contains("+JSON", StringComparison.Ordinal)
                || normalized.Contains("+XML", StringComparison.Ordinal)
                || normalized.Contains("YAML", StringComparison.Ordinal)
                || normalized.Contains("TOML", StringComparison.Ordinal)
                || normalized.Contains("SQL", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetByteSize(ReadDocument doc, out long bytes)
    {
        if (TryParseSize(doc.Headline, out bytes))
            return true;

        if (TryParseSize(doc.Summary, out bytes))
            return true;

        bytes = 0;
        return false;
    }

    private static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var matches = SizeRegex.Matches(text);
        if (matches.Count == 0)
            return false;

        var match = matches[^1];
        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        var multiplier = unit switch
        {
            "B" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            _ => 1d
        };

        bytes = (long)Math.Round(value * multiplier, MidpointRounding.AwayFromZero);
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex SizeRegex =
        new("(?<value>\\d+(?:\\.\\d+)?)\\s*(?<unit>B|KB|MB|GB)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
}
