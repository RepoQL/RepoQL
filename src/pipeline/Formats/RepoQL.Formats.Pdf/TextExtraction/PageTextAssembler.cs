using System.Text;
using RepoQL.Contracts;

namespace RepoQL.Formats.Pdf.TextExtraction;

/// <summary>
/// Assembles extracted per-page text into one artifact text and computes
/// per-page byte offsets and token counts.
///
/// Purpose: Enables #page fragment addressing without storing separate artifacts.
///
/// Complexity: Low - deterministic concatenation and offset math.
/// </summary>
internal static class PageTextAssembler
{
    private const string PageSeparator = "\n\n";

    public static PageAssemblyResult Assemble(IReadOnlyList<string> pageTexts)
    {
        var normalized = pageTexts.Select(text => text ?? string.Empty).ToList();
        var offsets = new List<(long Start, long End)>(normalized.Count);
        var tokenCounts = new List<int>(normalized.Count);

        var byteOffset = 0L;
        var builder = new StringBuilder();
        for (var index = 0; index < normalized.Count; index++)
        {
            var pageText = normalized[index];
            var start = byteOffset;
            builder.Append(pageText);
            byteOffset += Encoding.UTF8.GetByteCount(pageText);
            var end = byteOffset;

            offsets.Add((start, end));
            tokenCounts.Add(TokenEstimator.EstimateTokensSafe(pageText) ?? 0);

            if (index < normalized.Count - 1)
            {
                builder.Append(PageSeparator);
                byteOffset += Encoding.UTF8.GetByteCount(PageSeparator);
            }
        }

        return new PageAssemblyResult(builder.ToString(), offsets, tokenCounts);
    }
}

internal sealed record PageAssemblyResult(
    string Text,
    IReadOnlyList<(long Start, long End)> PageByteOffsets,
    IReadOnlyList<int> PageTokenCounts);
