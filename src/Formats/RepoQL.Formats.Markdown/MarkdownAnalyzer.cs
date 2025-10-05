using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Formats.Markdown;

public sealed class MarkdownAnalyzer : IFormatAnalyzer
{
    private const string RuleId = "markdown/broken-link";
    private const string Source = "RepoQL.Markdown";

    public bool Supports(SemanticMediaType mediaType)
        => string.Equals(mediaType.Kind, "markdown.doc", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(DocumentModel document, AnalyzerContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        if (!Supports(document.MediaType)) yield break;

        var state = document.GetMetadata<MarkdownDocumentState>(MarkdownLoader.StateMetadataKey);
        if (state is null) yield break;

        var ruleSettings = context.Settings.GetRule(RuleId);
        if (ruleSettings.Severity == AnalysisSeverity.None)
            yield break;

        var localSlugs = new HashSet<string>(state.Surface.Headings.Select(h => h.Slug), StringComparer.OrdinalIgnoreCase);

        foreach (var link in state.Surface.Links)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            var href = link.Href?.Trim();
            if (string.IsNullOrEmpty(href))
                continue;

            if (HrefLooksExternal(href))
                continue;

            if (href.StartsWith('#'))
            {
                var slug = MarkdownTextUtilities.Slug(href.TrimStart('#'));
                if (!localSlugs.Contains(slug))
                    yield return BuildResult(document, link, ruleSettings.Severity, $"Anchor '#{slug}' not found");
                continue;
            }

            if (!TryResolveLink(document.Uri.AbsoluteUri, href, out var targetContainer, out var anchor))
            {
                yield return BuildResult(document, link, ruleSettings.Severity, $"Unable to resolve link '{href}'");
                continue;
            }

            var targetDoc = await context.Workspace.LoadAsync(targetContainer, cancellationToken).ConfigureAwait(false);
            if (targetDoc is null)
            {
                yield return BuildResult(document, link, ruleSettings.Severity, $"Target document '{targetContainer.AbsolutePath}' not found");
                continue;
            }

            if (string.IsNullOrEmpty(anchor))
                continue;

            var targetState = targetDoc.GetMetadata<MarkdownDocumentState>(MarkdownLoader.StateMetadataKey);
            if (targetState is null)
            {
                yield return BuildResult(document, link, ruleSettings.Severity, $"Target document '{targetContainer.AbsolutePath}' missing markdown structure");
                continue;
            }

            var anchorSlug = MarkdownTextUtilities.Slug(anchor);
            var exists = targetState.Surface.Headings.Any(h => string.Equals(h.Slug, anchorSlug, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                yield return BuildResult(document, link, ruleSettings.Severity,
                    $"Anchor '#{anchorSlug}' not found in '{targetContainer.AbsolutePath}'");
            }
        }

        await foreach (var embedded in AnalyzeEmbeddedAsyncInternal(document, state, context, cancellationToken).ConfigureAwait(false))
        {
            yield return embedded;
        }
    }

    private async IAsyncEnumerable<AnalysisResult> AnalyzeEmbeddedAsyncInternal(
        DocumentModel document,
        MarkdownDocumentState state,
        AnalyzerContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var block in state.Surface.CodeBlocks)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            if (string.IsNullOrWhiteSpace(block.Language))
                continue;

            var label = block.Language.Trim().ToLowerInvariant();
            if (!context.Formats.TryResolveByLabel(label, out var descriptor))
                continue;

            if (!descriptor.Analyzer.Supports(descriptor.MediaType))
                continue;

            var text = SafeSubstring(document.Text, block.Span.StartChar, block.Span.Length);
            var fragment = new EmbeddedFragment(document.Uri, label, descriptor.MediaType, text, block.Span.StartChar, block.Span.Length, block.NodeId, block.SpanId);

            await foreach (var result in descriptor.Analyzer.AnalyzeEmbeddedAsync(fragment, context, cancellationToken).ConfigureAwait(false))
            {
                yield return RemapEmbeddedResult(document, fragment, result);
            }
        }
    }

    private static AnalysisResult BuildResult(DocumentModel document, LinkInfo link, AnalysisSeverity severity, string message)
        => new()
        {
            SemanticKey = $"{document.Uri}#rule:{RuleId}@node:{link.NodeId}",
            RuleId = RuleId,
            Source = Source,
            Kind = "lint",
            Severity = severity,
            Message = message,
            Data = new JsonObject { ["href"] = link.Href },
            Target = new AnalysisTarget
            {
                NodeId = link.NodeId,
                SpanId = link.SpanId,
                TargetUri = document.Uri.AbsoluteUri
            }
        };

    private static bool HrefLooksExternal(string href)
        => href.Contains("://", StringComparison.OrdinalIgnoreCase)
           || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
           || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase);

    private static string SafeSubstring(string text, int start, int length)
    {
        if (length <= 0 || start >= text.Length) return string.Empty;
        if (start < 0) start = 0;
        if (start + length > text.Length) length = text.Length - start;
        return text.Substring(start, length);
    }

    private static bool TryResolveLink(string containerUri, string href, out RepoUri targetContainer, out string? anchor)
    {
        targetContainer = null!;
        anchor = null;
        try
        {
            var baseUri = new Uri(containerUri);
            var baseContainer = new Uri(baseUri.GetLeftPart(UriPartial.Path));
            var resolved = new Uri(baseContainer, href);

            var containerPart = resolved.GetLeftPart(UriPartial.Path);
            targetContainer = RepoUri.Parse(containerPart);

            anchor = resolved.Fragment.Length > 1
                ? Uri.UnescapeDataString(resolved.Fragment[1..])
                : null;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AnalysisResult RemapEmbeddedResult(DocumentModel parent, EmbeddedFragment fragment, AnalysisResult child)
    {
        IReadOnlyList<AnalysisFix>? fixes = null;
        if (child.Fixes is { Count: > 0 })
        {
            var mapped = new List<AnalysisFix>(child.Fixes.Count);
            foreach (var fix in child.Fixes)
            {
                var replacements = fix.Replacements?
                    .Select(rep => RemapReplacement(parent, fragment, rep))
                    .Where(r => r is not null)
                    .Cast<AnalysisReplacement>()
                    .ToList()
                    ?? new List<AnalysisReplacement>();

                mapped.Add(new AnalysisFix
                {
                    Description = fix.Description,
                    Uri = parent.Uri.AbsoluteUri,
                    Replacements = replacements
                });
            }
            fixes = mapped;
        }

        var target = new AnalysisTarget
        {
            NodeId = fragment.ParentNodeId ?? child.Target?.NodeId,
            EdgeId = child.Target?.EdgeId,
            SpanId = fragment.ParentSpanId ?? child.Target?.SpanId,
            TargetUri = parent.Uri.AbsoluteUri
        };

        return new AnalysisResult
        {
            SemanticKey = $"{parent.Uri}#embed:{child.SemanticKey}",
            RuleId = child.RuleId,
            Source = child.Source,
            Kind = child.Kind,
            Severity = child.Severity,
            Message = child.Message,
            Data = child.Data,
            Target = target,
            Fixes = fixes,
            AutoFixable = fixes is { Count: > 0 } && fixes.All(f => f.Replacements is { Count: > 0 })
        };
    }

    private static AnalysisReplacement? RemapReplacement(DocumentModel parent, EmbeddedFragment fragment, AnalysisReplacement replacement)
    {
        if (replacement.Region is null)
            return null;

        var startChar = replacement.Region.StartChar ?? 0;
        var endChar = replacement.Region.EndChar ?? startChar;
        var span = fragment.MapToParent(parent, startChar, endChar - startChar);
        return new AnalysisReplacement
        {
            NewText = replacement.NewText,
            Region = new AnalysisRegion
            {
                StartChar = span.StartChar,
                EndChar = span.EndChar,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn
            }
        };
    }
}
