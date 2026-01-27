using System.Text;
using RepoQL.Contracts;

namespace RepoQL.Explore;

/// <summary>
/// Purpose: Supplies lint diagnostics for the read modifier by querying annotations.
/// Complexity: Coordinates severity filtering, ordering, budget fitting, and formatting so dispatch stays lightweight.
/// </summary>
public sealed class LintHandler : IModifierHandler
{
    private readonly ILintAnnotationProvider _annotationProvider;

    public LintHandler(ILintAnnotationProvider annotationProvider)
    {
        _annotationProvider = annotationProvider ?? throw new ArgumentNullException(nameof(annotationProvider));
    }

    public string ModifierName => "lint";

    public bool CanHandle(string? modifier)
    {
        if (string.IsNullOrWhiteSpace(modifier))
            return false;

        return modifier.Trim().StartsWith(ModifierName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Validate parameter early so invalid parameters always throw
        var filter = ParseFilter(parameter);

        var filesConsulted = documents
            .Select(doc => GetContainerUri(doc.Uri))
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fileTextByUri = BuildFileTextLookup(documents);

        if (filesConsulted.Count == 0)
        {
            return BuildMessageResult(
                "No files matched.",
                filesConsulted,
                tokenBudget,
                totalAvailable: 0,
                shown: 0,
                totalErrors: 0,
                totalWarnings: 0,
                filter: filter);
        }

        var annotations = await _annotationProvider
            .GetLintAnnotationsAsync(filesConsulted, ct)
            .ConfigureAwait(false);

        if (annotations.Count == 0)
        {
            return BuildMessageResult(
                "No diagnostics in scope.",
                filesConsulted,
                tokenBudget,
                totalAvailable: 0,
                shown: 0,
                totalErrors: 0,
                totalWarnings: 0,
                filter: filter);
        }
        var totals = CountBySeverity(annotations);

        var filtered = annotations
            .Where(annotation => ShouldInclude(annotation, filter))
            .OrderByDescending(annotation => annotation.SeverityRank)
            .ThenBy(annotation => annotation.FileUri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(annotation => annotation.LineStart ?? int.MaxValue)
            .ThenBy(annotation => annotation.ResolvedTargetUri, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (filtered.Count == 0)
        {
            return BuildMessageResult(
                "No diagnostics in scope.",
                filesConsulted,
                tokenBudget,
                totalAvailable: 0,
                shown: 0,
                totalErrors: totals.Errors,
                totalWarnings: totals.Warnings,
                filter: filter,
                summary: BuildSummary(totals.Errors, totals.Warnings, 0, filter));
        }

        var builder = new StringBuilder();
        var shown = new List<LintAnnotation>(filtered.Count);
        var lengths = new List<int>(filtered.Count);
        var currentFile = string.Empty;

        foreach (var annotation in filtered)
        {
            ct.ThrowIfCancellationRequested();
            var fileUri = ResolveFileUri(annotation);
            var isNewFile = !string.Equals(fileUri, currentFile, StringComparison.OrdinalIgnoreCase);
            var snippet = TryBuildSnippet(fileTextByUri, fileUri, annotation.LineStart);

            var blockWithSnippet = FormatDiagnostic(annotation, fileUri, includeFileHeader: isNewFile, snippet);
            if (!TryAppend(builder, blockWithSnippet, isNewFile, tokenBudget))
            {
                var blockNoSnippet = FormatDiagnostic(annotation, fileUri, includeFileHeader: isNewFile, snippet: null);
                if (!TryAppend(builder, blockNoSnippet, isNewFile, tokenBudget))
                    break;
            }

            shown.Add(annotation);
            lengths.Add(builder.Length);
            currentFile = fileUri;
        }

        var shownCounts = CountBySeverity(shown);
        var omitted = filtered.Count - shown.Count;

        if (shown.Count == 0)
        {
            var noFit = "No diagnostics fit within token budget.";
            return BuildMessageResult(
                noFit,
                filesConsulted,
                tokenBudget,
                totalAvailable: filtered.Count,
                shown: 0,
                totalErrors: totals.Errors,
                totalWarnings: totals.Warnings,
                filter: filter,
                omitted: omitted,
                summary: BuildSummary(totals.Errors, totals.Warnings, omitted, filter));
        }

        var summary = BuildSummary(totals.Errors, totals.Warnings, omitted, filter);
        if (shown.Count > 0 && !string.IsNullOrWhiteSpace(summary))
        {
            while (true)
            {
                var contentLength = builder.Length;
                if (contentLength > 0)
                    builder.AppendLine().AppendLine();
                builder.Append(summary);

                var tokens = TokenEstimator.EstimateTokens(builder.ToString());
                if (tokens <= tokenBudget)
                    break;

                builder.Length = contentLength;

                if (shown.Count == 0)
                    break;

                shown.RemoveAt(shown.Count - 1);
                lengths.RemoveAt(lengths.Count - 1);
                builder.Length = shown.Count > 0 ? lengths[^1] : 0;

                shownCounts = CountBySeverity(shown);
                omitted = filtered.Count - shown.Count;
                summary = BuildSummary(totals.Errors, totals.Warnings, omitted, filter);
            }
        }

        if (shown.Count == 0)
        {
            var noFit = "No diagnostics fit within token budget.";
            return BuildMessageResult(
                noFit,
                filesConsulted,
                tokenBudget,
                totalAvailable: filtered.Count,
                shown: 0,
                totalErrors: totals.Errors,
                totalWarnings: totals.Warnings,
                filter: filter,
                omitted: filtered.Count,
                summary: BuildSummary(totals.Errors, totals.Warnings, filtered.Count, filter));
        }

        var content = builder.ToString();
        var tokenCount = TokenEstimator.EstimateTokens(content);

        var metadata = BuildMetadata(filesConsulted, totals, shownCounts, filtered.Count, omitted, filter);

        return new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: filtered.Count,
            Shown: shown.Count,
            ExceedsBudget: false,
            Metadata: metadata);
    }

    private static ModifierResult BuildMessageResult(
        string message,
        IReadOnlyList<string> filesConsulted,
        int tokenBudget,
        int totalAvailable,
        int shown,
        int totalErrors,
        int totalWarnings,
        LintSeverityFilter filter,
        int omitted = 0,
        string? summary = null)
    {
        var content = message;
        if (!string.IsNullOrWhiteSpace(summary))
            content = $"{message}\n\n{summary}";

        var tokenCount = TokenEstimator.EstimateTokens(content);
        var metadata = BuildMetadata(
            filesConsulted,
            new SeverityCounts(totalErrors, totalWarnings, 0, 0),
            new SeverityCounts(0, 0, 0, 0),
            totalAvailable,
            omitted,
            filter);

        return new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: shown,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: metadata);
    }

    private static ResultMetadata BuildMetadata(
        IReadOnlyList<string> filesConsulted,
        SeverityCounts totals,
        SeverityCounts shown,
        int totalAvailable,
        int omitted,
        LintSeverityFilter filter)
    {
        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["total_diagnostics"] = totalAvailable,
            ["shown_diagnostics"] = totalAvailable - omitted,
            ["omitted_diagnostics"] = omitted,
            ["total_errors"] = totals.Errors,
            ["total_warnings"] = totals.Warnings,
            ["shown_errors"] = shown.Errors,
            ["shown_warnings"] = shown.Warnings,
            ["filter"] = filter.ToString()
        };

        return new ResultMetadata(filesConsulted, Warning: null, Extra: extra);
    }

    private static LintSeverityFilter ParseFilter(string? parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return LintSeverityFilter.Default;

        var normalized = parameter.Trim().ToLowerInvariant();
        return normalized switch
        {
            "errors" => LintSeverityFilter.Errors,
            "warnings" => LintSeverityFilter.Warnings,
            _ => throw new ArgumentException(
                "lint modifier parameters must be 'errors' or 'warnings'.",
                nameof(parameter))
        };
    }

    private static bool ShouldInclude(LintAnnotation annotation, LintSeverityFilter filter)
    {
        var severity = NormalizeSeverity(annotation.Severity);
        return filter switch
        {
            LintSeverityFilter.Errors => severity == "error",
            LintSeverityFilter.Warnings => severity == "warning",
            _ => severity is "error" or "warning"
        };
    }

    private static string FormatDiagnostic(
        LintAnnotation annotation,
        string fileUri,
        bool includeFileHeader,
        string? snippet)
    {
        var header = new StringBuilder();
        if (includeFileHeader && !string.IsNullOrWhiteSpace(fileUri))
        {
            header.Append(fileUri);
            header.Append('\n');
        }

        header.Append("  ");
        if (annotation.LineStart.HasValue)
        {
            header.Append("#line=");
            header.Append(annotation.LineStart.Value);
            header.Append(' ');
        }

        header.Append('[');
        header.Append(NormalizeSeverity(annotation.Severity));
        header.Append(']');

        var code = !string.IsNullOrWhiteSpace(annotation.RuleId)
            ? annotation.RuleId
            : annotation.Source;
        if (!string.IsNullOrWhiteSpace(code))
        {
            header.Append(' ');
            header.Append(code);
        }

        var message = annotation.Message?.TrimEnd();
        if (string.IsNullOrWhiteSpace(message))
            return AppendSnippet(header, snippet);

        var builder = new StringBuilder();
        builder.Append(header);

        var lines = message.Split('\n', StringSplitOptions.None);
        foreach (var raw in lines)
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            builder.Append('\n');
            builder.Append("    ");
            builder.Append(line);
        }

        return AppendSnippet(builder, snippet);
    }

    private static string BuildSummary(
        int totalErrors,
        int totalWarnings,
        int omitted,
        LintSeverityFilter filter)
    {
        var errorsLabel = totalErrors == 1 ? "error" : "errors";
        var warningsLabel = totalWarnings == 1 ? "warning" : "warnings";
        var summary = $"[{totalErrors} {errorsLabel}, {totalWarnings} {warningsLabel} in scope";

        var filtered = filter switch
        {
            LintSeverityFilter.Errors => totalWarnings,
            LintSeverityFilter.Warnings => totalErrors,
            _ => 0
        };

        if (filtered > 0)
        {
            var filteredLabel = filtered == 1 ? "diagnostic" : "diagnostics";
            summary += $" ({filtered} {filteredLabel} filtered)";
        }

        if (omitted > 0)
        {
            var omittedLabel = omitted == 1 ? "diagnostic" : "diagnostics";
            summary += $", {omitted} {omittedLabel} omitted";
        }

        summary += "]";
        return summary;
    }

    private static SeverityCounts CountBySeverity(IEnumerable<LintAnnotation> annotations)
    {
        var errors = 0;
        var warnings = 0;
        var infos = 0;
        var hints = 0;

        foreach (var annotation in annotations)
        {
            var severity = NormalizeSeverity(annotation.Severity);
            switch (severity)
            {
                case "error":
                    errors++;
                    break;
                case "warning":
                    warnings++;
                    break;
                case "info":
                    infos++;
                    break;
                case "hint":
                    hints++;
                    break;
            }
        }

        return new SeverityCounts(errors, warnings, infos, hints);
    }

    private static string NormalizeSeverity(string? severity)
        => string.IsNullOrWhiteSpace(severity) ? "unknown" : severity.Trim().ToLowerInvariant();

    private static string GetContainerUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return string.Empty;

        var hashIndex = uri.IndexOf('#');
        return hashIndex < 0 ? uri : uri[..hashIndex];
    }

    private static IReadOnlyDictionary<string, string> BuildFileTextLookup(IReadOnlyList<ReadDocument> documents)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.TextContent))
                continue;

            var container = GetContainerUri(doc.Uri);
            if (string.IsNullOrWhiteSpace(container))
                continue;

            if (!lookup.ContainsKey(container))
                lookup[container] = doc.TextContent!;
        }

        return lookup;
    }

    private static string ResolveFileUri(LintAnnotation annotation)
    {
        if (!string.IsNullOrWhiteSpace(annotation.FileUri))
            return annotation.FileUri;

        return GetContainerUri(annotation.ResolvedTargetUri);
    }

    private static bool TryAppend(StringBuilder builder, string block, bool newFileGroup, int tokenBudget)
    {
        var originalLength = builder.Length;
        if (originalLength > 0)
            builder.Append(newFileGroup ? "\n\n" : "\n");

        builder.Append(block);

        var tokens = TokenEstimator.EstimateTokens(builder.ToString());
        if (tokens <= tokenBudget)
            return true;

        builder.Length = originalLength;
        return false;
    }

    private static string? TryBuildSnippet(IReadOnlyDictionary<string, string> fileTextByUri, string fileUri, int? lineStart)
    {
        if (lineStart is null || lineStart <= 0)
            return null;

        if (!fileTextByUri.TryGetValue(fileUri, out var text) || string.IsNullOrWhiteSpace(text))
            return null;

        var lines = text.Split('\n', StringSplitOptions.None);
        if (lines.Length == 0)
            return null;

        var targetIndex = lineStart.Value - 1;
        if (targetIndex < 0 || targetIndex >= lines.Length)
            return null;

        var start = Math.Max(0, targetIndex - 1);
        var end = Math.Min(lines.Length - 1, targetIndex + 1);

        var snippet = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            var prefix = i == targetIndex ? '>' : ' ';
            var lineNumber = i + 1;
            var lineText = lines[i].TrimEnd('\r');
            snippet.Append("  ");
            snippet.Append(prefix);
            snippet.Append(lineNumber);
            snippet.Append(": ");
            snippet.Append(lineText);
            if (i < end)
                snippet.Append('\n');
        }

        return snippet.ToString();
    }

    private static string AppendSnippet(StringBuilder builder, string? snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
            return builder.ToString();

        builder.Append('\n');
        builder.Append('\n');
        builder.Append(snippet);
        return builder.ToString();
    }

    private enum LintSeverityFilter
    {
        Default,
        Errors,
        Warnings
    }

    private sealed record SeverityCounts(
        int Errors,
        int Warnings,
        int Infos,
        int Hints);
}

/// <summary>
/// Purpose: Supplies lint annotations for read modifiers from storage.
/// Complexity: Abstracts annotation retrieval so handlers stay format-focused.
/// </summary>
public interface ILintAnnotationProvider
{
    Task<IReadOnlyList<LintAnnotation>> GetLintAnnotationsAsync(IReadOnlyList<string> fileUris, CancellationToken ct);
}

/// <summary>
/// Purpose: Represents a resolved lint diagnostic with location and metadata for rendering.
/// Complexity: Bundles annotation attributes needed for ordering, filtering, and display.
/// </summary>
public sealed record LintAnnotation(
    string FileUri,
    string ResolvedTargetUri,
    string Severity,
    int SeverityRank,
    string Message,
    string? RuleId,
    string? Source,
    int? LineStart);
