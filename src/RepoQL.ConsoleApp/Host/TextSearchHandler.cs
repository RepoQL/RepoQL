using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Searches matched file content by literal text or regex as a read modifier output.
/// Complexity: Handles modifier-mode routing, line/context extraction, and token-budget fitting.
/// </summary>
internal sealed class TextSearchHandler : IModifierHandler
{
    private static readonly AsyncLocal<string?> RequestedModifier = new();

    public string ModifierName => "grep";

    public bool CanHandle(string? modifier)
    {
        if (string.Equals(modifier, "grep", StringComparison.OrdinalIgnoreCase))
        {
            RequestedModifier.Value = "grep";
            return true;
        }

        if (string.Equals(modifier, "regex", StringComparison.OrdinalIgnoreCase))
        {
            RequestedModifier.Value = "regex";
            return true;
        }

        return false;
    }

    public Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (documents.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No files matched.",
                filesConsulted: [],
                tokenBudget: tokenBudget));
        }

        var pattern = parameter?.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult(BuildSimpleResult(
                "Usage: `=> grep: <search term>` or `=> regex: <pattern>`",
                filesConsulted: [],
                tokenBudget: tokenBudget));
        }

        var mode = RequestedModifier.Value;
        var regexMode = string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase);

        Regex? regex = null;
        if (regexMode)
        {
            try
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(BuildSimpleResult(
                    $"Invalid regex pattern: {ex.Message}",
                    filesConsulted: [],
                    tokenBudget: tokenBudget));
            }
        }

        var consultedUris = ExtractConsultedUris(documents);
        if (consultedUris.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No valid URIs matched for text search.",
                filesConsulted: documents.Select(d => d.Uri).ToArray(),
                tokenBudget: tokenBudget));
        }

        var searchable = documents
            .Select(TryBuildSearchDocument)
            .Where(d => d is not null)
            .Cast<SearchDocument>()
            .ToList();

        if (searchable.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No indexed content available for matched files.",
                filesConsulted: consultedUris,
                tokenBudget: tokenBudget));
        }

        var matches = new List<LineMatch>();
        var filesWithMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in searchable)
        {
            ct.ThrowIfCancellationRequested();

            var lines = doc.TextContent.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var currentLine = lines[i].TrimEnd('\r');
                var isMatch = regexMode
                    ? regex!.IsMatch(currentLine)
                    : currentLine.Contains(pattern, StringComparison.OrdinalIgnoreCase);

                if (!isMatch)
                    continue;

                var lineNumber = i + 1;
                var before = i > 0 ? lines[i - 1].TrimEnd('\r') : null;
                var after = i + 1 < lines.Length ? lines[i + 1].TrimEnd('\r') : null;
                matches.Add(new LineMatch(doc.Uri, lineNumber, currentLine, before, after));
                filesWithMatches.Add(doc.Uri);
            }
        }

        if (matches.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                $"No matches for '{pattern}' in {searchable.Count.ToString(CultureInfo.InvariantCulture)} files.",
                filesConsulted: consultedUris,
                tokenBudget: tokenBudget));
        }

        var rendered = FitToBudget(
            matches,
            filesWithMatches.Count,
            searchable.Count,
            tokenBudget,
            ct);

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["mode"] = regexMode ? "regex" : "grep",
            ["pattern"] = pattern,
            ["matches_found"] = matches.Count,
            ["matches_shown"] = rendered.Shown,
            ["files_with_matches"] = filesWithMatches.Count,
            ["files_searched"] = searchable.Count
        };

        return Task.FromResult(new ModifierResult(
            Content: rendered.Content,
            TokenCount: rendered.TokenCount,
            TotalAvailable: matches.Count,
            Shown: rendered.Shown,
            ExceedsBudget: rendered.TokenCount > tokenBudget,
            Metadata: new ResultMetadata(consultedUris, rendered.Warning, extra)));
    }

    private static ModifierResult BuildSimpleResult(
        string message,
        IReadOnlyList<string> filesConsulted,
        int tokenBudget,
        int totalAvailable = 0,
        int shown = 0,
        string? warning = null)
    {
        var tokenCount = TokenEstimator.EstimateTokens(message);
        return new ModifierResult(
            Content: message,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: shown,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(filesConsulted, warning, new Dictionary<string, object>()));
    }

    private static IReadOnlyList<string> ExtractConsultedUris(IReadOnlyList<ReadDocument> documents)
    {
        var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Uri))
                continue;

            if (!RepoUri.TryParse(doc.Uri, out var repoUri))
                continue;

            uris.Add(repoUri.Container.AbsoluteUri);
        }

        return uris.ToList();
    }

    private static SearchDocument? TryBuildSearchDocument(ReadDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.TextContent))
            return null;

        if (!RepoUri.TryParse(document.Uri, out var repoUri))
            return null;

        return new SearchDocument(repoUri.Container.AbsoluteUri, document.TextContent!);
    }

    private static BudgetFitResult FitToBudget(
        IReadOnlyList<LineMatch> matches,
        int filesWithMatches,
        int filesSearched,
        int tokenBudget,
        CancellationToken ct)
    {
        var sections = matches.Select(FormatMatch).ToArray();
        var selected = new List<string>(sections.Length);
        var shown = 0;

        for (var i = 0; i < sections.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var candidateShown = shown + 1;
            var candidate = BuildRenderedOutput(
                selected.Append(sections[i]),
                candidateShown,
                matches.Count,
                filesWithMatches,
                filesSearched);

            var candidateTokens = TokenEstimator.EstimateTokens(candidate);
            if (shown == 0 || candidateTokens <= tokenBudget)
            {
                selected.Add(sections[i]);
                shown = candidateShown;
                continue;
            }

            break;
        }

        var content = BuildRenderedOutput(selected, shown, matches.Count, filesWithMatches, filesSearched);
        var tokenCount = TokenEstimator.EstimateTokens(content);
        var warning = shown < matches.Count ? "Output truncated to fit token budget." : null;

        return new BudgetFitResult(content, tokenCount, shown, warning);
    }

    private static string BuildRenderedOutput(
        IEnumerable<string> sections,
        int shown,
        int totalMatches,
        int filesWithMatches,
        int filesSearched)
    {
        var body = string.Join("\n\n", sections);
        var footer = BuildFooter(shown, totalMatches, filesWithMatches, filesSearched);

        if (string.IsNullOrEmpty(body))
            return footer;

        return $"{body}\n\n{footer}";
    }

    private static string BuildFooter(int shown, int totalMatches, int filesWithMatches, int filesSearched)
    {
        if (shown < totalMatches)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "[showing {0}/{1} matches in {2} files, {3} files searched]",
                shown,
                totalMatches,
                filesWithMatches,
                filesSearched);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "[{0} matches in {1} files, {2} files searched]",
            totalMatches,
            filesWithMatches,
            filesSearched);
    }

    private static string FormatMatch(LineMatch match)
    {
        var sb = new StringBuilder();
        sb.Append(match.Uri);
        sb.Append("#line=");
        sb.Append(match.LineNumber.ToString(CultureInfo.InvariantCulture));
        sb.Append('\n');

        if (match.ContextBefore is not null)
        {
            sb.Append(' ');
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0,4}: {1}",
                match.LineNumber - 1,
                match.ContextBefore);
            sb.Append('\n');
        }

        sb.Append(' ');
        sb.AppendFormat(
            CultureInfo.InvariantCulture,
            "{0,4}: {1}",
            match.LineNumber,
            match.LineContent);

        if (match.ContextAfter is not null)
        {
            sb.Append('\n');
            sb.Append(' ');
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "{0,4}: {1}",
                match.LineNumber + 1,
                match.ContextAfter);
        }

        return sb.ToString();
    }

    private sealed record SearchDocument(string Uri, string TextContent);

    private sealed record LineMatch(
        string Uri,
        int LineNumber,
        string LineContent,
        string? ContextBefore,
        string? ContextAfter);

    private sealed record BudgetFitResult(
        string Content,
        int TokenCount,
        int Shown,
        string? Warning);
}
