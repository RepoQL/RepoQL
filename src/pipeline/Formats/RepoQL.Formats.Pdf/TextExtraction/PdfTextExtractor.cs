using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace RepoQL.Formats.Pdf.TextExtraction;

/// <summary>
/// Extracts text from PDF pages using layout analysis, with content-order fallback.
///
/// Purpose: Provide robust page text extraction for reading/search while stripping
/// repeating page decorations (headers/footers/page numbers).
///
/// Complexity: Medium - two-pass extraction with per-page fault tolerance.
/// </summary>
public sealed partial class PdfTextExtractor(ILogger<PdfTextExtractor>? logger = null)
{
    private readonly ILogger<PdfTextExtractor> _logger = logger ?? NullLogger<PdfTextExtractor>.Instance;

    internal IReadOnlyList<PageExtractionResult> Extract(
        byte[] bytes,
        int pageCount,
        bool reopenPerPage,
        CancellationToken cancellationToken = default)
    {
        var captures = reopenPerPage
            ? ExtractWithReopenPerPage(bytes, pageCount, cancellationToken)
            : ExtractWithSingleOpen(bytes, cancellationToken);

        var decorationTextByPage = TryDetectDecorations(captures);
        var results = new List<PageExtractionResult>(captures.Count);

        foreach (var capture in captures.OrderBy(c => c.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filteredBlocks = FilterDecorations(
                capture.OrderedBlockTexts,
                decorationTextByPage.TryGetValue(capture.PageNumber, out var values) ? values : null);

            var pageText = filteredBlocks.Count > 0
                ? string.Join(Environment.NewLine + Environment.NewLine, filteredBlocks)
                : capture.FallbackText;

            if (string.IsNullOrWhiteSpace(pageText) && capture.OrderedBlockTexts.Count > 0)
            {
                pageText = string.Join(Environment.NewLine + Environment.NewLine, capture.OrderedBlockTexts);
            }

            pageText = NormalizeText(pageText);
            var hasText = !string.IsNullOrWhiteSpace(pageText) || (capture.LetterCount > 0 && capture.IsInvisibleTextOnly);
            var isImageOnly = capture.LetterCount == 0 && !hasText;

            results.Add(new PageExtractionResult(
                capture.PageNumber,
                pageText,
                hasText,
                isImageOnly));
        }

        return results;
    }

    private List<PageExtractionCapture> ExtractWithSingleOpen(byte[] bytes, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(bytes);
        var captures = new List<PageExtractionCapture>(document.NumberOfPages);

        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var page = document.GetPage(pageNumber);
                captures.Add(ExtractPage(pageNumber, page));
            }
            catch (Exception ex)
            {
                LogPageExtractionFailed(ex, pageNumber);
                captures.Add(new PageExtractionCapture(pageNumber, [], [], string.Empty, LetterCount: 0, IsInvisibleTextOnly: false));
            }
        }

        return captures;
    }

    private List<PageExtractionCapture> ExtractWithReopenPerPage(byte[] bytes, int pageCount, CancellationToken cancellationToken)
    {
        var captures = new List<PageExtractionCapture>(pageCount);
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = PdfDocument.Open(bytes);
                var page = document.GetPage(pageNumber);
                captures.Add(ExtractPage(pageNumber, page));
            }
            catch (Exception ex)
            {
                LogPageExtractionFailed(ex, pageNumber);
                captures.Add(new PageExtractionCapture(pageNumber, [], [], string.Empty, LetterCount: 0, IsInvisibleTextOnly: false));
            }
        }

        return captures;
    }

    private static PageExtractionCapture ExtractPage(int pageNumber, Page page)
    {
        var (orderedBlocks, orderedBlockTexts) = ExtractOrderedBlocks(page);

        string fallbackText;
        try
        {
            fallbackText = NormalizeText(ContentOrderTextExtractor.GetText(page));
        }
        catch
        {
            fallbackText = string.Empty;
        }

        var letters = page.Letters?.ToList() ?? [];
        var letterCount = letters.Count;
        var isInvisibleTextOnly = letterCount > 0 && letters.All(letter =>
            letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip);

        return new PageExtractionCapture(
            pageNumber,
            orderedBlocks,
            orderedBlockTexts,
            fallbackText,
            letterCount,
            isInvisibleTextOnly);
    }

    private static (IReadOnlyList<TextBlock> Blocks, IReadOnlyList<string> Texts) ExtractOrderedBlocks(Page page)
    {
        try
        {
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words).ToList();
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList();

            var blockTexts = ordered
                .Select(block => NormalizeText(block.Text))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return (ordered, blockTexts);
        }
        catch
        {
            return ([], []);
        }
    }

    private static IReadOnlyDictionary<int, HashSet<string>> TryDetectDecorations(IReadOnlyList<PageExtractionCapture> captures)
    {
        try
        {
            var decoratedBlocks = DecorationTextBlockClassifier.Get(
                captures
                    .OrderBy(capture => capture.PageNumber)
                    .Select(capture => capture.OrderedBlocks)
                    .ToList(),
                0.2,
                2,
                5);

            var map = new Dictionary<int, HashSet<string>>();
            for (var pageIndex = 0; pageIndex < decoratedBlocks.Count; pageIndex++)
            {
                map[pageIndex + 1] = decoratedBlocks[pageIndex]
                    .Select(block => NormalizeForComparison(block.Text))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToHashSet(StringComparer.Ordinal);
            }

            return map;
        }
        catch
        {
            return new Dictionary<int, HashSet<string>>();
        }
    }

    private static List<string> FilterDecorations(IReadOnlyList<string> orderedBlocks, HashSet<string>? decorations)
    {
        if (decorations is null || decorations.Count == 0)
            return [.. orderedBlocks];

        return orderedBlocks
            .Where(text => !decorations.Contains(NormalizeForComparison(text)))
            .ToList();
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return text.Replace("\r\n", "\n").Trim();
    }

    private static string NormalizeForComparison(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return string.Join(" ", text.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToUpperInvariant();
    }

    [LoggerMessage(LogLevel.Warning, "PDF page {PageNumber} text extraction failed; page was skipped.")]
    partial void LogPageExtractionFailed(Exception ex, int pageNumber);
}

internal sealed record PageExtractionResult(
    int PageNumber,
    string Text,
    bool HasText,
    bool IsImageOnly);

internal sealed record PageExtractionCapture(
    int PageNumber,
    IReadOnlyList<TextBlock> OrderedBlocks,
    IReadOnlyList<string> OrderedBlockTexts,
    string FallbackText,
    int LetterCount,
    bool IsInvisibleTextOnly);
